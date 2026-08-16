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
    private CorrectionKind _warningCorrectionKind;
    private ResidualCorrectionCommand? _lastFailedCorrectionCommand;
    private bool _retryAfterConnectionRecovery;
    private int _consecutiveFreshUnloadSamples;

    private enum CorrectionKind
    {
        None,
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
            "CommandTimeout={Timeout}ms, NonBlockingCorrection={NonBlockingCorrection}",
            _settings.MinWeight,
            _settings.ZeroResidualMaxKg,
            _settings.TareMaxKg,
            _settings.PlateauStableSamples,
            _settings.PlateauWindowKg,
            _settings.UnloadStableSamples,
            _settings.CommandTimeoutMs,
            _settings.EnableNonBlockingCorrection);
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
        _consecutiveFreshUnloadSamples = 0;

        if (!_connected)
        {
            Transition(FsmState.Disconnected);
            return;
        }

        if (_settings.EnableNonBlockingCorrection &&
            _redLatched &&
            _lastFailedCorrectionCommand.HasValue)
        {
            _armed = true;
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
            (_suppressIdleCorrectionUntilLoad &&
             (_settings.EnableNonBlockingCorrection
                 ? Math.Abs(sample.WeightKg) <= (decimal)_settings.ZeroResidualMaxKg
                 : sample.WeightKg > 0m)))
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
        if (sample.WeightKg >= (decimal)_settings.MinWeight)
        {
            _armed = true;
            _suppressIdleCorrectionUntilLoad = false;
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
            _unloadSamples.Clear();
            Transition(FsmState.AwaitUnload);
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
        if (_settings.EnableNonBlockingCorrection)
        {
            await HandleConfirmedResidualNonBlockingAsync(residualKg, terminalZero);
            return;
        }

        await HandleConfirmedResidualLegacyAsync(residualKg, terminalZero);
    }

    private async Task HandleConfirmedResidualNonBlockingAsync(decimal residualKg, bool terminalZero)
    {
        _armed = true;
        var absoluteResidual = Math.Abs(residualKg);
        var zeroMaxKg = (decimal)_settings.ZeroResidualMaxKg;
        var tareMaxKg = (decimal)_settings.TareMaxKg;

        if (terminalZero || residualKg == 0m)
        {
            CompleteRecovery();
            return;
        }

        var correctionKind = ClassifyCorrection(residualKg, zeroMaxKg, tareMaxKg);
        if (!ShouldAttemptCorrection(residualKg, correctionKind))
        {
            _plateauSamples.Clear();
            _unloadSamples.Clear();
            Transition(FsmState.InvalidWeightState);
            return;
        }

        _retryAfterConnectionRecovery = false;
        if (correctionKind == CorrectionKind.CleaningRequired)
        {
            EnterCorrectionWarning(
                residualKg,
                correctionKind,
                failedCommand: null,
                reason: "Остаточный вес требует очистки платформы");
            return;
        }

        if (residualKg < 0m)
        {
            var negativeZeroResult = await SendCorrectionAsync(
                new ResidualCorrectionRequest(ResidualCorrectionCommand.SetZero));
            if (!negativeZeroResult.Succeeded)
            {
                HandleNonBlockingCorrectionFailure(
                    negativeZeroResult,
                    residualKg,
                    correctionKind,
                    ResidualCorrectionCommand.SetZero,
                    "ZERO отрицательного остатка");
                return;
            }

            MarkAcceptedCorrection(residualKg, correctionKind);
            BeginPostCommandConfirmation();
            return;
        }

        if (absoluteResidual <= zeroMaxKg)
        {
            var zeroResult = await SendCorrectionAsync(
                new ResidualCorrectionRequest(ResidualCorrectionCommand.SetZero));
            if (zeroResult.Succeeded)
            {
                MarkAcceptedCorrection(residualKg, correctionKind);
                BeginPostCommandConfirmation();
                return;
            }

            if (zeroResult.IsTransportError)
            {
                HandleNonBlockingCorrectionFailure(
                    zeroResult,
                    residualKg,
                    correctionKind,
                    ResidualCorrectionCommand.SetZero,
                    "ZERO");
                return;
            }

            _log.LogInformation(
                "ZERO отклонён терминалом; выполняется один TARE положительного остатка {Residual:0.###} кг",
                residualKg);
            var tareAfterZeroResult = await SendCorrectionAsync(
                new ResidualCorrectionRequest(ResidualCorrectionCommand.SetTare, residualKg));
            if (!tareAfterZeroResult.Succeeded)
            {
                HandleNonBlockingCorrectionFailure(
                    tareAfterZeroResult,
                    residualKg,
                    correctionKind,
                    ResidualCorrectionCommand.SetTare,
                    "TARE после отказа ZERO");
                return;
            }

            MarkAcceptedCorrection(residualKg, correctionKind);
            BeginPostCommandConfirmation();
            return;
        }

        var tareResult = await SendCorrectionAsync(
            new ResidualCorrectionRequest(ResidualCorrectionCommand.SetTare, residualKg));
        if (!tareResult.Succeeded)
        {
            HandleNonBlockingCorrectionFailure(
                tareResult,
                residualKg,
                correctionKind,
                ResidualCorrectionCommand.SetTare,
                "TARE остатка");
            return;
        }

        MarkAcceptedCorrection(residualKg, correctionKind);
        BeginPostCommandConfirmation();
    }

    private async Task HandleConfirmedResidualLegacyAsync(decimal residualKg, bool terminalZero)
    {
        // Прежний блокирующий алгоритм оставлен только как временный путь отката.
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
        if (_settings.EnableNonBlockingCorrection)
            _armed = true;
        _correctionAllowedAfterChange = false;
        _unloadSamples.Clear();
        Transition(_redLatched ? FsmState.InvalidWeightState : FsmState.Weighing);
    }

    private async Task HandlePostCommandConfirmationAsync(ScaleDataPoint sample)
    {
        if (_settings.EnableNonBlockingCorrection &&
            sample.WeightKg >= (decimal)_settings.MinWeight)
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

        if (Math.Abs(sample.WeightKg) > (decimal)_settings.ZeroResidualMaxKg)
        {
            _waitingForPostCommandZero = false;
            if (_settings.EnableNonBlockingCorrection)
            {
                var correctionKind = ClassifyCorrection(
                    sample.WeightKg,
                    (decimal)_settings.ZeroResidualMaxKg,
                    (decimal)_settings.TareMaxKg);
                EnterCorrectionWarning(
                    sample.WeightKg,
                    correctionKind,
                    failedCommand: null,
                    reason: "После команды не подтверждён околонулевой результат");
            }
            else
            {
                EnterRedLatch(sample.WeightKg, "После команды не подтверждён околонулевой результат");
            }
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
        if (_settings.EnableNonBlockingCorrection)
        {
            await HandleCorrectionWarningAsync(sample);
            return;
        }

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

    private async Task HandleCorrectionWarningAsync(ScaleDataPoint sample)
    {
        if (sample.WeightKg >= (decimal)_settings.MinWeight)
        {
            _suppressIdleCorrectionUntilLoad = false;
            _unloadSamples.Clear();
            await AddPlateauSampleAsync(sample.WeightKg);
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
        await HandleConfirmedResidualAsync(residualKg, sample.IsTerminalZero);
    }

    private void HandleNonBlockingCorrectionFailure(
        ScaleCommandResult result,
        decimal residualKg,
        CorrectionKind correctionKind,
        ResidualCorrectionCommand failedCommand,
        string operation)
    {
        if (result.IsTransportError)
        {
            _connected = false;
            _waitingForPostCommandZero = false;
            _armed = true;
            _retryAfterConnectionRecovery = true;
            EnterCorrectionWarning(
                residualKg,
                correctionKind,
                failedCommand,
                $"Потеря связи при {operation}: {result.ResponseText}",
                transitionToWarningState: false,
                continuationMessage: "автофиксация продолжится после восстановления связи");
            Transition(FsmState.Disconnected);
            return;
        }

        EnterCorrectionWarning(
            residualKg,
            correctionKind,
            failedCommand,
            $"Терминал отклонил {operation}: {result.ResponseText} (code={result.ResponseCode})");
    }

    private void EnterCorrectionWarning(
        decimal residualKg,
        CorrectionKind correctionKind,
        ResidualCorrectionCommand? failedCommand,
        string reason,
        bool transitionToWarningState = true,
        string continuationMessage = "автофиксация продолжена")
    {
        var shouldLog = !_redLatched || HasMaterialCorrectionChange(residualKg, correctionKind);
        _redLatched = true;
        _waitingForPostCommandZero = false;
        _armed = true;
        _blockedAtWeightKg = residualKg;
        _warningCorrectionKind = correctionKind;
        _lastFailedCorrectionCommand = failedCommand;
        _correctionAllowedAfterChange = false;
        _plateauSamples.Clear();
        _unloadSamples.Clear();

        if (transitionToWarningState)
            Transition(FsmState.InvalidWeightState);

        if (shouldLog)
        {
            _log.LogWarning(
                "{Reason}. Weight={Weight:0.###}kg; {ContinuationMessage}",
                reason,
                residualKg,
                continuationMessage);
        }
    }

    private bool ShouldAttemptCorrection(decimal residualKg, CorrectionKind correctionKind)
    {
        if (!_redLatched)
            return true;

        if (_retryAfterConnectionRecovery)
            return true;

        return HasMaterialCorrectionChange(residualKg, correctionKind);
    }

    private bool HasMaterialCorrectionChange(decimal residualKg, CorrectionKind correctionKind)
    {
        if (correctionKind != _warningCorrectionKind)
            return true;

        if (Math.Sign(residualKg) != Math.Sign(_blockedAtWeightKg))
            return true;

        var retryThresholdKg = Math.Max(_divisionKg, (decimal)_settings.PlateauWindowKg);
        return Math.Abs(residualKg - _blockedAtWeightKg) >= retryThresholdKg;
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

    private void MarkAcceptedCorrection(decimal residualKg, CorrectionKind correctionKind)
    {
        if (!_redLatched)
            return;

        _blockedAtWeightKg = residualKg;
        _warningCorrectionKind = correctionKind;
        _lastFailedCorrectionCommand = null;
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
        _warningCorrectionKind = CorrectionKind.None;
        _lastFailedCorrectionCommand = null;
        _retryAfterConnectionRecovery = false;
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
