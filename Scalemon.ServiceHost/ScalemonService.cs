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

    private readonly SemaphoreSlim _fsmGate = new(1, 1);

    // Поле для хранения последнего отправленного сигнала
    private Enums.ArduinoSignalCode? _lastSentSignal = null;

    public ScalemonService(
            ILogger<ScalemonService> logger,
            IScaleProcessor scale,
            IScaleStateMachine fsm,
            IDataAccess db,
            ISignalBus arduino,
            ISlaughterModeState slaughterModeState)
    {
        _logger = logger;
        _scale = scale;
        _fsm = fsm;
        _db = db;
        _arduino = arduino;
        _slaughterModeState = slaughterModeState;
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
        => _ = _arduino.SendAsync(ArduinoSignalCode.RequestSlaughterMode);

    private void HandleArduinoDisconnected()
    {
        _slaughterModeState.Reset();
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

        // И запоминаем её
        _lastSentSignal = signal;
    }
    // --- КОНЕЦ ИЗМЕНЕНИЙ ---

    private async Task HandleDataAsync(ScaleDataPoint data)
    {
        if (!_fsmGate.Wait(0)) return;
        try
        {
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _scale.DataReceived += HandleDataAsync;
        _arduino.SubscribeButtonPressed(_fsm.OnButtonPressedAsync);
        _arduino.SlaughterModeChanged += HandleSlaughterModeChanged;
        _arduino.ConnectionEstablished += HandleArduinoConnected;
        _arduino.ConnectionLost += HandleArduinoDisconnected;
        _db.DatabaseFailed += async ex => await _fsm.OnDatabaseFailedAsync(ex);
        _db.DatabaseRestored += async () => await _fsm.OnDatabaseRestoredAsync();

        _arduino.Start();
        _scale.Start();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ScalemonService: остановка службы.");
        _scale.DataReceived -= HandleDataAsync;
        _arduino.UnsubscribeButtonPressed(_fsm.OnButtonPressedAsync);
        _arduino.SlaughterModeChanged -= HandleSlaughterModeChanged;
        _arduino.ConnectionEstablished -= HandleArduinoConnected;
        _arduino.ConnectionLost -= HandleArduinoDisconnected;
        _scale.Stop();
        _arduino.Stop();
        return base.StopAsync(cancellationToken);
    }
}
