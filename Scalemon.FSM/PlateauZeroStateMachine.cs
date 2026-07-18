using Microsoft.Extensions.Logging;
using Scalemon.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static Scalemon.Common.Enums;

namespace Scalemon.FSM;

/// <summary>
/// FSM автоматической фиксации: стабильное плато → немедленная запись → подтверждённая разгрузка.
/// </summary>
public sealed class PlateauZeroStateMachine
{
    private readonly SystemSettings _settings;
    private readonly ILogger _log;
    private readonly Func<decimal, Task> _onRecordAsync;
    private readonly Func<ResidualCorrectionRequest, CancellationToken, Task<ScaleCommandResult>> _correctResidualAsync;
    private readonly List<decimal> _plateauSamples = new();
    private readonly List<decimal> _unloadSamples = new();

    private FsmState _state = FsmState.Disconnected;
    private bool _connected;
    private bool _alarm;
    private bool _armed;
    private bool _cycleRecorded;
    private bool _waitingForPostCommandZero;
    private bool _redLatched;
    private bool _correctionAllowedAfterChange;
    private bool _suppressIdleCorrectionUntilLoad;
    private decimal _blockedAtWeightKg;
    private decimal _divisionKg = 0.01m;

    /// <summary>
    /// Создаёт конечный автомат автоматической фиксации веса.
    /// </summary>
    public PlateauZeroStateMachine(
        SystemSettings settings,
        ILogger logger,
        Func<decimal, Task> onRecordAsync,
        Func<ResidualCorrectionRequest, CancellationToken, Task<ScaleCommandResult>> correctResidualAsync)
    {
        settings.ValidateWeighing();
        _settings = settings;
        _log = logger;
        _onRecordAsync = onRecordAsync;
        _correctResidualAsync = correctResidualAsync;

        _log.LogInformation(
            "FSM: Min={Min}kg, ZeroMax={ZeroMax}kg, TareMax={TareMax}kg, " +
            "Plateau={PlateauSamples}x/{PlateauWindow}kg, Unload={UnloadSamples}x, CommandTimeout={Timeout}ms",
            _settings.MinWeight,
            _settings.ZeroResidualMaxKg,
            _settings.TareMaxKg,
            _settings.PlateauStableSamples,
            _settings.PlateauWindowKg,
            _settings.UnloadStableSamples,
            _settings.CommandTimeoutMs);
    }

    public FsmState CurrentState => _state;

    /// <summary>
    /// Обновляет подтверждённое состояние соединения с весами.
    /// </summary>
    public void SetConnection(bool isConnected)
    {
        if (_connected == isConnected)
            return;

        _connected = isConnected;
        _plateauSamples.Clear();
        _unloadSamples.Clear();

        if (!_connected)
        {
            Transition(FsmState.Disconnected);
            return;
        }

        _log.LogInformation("Связь с весами восстановлена");
    }

    /// <summary>
    /// Обновляет аппаратный аварийный статус весов.
    /// </summary>
    public Task SetAlarmAsync(bool isAlarm)
    {
        if (_alarm == isAlarm)
            return Task.CompletedTask;

        _alarm = isAlarm;
        _plateauSamples.Clear();
        _unloadSamples.Clear();

        if (_alarm)
        {
            Transition(FsmState.Alarm);
        }
        else
        {
            _log.LogInformation("Аварийный сигнал весов снят");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Обрабатывает очередной свежий ответ массы от терминала.
    /// </summary>
    public async Task OnSampleAsync(ScaleDataPoint sample)
    {
        if (!_connected)
        {
            Transition(FsmState.Disconnected);
            return;
        }

        if (_alarm)
        {
            Transition(FsmState.Alarm);
            return;
        }

        if (!sample.HasFreshMeasurement)
            return;

        if (!sample.IsStable)
        {
            _plateauSamples.Clear();
            _unloadSamples.Clear();
            return;
        }

        if (sample.DivisionKg > 0m)
            _divisionKg = sample.DivisionKg;

        if (_cycleRecorded)
        {
            await HandleAwaitUnloadAsync(sample);
            return;
        }

        if (_waitingForPostCommandZero)
        {
            await HandlePostCommandConfirmationAsync(sample);
            return;
        }

        if (_redLatched)
        {
            await HandleBlockedAsync(sample);
            return;
        }

        if (!_armed)
        {
            await HandleUnarmedAsync(sample);
            return;
        }

        await HandleArmedAsync(sample);
    }

    private async Task HandleArmedAsync(ScaleDataPoint sample)
    {
        var minWeightKg = (decimal)_settings.MinWeight;
        if (sample.WeightKg >= minWeightKg)
        {
            _suppressIdleCorrectionUntilLoad = false;
            _unloadSamples.Clear();
            await AddPlateauSampleAsync(sample.WeightKg);
            return;
        }

        _plateauSamples.Clear();
        if (sample.IsTerminalZero ||
            sample.WeightKg == 0m ||
            (_suppressIdleCorrectionUntilLoad && sample.WeightKg > 0m))
        {
            _unloadSamples.Clear();
            Transition(FsmState.IdleZero);
            return;
        }

        Transition(FsmState.Weighing);
        await AddUnloadSampleAsync(sample);
    }

    private async Task HandleUnarmedAsync(ScaleDataPoint sample)
    {
        _plateauSamples.Clear();
        Transition(FsmState.Weighing);
        await AddUnloadSampleAsync(sample);
    }

    private async Task AddPlateauSampleAsync(decimal weightKg)
    {
        AddToStableWindow(
            _plateauSamples,
            weightKg,
            _settings.PlateauStableSamples,
            (decimal)_settings.PlateauWindowKg);
        Transition(FsmState.Weighing);

        if (_plateauSamples.Count < _settings.PlateauStableSamples)
            return;

        var recordedWeight = RoundToDivision(Median(_plateauSamples));
        _plateauSamples.Clear();
        if (recordedWeight < (decimal)_settings.MinWeight)
            return;

        // Блокировка цикла устанавливается до обращения к БД: ошибка записи не должна создавать дубль.
        _cycleRecorded = true;
        _armed = false;
        Transition(FsmState.AwaitUnload);

        try
        {
            _log.LogInformation(
                "Запись подтверждённого плато: Weight={Weight:0.###}kg, Division={Division:0.####}kg, Samples={Samples}",
                recordedWeight,
                _divisionKg,
                _settings.PlateauStableSamples);
            await _onRecordAsync(recordedWeight);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ошибка записи взвешивания в БД; автоматический повтор отключён во избежание дубля");
        }
    }

    private async Task HandleAwaitUnloadAsync(ScaleDataPoint sample)
    {
        if (sample.WeightKg >= (decimal)_settings.MinWeight)
        {
            _unloadSamples.Clear();
            Transition(FsmState.AwaitUnload);
            return;
        }

        await AddUnloadSampleAsync(sample);
    }

    private async Task AddUnloadSampleAsync(ScaleDataPoint sample)
    {
        AddToStableWindow(
            _unloadSamples,
            sample.WeightKg,
            _settings.UnloadStableSamples,
            (decimal)_settings.PlateauWindowKg);

        if (_unloadSamples.Count < _settings.UnloadStableSamples)
            return;

        var residualKg = Median(_unloadSamples);
        _unloadSamples.Clear();
        _cycleRecorded = false;
        await HandleConfirmedResidualAsync(residualKg, sample.IsTerminalZero);
    }

    private async Task HandleConfirmedResidualAsync(decimal residualKg, bool terminalZero)
    {
        _armed = false;
        var absoluteResidual = Math.Abs(residualKg);
        var zeroMaxKg = (decimal)_settings.ZeroResidualMaxKg;
        var tareMaxKg = (decimal)_settings.TareMaxKg;

        if (terminalZero || residualKg == 0m)
        {
            CompleteRecovery();
            return;
        }

        if (absoluteResidual > tareMaxKg)
        {
            EnterRedLatch(
                residualKg,
                "Остаточный вес требует очистки платформы");
            return;
        }

        ScaleCommandResult result;
        if (residualKg < 0m)
        {
            result = await SendCorrectionAsync(
                new ResidualCorrectionRequest(ResidualCorrectionCommand.TareCurrentWeight));
            if (!result.Succeeded)
            {
                HandleCorrectionFailure(result, residualKg, "TARE текущей отрицательной нагрузки");
                return;
            }

            result = await SendCorrectionAsync(
                new ResidualCorrectionRequest(ResidualCorrectionCommand.SetZero));
            if (!result.Succeeded)
            {
                HandleCorrectionFailure(result, residualKg, "ZERO после снятия отрицательной тары");
                return;
            }

            BeginPostCommandConfirmation();
            return;
        }

        if (absoluteResidual <= zeroMaxKg)
        {
            result = await SendCorrectionAsync(
                new ResidualCorrectionRequest(ResidualCorrectionCommand.SetZero));
            if (result.Succeeded)
            {
                BeginPostCommandConfirmation();
                return;
            }

            if (result.IsTransportError)
            {
                HandleCorrectionFailure(result, residualKg, "ZERO");
                return;
            }

            // Только явный отказ ZERO разрешает переход к явной установке тары.
            _log.LogInformation(
                "ZERO отклонён терминалом; выполняется один TARE остатка {Residual:0.###} кг",
                residualKg);
            result = await SendCorrectionAsync(
                new ResidualCorrectionRequest(ResidualCorrectionCommand.SetTare, residualKg));
            if (!result.Succeeded)
            {
                HandleCorrectionFailure(result, residualKg, "TARE после отказа ZERO");
                return;
            }

            BeginPostCommandConfirmation();
            return;
        }

        result = await SendCorrectionAsync(
            new ResidualCorrectionRequest(ResidualCorrectionCommand.SetTare, residualKg));
        if (!result.Succeeded)
        {
            HandleCorrectionFailure(result, residualKg, "TARE остатка");
            return;
        }

        BeginPostCommandConfirmation();
    }

    private async Task<ScaleCommandResult> SendCorrectionAsync(ResidualCorrectionRequest request)
    {
        return await _correctResidualAsync(request, CancellationToken.None);
    }

    private void HandleCorrectionFailure(
        ScaleCommandResult result,
        decimal residualKg,
        string operation)
    {
        if (result.IsTransportError)
        {
            _connected = false;
            _waitingForPostCommandZero = false;
            _redLatched = true;
            _correctionAllowedAfterChange = false;
            _blockedAtWeightKg = residualKg;
            _log.LogWarning(
                "Потеря связи при {Operation}: {Error}. Повторная команда не отправляется",
                operation,
                result.ResponseText);
            Transition(FsmState.Disconnected);
            return;
        }

        EnterRedLatch(
            residualKg,
            $"Терминал отклонил {operation}: {result.ResponseText} (code={result.ResponseCode})");
    }

    private void BeginPostCommandConfirmation()
    {
        _waitingForPostCommandZero = true;
        _correctionAllowedAfterChange = false;
        _unloadSamples.Clear();
        Transition(_redLatched ? FsmState.InvalidWeightState : FsmState.Weighing);
    }

    private async Task HandlePostCommandConfirmationAsync(ScaleDataPoint sample)
    {
        if (Math.Abs(sample.WeightKg) > (decimal)_settings.ZeroResidualMaxKg)
        {
            _waitingForPostCommandZero = false;
            EnterRedLatch(sample.WeightKg, "После команды не подтверждён околонулевой результат");
            return;
        }

        AddToStableWindow(
            _unloadSamples,
            sample.WeightKg,
            _settings.UnloadStableSamples,
            (decimal)_settings.PlateauWindowKg);
        Transition(_redLatched ? FsmState.InvalidWeightState : FsmState.Weighing);

        if (_unloadSamples.Count < _settings.UnloadStableSamples)
            return;

        _unloadSamples.Clear();
        CompleteRecovery(suppressIdleCorrectionUntilLoad: true);
        await Task.CompletedTask;
    }

    private async Task HandleBlockedAsync(ScaleDataPoint sample)
    {
        Transition(FsmState.InvalidWeightState);
        if (Math.Abs(sample.WeightKg - _blockedAtWeightKg) > (decimal)_settings.PlateauWindowKg)
            _correctionAllowedAfterChange = true;

        if (Math.Abs(sample.WeightKg) > (decimal)_settings.ZeroResidualMaxKg)
        {
            _unloadSamples.Clear();
            return;
        }

        AddToStableWindow(
            _unloadSamples,
            sample.WeightKg,
            _settings.UnloadStableSamples,
            (decimal)_settings.PlateauWindowKg);

        if (_unloadSamples.Count < _settings.UnloadStableSamples)
            return;

        var residualKg = Median(_unloadSamples);
        _unloadSamples.Clear();
        if (sample.IsTerminalZero || residualKg == 0m)
        {
            CompleteRecovery();
            return;
        }

        if (!_correctionAllowedAfterChange)
            return;

        _correctionAllowedAfterChange = false;
        await HandleConfirmedResidualAsync(residualKg, sample.IsTerminalZero);
    }

    private void EnterRedLatch(decimal residualKg, string reason)
    {
        var wasLatched = _redLatched;
        _redLatched = true;
        _waitingForPostCommandZero = false;
        _armed = false;
        _blockedAtWeightKg = residualKg;
        _correctionAllowedAfterChange = false;
        _plateauSamples.Clear();
        _unloadSamples.Clear();
        Transition(FsmState.InvalidWeightState);

        if (!wasLatched)
            _log.LogWarning("{Reason}. Weight={Weight:0.###}kg", reason, residualKg);
    }

    private void CompleteRecovery(bool suppressIdleCorrectionUntilLoad = false)
    {
        if (_redLatched)
            _log.LogInformation("Коррекция остатка подтверждена; красная сигнализация снята");

        _redLatched = false;
        _waitingForPostCommandZero = false;
        _correctionAllowedAfterChange = false;
        _suppressIdleCorrectionUntilLoad = suppressIdleCorrectionUntilLoad;
        _blockedAtWeightKg = 0m;
        _cycleRecorded = false;
        _armed = true;
        _plateauSamples.Clear();
        _unloadSamples.Clear();
        Transition(FsmState.IdleZero);
    }

    private static void AddToStableWindow(
        List<decimal> samples,
        decimal value,
        int requiredSamples,
        decimal maxSpreadKg)
    {
        samples.Add(value);
        while (samples.Count > 1 && samples.Max() - samples.Min() > maxSpreadKg)
            samples.RemoveAt(0);

        while (samples.Count > requiredSamples)
            samples.RemoveAt(0);
    }

    private decimal RoundToDivision(decimal value)
    {
        var division = _divisionKg > 0m ? _divisionKg : 0.01m;
        return Math.Round(value / division, 0, MidpointRounding.AwayFromZero) * division;
    }

    private static decimal Median(IReadOnlyCollection<decimal> samples)
    {
        var ordered = samples.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2m
            : ordered[middle];
    }

    private void Transition(FsmState next)
    {
        if (_state == next)
            return;

        var previous = _state;
        _state = next;
        _log.LogDebug("FSM: {Previous} → {Next}", previous, next);
    }
}
