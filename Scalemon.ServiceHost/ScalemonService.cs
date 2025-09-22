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

    private readonly SemaphoreSlim _fsmGate = new(1, 1);

    // Поле для хранения последнего отправленного сигнала
    private Enums.ArduinoSignalCode? _lastSentSignal = null;

    public ScalemonService(
            ILogger<ScalemonService> logger,
            IScaleProcessor scale,
            IScaleStateMachine fsm,
            IDataAccess db,
            ISignalBus arduino)
    {
        _logger = logger;
        _scale = scale;
        _fsm = fsm;
        _db = db;
        _arduino = arduino;
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

            if (data.IsStable)
            {
                await _fsm.OnWeightSampleAsync(data.WeightKg);
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
                    case FsmState.InvalidWeightState:
                        signalToSend = Enums.ArduinoSignalCode.YellowRedOn;
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
        _db.DatabaseFailed += async ex => await _fsm.OnDatabaseFailedAsync(ex);
        _db.DatabaseRestored += async () => await _fsm.OnDatabaseRestoredAsync();

        _scale.Start();
        _arduino.Start();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ScalemonService: остановка службы.");
        _scale.DataReceived -= HandleDataAsync;
        _scale.Stop();
        _arduino.Stop();
        return base.StopAsync(cancellationToken);
    }
}