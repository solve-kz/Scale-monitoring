using Scalemon.Common.Updates;
using Scalemon.ServiceHost.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalemon.Common;
using Scalemon.FSM;
using System.Threading;
using System.Threading.Tasks;
using static Scalemon.Common.Enums;

/// <summary>
/// Фоновый сервис, который связывает все компоненты системы.
/// </summary>
public class ScalemonService : BackgroundService
{
    private readonly ILogger<ScalemonService> _logger;
    private readonly IScaleProcessor _scale;
    private readonly IScaleStateMachine _fsm;
    private readonly IDataAccess _db;
    private readonly ISignalBus _arduino;
    private readonly ISlaughterModeState _slaughterModeState;
    private readonly IProductionIndicatorState _indicatorState;

    private readonly ApplicationMaintenance _maintenance;

    private readonly SemaphoreSlim _fsmGate = new(1, 1);

    // Поле для хранения последнего отправленного сигнала
    private Enums.ArduinoSignalCode? _lastSentSignal = null;

    public ScalemonService(
            ILogger<ScalemonService> logger,
            IScaleProcessor scale,
            IScaleStateMachine fsm,
            IDataAccess db,
            ISignalBus arduino,
            ISlaughterModeState slaughterModeState,
            IProductionIndicatorState indicatorState,
            ApplicationMaintenance maintenance)
    {
        _logger = logger;
        _scale = scale;
        _fsm = fsm;
        _db = db;
        _arduino = arduino;
        _slaughterModeState = slaughterModeState;
        _indicatorState = indicatorState;
        _maintenance = maintenance;
    }

    private void HandleSlaughterModeChanged(SlaughterMode mode)
    {
        var wasKnown = _slaughterModeState.IsKnown;
        var previous = wasKnown ? _slaughterModeState.Current : mode;
        _slaughterModeState.Set(mode);
        if (!wasKnown || previous != mode)
        {
            _logger.LogInformation("Режим забоя подтверждён: {mode}", mode);
        }

        var indicator = mode == SlaughterMode.Sanitary
            ? ArduinoSignalCode.SanitaryModeIndicator
            : ArduinoSignalCode.GeneralModeIndicator;
        _ = _arduino.SendAsync(indicator);
    }

    private void HandleArduinoConnected()
    {
        // Arduino гасит лампы при перезапуске. Сбрасываем кэш, чтобы следующий
        // снимок весов обязательно восстановил актуальную индикацию, включая ошибку.
        _lastSentSignal = null;
        _ = _arduino.SendAsync(ArduinoSignalCode.RequestSlaughterMode);
    }

    private void HandleArduinoDisconnected()
    {
        _slaughterModeState.Reset();
        _indicatorState.Reset();
        _logger.LogWarning("Режим забоя неизвестен до восстановления связи с Arduino");
    }

    // --- НАЧАЛО ИЗМЕНЕНИЙ ---
    /// <summary>
    /// Отправляет сигнал на Arduino, только если он отличается от предыдущего.
    /// Гарантирует, что перед включением новой индикации старая будет погашена.
    /// </summary>
    private async Task SendSignalOnceAsync(Enums.ArduinoSignalCode signal)
    {
        if (_lastSentSignal == signal)
            return;

        // Определяем, является ли новый сигнал командой на включение какой-либо лампы
        bool isNewSignalOnCommand = signal != Enums.ArduinoSignalCode.Unstable &&
                                    signal != Enums.ArduinoSignalCode.LinkOff;

        // Если мы хотим включить какую-то лампу, сначала всегда отправляем команду "погасить всё".
        // Это предотвращает "наложение" сигналов (например, зелёного и красного).
        if (isNewSignalOnCommand)
        {
            await _arduino.SendAsync(Enums.ArduinoSignalCode.Unstable);
        }

        // Теперь отправляем целевую команду
        await _arduino.SendAsync(signal);
        _indicatorState.Set(signal);

        // И запоминаем её
        _lastSentSignal = signal;
    }
    // --- КОНЕЦ ИЗМЕНЕНИЙ ---

    private async Task HandleDataAsync(ScaleDataPoint data)
    {
        _maintenance.Observe(data);
        if (MaintenanceGate.Shared.IsClosed || !_fsmGate.Wait(0)) return;
        try
        {
            if (MaintenanceGate.Shared.IsClosed) return;
            using var operation = MaintenanceGate.Shared.Enter();
            await _fsm.SetConnectionAsync(data.IsConnected);
            await _fsm.SetAlarmAsync(data.IsAlarm);

            if (data.HasFreshMeasurement)
            {
                await _fsm.OnScaleSampleAsync(data);
            }

            var fsmState = _fsm.CurrentState;
            var signalToSend = Enums.ArduinoSignalCode.Unstable; // По умолчанию - гасим всё

            if (data.IsAlarm)
            {
                signalToSend = Enums.ArduinoSignalCode.RedOn;
            }
            else if (fsmState == FsmState.Disconnected)
            {
                signalToSend = Enums.ArduinoSignalCode.LinkOff;
            }
            else if (_fsm.HasLatchedProcessError)
            {
                signalToSend = Enums.ArduinoSignalCode.RedOn;
            }
            else if (fsmState == FsmState.InvalidWeightState)
            {
                signalToSend = Enums.ArduinoSignalCode.RedOn;
            }
            else if (data.IsStable)
            {
                switch (fsmState)
                {
                    case FsmState.IdleZero:
                        signalToSend = Enums.ArduinoSignalCode.Idle;
                        break;
                    case FsmState.AwaitUnload:
                        if (data.WeightKg > 0)
                            signalToSend = Enums.ArduinoSignalCode.Completed;
                        else
                            signalToSend = Enums.ArduinoSignalCode.Unstable;
                        break;
                }
            }

            await SendSignalOnceAsync(signalToSend);
        }
        finally
        {
            _fsmGate.Release();
        }
    }

    private async Task HandleButtonAsync()
    {
        await _fsmGate.WaitAsync();
        try
        {
            if (MaintenanceGate.Shared.IsClosed) return;
            using var operation = MaintenanceGate.Shared.Enter();
            await _fsm.OnButtonPressedAsync();
        }
        finally { _fsmGate.Release(); }
    }

    private async void HandleDatabaseFailed(Exception exception)
    {
        IDisposable? operation = null;
        try
        {
            operation = MaintenanceGate.Shared.Enter();
            await _fsmGate.WaitAsync();
            try { await _fsm.OnDatabaseFailedAsync(exception); }
            finally { _fsmGate.Release(); }
        }
        catch (InvalidOperationException) when (MaintenanceGate.Shared.IsClosed) { }
        catch (Exception ex) { _logger.LogError(ex, "Не удалось передать FSM состояние ошибки БД"); }
        finally { operation?.Dispose(); }
    }

    private async void HandleDatabaseRestored()
    {
        IDisposable? operation = null;
        try
        {
            operation = MaintenanceGate.Shared.Enter();
            await _fsmGate.WaitAsync();
            try { await _fsm.OnDatabaseRestoredAsync(); }
            finally { _fsmGate.Release(); }
        }
        catch (InvalidOperationException) when (MaintenanceGate.Shared.IsClosed) { }
        catch (Exception ex) { _logger.LogError(ex, "Не удалось передать FSM восстановление БД"); }
        finally { operation?.Dispose(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _scale.DataReceived += HandleDataAsync;
        _arduino.SubscribeButtonPressed(HandleButtonAsync);
        _arduino.SlaughterModeChanged += HandleSlaughterModeChanged;
        _arduino.ConnectionEstablished += HandleArduinoConnected;
        _arduino.ConnectionLost += HandleArduinoDisconnected;
        _db.DatabaseFailed += HandleDatabaseFailed;
        _db.DatabaseRestored += HandleDatabaseRestored;

        _arduino.Start();
        _scale.Start();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ScalemonService: остановка службы.");
        _scale.DataReceived -= HandleDataAsync;
        _arduino.UnsubscribeButtonPressed(HandleButtonAsync);
        _arduino.SlaughterModeChanged -= HandleSlaughterModeChanged;
        _arduino.ConnectionEstablished -= HandleArduinoConnected;
        _arduino.ConnectionLost -= HandleArduinoDisconnected;
        _db.DatabaseFailed -= HandleDatabaseFailed;
        _db.DatabaseRestored -= HandleDatabaseRestored;
        _scale.Stop();
        _arduino.Stop();
        MaintenanceGate.Shared.Close();
        await MaintenanceGate.Shared.WaitAsync(cancellationToken);
        await ((IDataWriteDrain)_db).StopWritesAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
