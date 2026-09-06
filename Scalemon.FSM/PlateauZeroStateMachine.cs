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
    private decimal _divisionKg = 0.01m;
    private int _consecutiveFreshUnloadSamples;
    private int _zeroCommandAttempts;
    private int _tareCommandAttempts;
    private int _automaticZeroApplications;
    private int _automaticTareApplications;

    private enum CorrectionKind
    {
        SetZeroNegative,
        SetZeroPositive,
        SetTarePositive,
        CleaningRequired
    }

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
            "Plateau={PlateauSamples}x/{PlateauWindow}kg, Unload={UnloadSamples}x, " +
            "CommandTimeout={Timeout}ms, ZeroAttempts={ZeroAttempts}, TareAttempts={TareAttempts}, " +
            "MaxZeroApplications={MaxZeroApplications}, MaxTareApplications={MaxTareApplications}, " +
            "NonBlockingCorrection={NonBlockingCorrection}",
            _settings.MinWeight,
            _settings.ZeroResidualMaxKg,
            _settings.TareMaxKg,
            _settings.PlateauStableSamples,
            _settings.PlateauWindowKg,
            _settings.UnloadStableSamples,
            _settings.CommandTimeoutMs,
            _settings.ZeroCommandMaxAttempts,
            _settings.TareCommandMaxAttempts,
            _settings.MaxAutomaticZeroApplications,
            _settings.MaxAutomaticTareApplications,
            _settings.EnableNonBlockingCorrection);
    }

    /// <summary>
    /// Возвращает текущую рабочую фазу автомата.
    /// </summary>
    public FsmState CurrentState => _state;

    /// <summary>
    /// Показывает, что технологическая ошибка коррекции защёлкнута до очистки платформы
    /// и подтверждения стабильного нуля.
    /// </summary>
    public bool HasLatchedProcessError => _redLatched;

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
        _consecutiveFreshUnloadSamples = 0;

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
        _consecutiveFreshUnloadSamples = 0;

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

        if (sample.DivisionKg > 0m)
            _divisionKg = sample.DivisionKg;

        if (_cycleRecorded)
        {
            await HandleAwaitUnloadAsync(sample);
            return;
        }

        if (!sample.IsStable)
        {
            _plateauSamples.Clear();
            _unloadSamples.Clear();
            return;
        }

        if (_waitingForPostCommandZero)
        {
            await HandlePostCommandConfirmationAsync(sample);
            return;
        }

        if (_redLatched)
        {
            await HandleLatchedProcessErrorAsync(sample);
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
        if (sample.WeightKg >= (decimal)_settings.MinWeight)
        {
            _unloadSamples.Clear();
            await AddPlateauSampleAsync(sample.WeightKg);
            return;
        }

        _plateauSamples.Clear();
        if (sample.IsTerminalZero || sample.WeightKg == 0m)
        {
            _unloadSamples.Clear();
            // После автоматической коррекции терминал продолжает передавать ноль.
            // Счётчик успешных применений сохраняется до следующего груза, иначе
            // ограничение можно было бы обойти одним дополнительным нулевым кадром.
            ResetCorrectionCounters(resetApplications: false);
            Transition(FsmState.IdleZero);
            return;
        }

        Transition(FsmState.Weighing);
        await AddUnloadSampleAsync(sample);
    }

    private async Task HandleUnarmedAsync(ScaleDataPoint sample)
    {
        if (sample.WeightKg >= (decimal)_settings.MinWeight)
        {
            _armed = true;
            _unloadSamples.Clear();
            await AddPlateauSampleAsync(sample.WeightKg);
            return;
        }

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

        if (!_redLatched)
        {
            // Новое подтверждённое взвешивание начинает отдельный цикл защиты
            // от бесконечных автоматических установок ZERO/TARE.
            ResetCorrectionCounters(resetApplications: true);
        }

        // Блокировка цикла устанавливается до обращения к БД: ошибка записи не должна создавать дубль.
        _cycleRecorded = true;
        _armed = false;
        _consecutiveFreshUnloadSamples = 0;
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
        var tareMaxKg = (decimal)_settings.TareMaxKg;
        if (Math.Abs(sample.WeightKg) > tareMaxKg)
        {
            _consecutiveFreshUnloadSamples = 0;

            // Валидная положительная масса означает, что груз ещё не снят. Стабильное
            // значение ниже MinWeight, но выше TareMax, уже является остатком.
            if (!sample.IsStable || sample.WeightKg >= (decimal)_settings.MinWeight)
            {
                _unloadSamples.Clear();
                Transition(FsmState.AwaitUnload);
                return;
            }

            AddToStableWindow(
                _unloadSamples,
                sample.WeightKg,
                _settings.UnloadStableSamples,
                (decimal)_settings.PlateauWindowKg);
            if (_unloadSamples.Count < _settings.UnloadStableSamples)
            {
                Transition(FsmState.AwaitUnload);
                return;
            }

            var residualKg = Median(_unloadSamples);
            _unloadSamples.Clear();
            _cycleRecorded = false;
            _armed = true;
            EnterProcessError(
                residualKg,
                "Остаточный вес превышает верхнюю границу автоматического TARE");
            return;
        }

        _consecutiveFreshUnloadSamples++;
        if (sample.IsStable)
        {
            AddToStableWindow(
                _unloadSamples,
                sample.WeightKg,
                _settings.UnloadStableSamples,
                (decimal)_settings.PlateauWindowKg);
        }
        else
        {
            _unloadSamples.Clear();
        }

        if (_consecutiveFreshUnloadSamples < 2)
        {
            Transition(FsmState.AwaitUnload);
            return;
        }

        _consecutiveFreshUnloadSamples = 0;
        _cycleRecorded = false;
        _armed = true;
        _plateauSamples.Clear();
        _log.LogDebug(
            "Разгрузка подтверждена по двум свежим показаниям в диапазоне ±{TareMax:0.###} кг",
            tareMaxKg);

        if (sample.IsStable && _unloadSamples.Count >= _settings.UnloadStableSamples)
        {
            var residualKg = Median(_unloadSamples);
            _unloadSamples.Clear();
            await HandleConfirmedResidualAsync(residualKg, sample.IsTerminalZero);
            return;
        }

        Transition(_redLatched ? FsmState.InvalidWeightState : FsmState.Weighing);
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
        _armed = true;

        if (_redLatched)
        {
            HandleLatchedResidual(residualKg, terminalZero);
            return;
        }

        if (terminalZero || residualKg == 0m)
        {
            CompleteRecovery(resetAutomaticApplications: true);
            return;
        }

        var correctionKind = ClassifyCorrection(
            residualKg,
            (decimal)_settings.ZeroResidualMaxKg,
            (decimal)_settings.TareMaxKg);

        switch (correctionKind)
        {
            case CorrectionKind.CleaningRequired:
                EnterProcessError(residualKg, "Остаточный вес требует очистки платформы");
                return;
            case CorrectionKind.SetZeroNegative:
            case CorrectionKind.SetZeroPositive:
                await TrySetZeroAsync(residualKg);
                return;
            case CorrectionKind.SetTarePositive:
                await TrySetTareAsync(residualKg);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(correctionKind));
        }
    }

    private async Task TrySetZeroAsync(decimal residualKg)
    {
        if (_automaticZeroApplications >= _settings.MaxAutomaticZeroApplications)
        {
            EnterProcessError(
                residualKg,
                $"Превышен лимит автоматических установок ZERO ({_settings.MaxAutomaticZeroApplications})");
            return;
        }

        if (_zeroCommandAttempts >= _settings.ZeroCommandMaxAttempts)
        {
            EnterProcessError(
                residualKg,
                $"Команда ZERO не выполнена за {_settings.ZeroCommandMaxAttempts} попыток");
            return;
        }

        _zeroCommandAttempts++;
        var result = await SendCorrectionAsync(
            new ResidualCorrectionRequest(ResidualCorrectionCommand.SetZero));
        if (result.Succeeded)
        {
            _zeroCommandAttempts = 0;
            _tareCommandAttempts = 0;
            _automaticZeroApplications++;
            BeginPostCommandConfirmation();
            return;
        }

        HandleCorrectionAttemptFailure(
            result,
            residualKg,
            ResidualCorrectionCommand.SetZero,
            _zeroCommandAttempts,
            _settings.ZeroCommandMaxAttempts);
    }

    private async Task TrySetTareAsync(decimal residualKg)
    {
        if (_automaticTareApplications >= _settings.MaxAutomaticTareApplications)
        {
            EnterProcessError(
                residualKg,
                $"Превышен лимит автоматических установок TARE ({_settings.MaxAutomaticTareApplications})");
            return;
        }

        if (_tareCommandAttempts >= _settings.TareCommandMaxAttempts)
        {
            EnterProcessError(
                residualKg,
                $"Команда TARE не выполнена за {_settings.TareCommandMaxAttempts} попыток");
            return;
        }

        _tareCommandAttempts++;
        var result = await SendCorrectionAsync(
            new ResidualCorrectionRequest(ResidualCorrectionCommand.SetTare, residualKg));
        if (result.Succeeded)
        {
            _zeroCommandAttempts = 0;
            _tareCommandAttempts = 0;
            _automaticTareApplications++;
            BeginPostCommandConfirmation();
            return;
        }

        HandleCorrectionAttemptFailure(
            result,
            residualKg,
            ResidualCorrectionCommand.SetTare,
            _tareCommandAttempts,
            _settings.TareCommandMaxAttempts);
    }

    private void HandleCorrectionAttemptFailure(
        ScaleCommandResult result,
        decimal residualKg,
        ResidualCorrectionCommand command,
        int attempt,
        int maxAttempts)
    {
        _waitingForPostCommandZero = false;
        _armed = true;
        _plateauSamples.Clear();
        _unloadSamples.Clear();

        if (result.IsTransportError)
        {
            _connected = false;
            _log.LogWarning(
                "Ошибка связи при {Command}: попытка {Attempt}/{MaxAttempts}, Weight={Weight:0.###}kg, Error={Error}",
                command,
                attempt,
                maxAttempts,
                residualKg,
                result.ResponseText);

            if (attempt >= maxAttempts)
            {
                EnterProcessError(
                    residualKg,
                    $"Команда {command} не выполнена за {maxAttempts} попыток");
            }

            Transition(FsmState.Disconnected);
            return;
        }

        if (attempt < maxAttempts)
        {
            _log.LogDebug(
                "Терминал отклонил {Command}: попытка {Attempt}/{MaxAttempts}, " +
                "Weight={Weight:0.###}kg, Error={Error} (code={Code}); повтор после нового стабильного остатка",
                command,
                attempt,
                maxAttempts,
                residualKg,
                result.ResponseText,
                result.ResponseCode);
            Transition(FsmState.Weighing);
            return;
        }

        EnterProcessError(
            residualKg,
            $"Терминал отклонил {command} {maxAttempts} раз: {result.ResponseText} " +
            $"(code={result.ResponseCode})");
    }

    private async Task<ScaleCommandResult> SendCorrectionAsync(ResidualCorrectionRequest request)
    {
        return await _correctResidualAsync(request, CancellationToken.None);
    }

    private void BeginPostCommandConfirmation()
    {
        _waitingForPostCommandZero = true;
        _armed = true;
        _unloadSamples.Clear();
        Transition(FsmState.Weighing);
    }

    private async Task HandlePostCommandConfirmationAsync(ScaleDataPoint sample)
    {
        if (sample.WeightKg >= (decimal)_settings.MinWeight)
        {
            _waitingForPostCommandZero = false;
            _armed = true;
            _unloadSamples.Clear();
            _log.LogInformation(
                "Подтверждение коррекции прервано новым грузом {Weight:0.###} кг; автофиксация продолжена",
                sample.WeightKg);
            await AddPlateauSampleAsync(sample.WeightKg);
            return;
        }

        AddToStableWindow(
            _unloadSamples,
            sample.WeightKg,
            _settings.UnloadStableSamples,
            (decimal)_settings.PlateauWindowKg);
        Transition(FsmState.Weighing);

        if (_unloadSamples.Count < _settings.UnloadStableSamples)
            return;

        var residualKg = Median(_unloadSamples);
        _unloadSamples.Clear();
        _waitingForPostCommandZero = false;

        if (sample.IsTerminalZero || residualKg == 0m)
        {
            // Ноль получен в ответ на автоматическую команду: счётчики успешных
            // применений сохраняются, чтобы повторное появление воды имело предел.
            CompleteRecovery(resetAutomaticApplications: false);
            return;
        }

        await HandleConfirmedResidualAsync(residualKg, terminalZero: false);
    }

    private async Task HandleLatchedProcessErrorAsync(ScaleDataPoint sample)
    {
        if (sample.WeightKg >= (decimal)_settings.MinWeight)
        {
            _unloadSamples.Clear();
            if (_settings.EnableNonBlockingCorrection)
            {
                await AddPlateauSampleAsync(sample.WeightKg);
            }
            else
            {
                Transition(FsmState.InvalidWeightState);
            }
            return;
        }

        _plateauSamples.Clear();
        Transition(FsmState.InvalidWeightState);
        AddToStableWindow(
            _unloadSamples,
            sample.WeightKg,
            _settings.UnloadStableSamples,
            (decimal)_settings.PlateauWindowKg);

        if (_unloadSamples.Count < _settings.UnloadStableSamples)
            return;

        var residualKg = Median(_unloadSamples);
        _unloadSamples.Clear();
        HandleLatchedResidual(residualKg, sample.IsTerminalZero);
    }

    private void HandleLatchedResidual(decimal residualKg, bool terminalZero)
    {
        if (terminalZero || residualKg == 0m)
        {
            CompleteRecovery(resetAutomaticApplications: true);
            return;
        }

        Transition(FsmState.InvalidWeightState);
    }

    private void EnterProcessError(decimal residualKg, string reason)
    {
        var wasLatched = _redLatched;
        _redLatched = true;
        _waitingForPostCommandZero = false;
        _armed = _settings.EnableNonBlockingCorrection;
        _plateauSamples.Clear();
        _unloadSamples.Clear();
        Transition(FsmState.InvalidWeightState);

        if (!wasLatched)
        {
            _log.LogError(
                "{Reason}. Weight={Weight:0.###}kg. Автокоррекция остановлена до очистки платформы и стабильного нуля",
                reason,
                residualKg);
        }
    }

    private void CompleteRecovery(bool resetAutomaticApplications)
    {
        if (_redLatched)
        {
            _log.LogInformation(
                "Стабильный ноль подтверждён; технологическая ошибка снята после очистки/ручного обнуления");
        }

        _redLatched = false;
        _waitingForPostCommandZero = false;
        _cycleRecorded = false;
        _armed = true;
        _plateauSamples.Clear();
        _unloadSamples.Clear();
        ResetCorrectionCounters(resetAutomaticApplications);
        Transition(FsmState.IdleZero);
    }

    private void ResetCorrectionCounters(bool resetApplications)
    {
        _zeroCommandAttempts = 0;
        _tareCommandAttempts = 0;
        if (!resetApplications)
            return;

        _automaticZeroApplications = 0;
        _automaticTareApplications = 0;
    }

    private static CorrectionKind ClassifyCorrection(
        decimal residualKg,
        decimal zeroMaxKg,
        decimal tareMaxKg)
    {
        if (Math.Abs(residualKg) > tareMaxKg)
            return CorrectionKind.CleaningRequired;
        if (residualKg < 0m)
            return CorrectionKind.SetZeroNegative;
        if (residualKg <= zeroMaxKg)
            return CorrectionKind.SetZeroPositive;
        return CorrectionKind.SetTarePositive;
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
