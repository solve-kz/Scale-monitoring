using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalemon.Common;
using Scalemon.DataAccess;
using Scalemon.FSM;
using Scalemon.SerialLink;
using Scalemon.SignalBus;
using System.Threading;
using System.Threading.Tasks;

public class ScalemonService : BackgroundService
{
    private readonly ILogger<ScalemonService> _logger;
    private readonly IScaleProcessor _scale;
    private readonly IStateMachine _fsm;
    private readonly IDataAccess _db;
    private readonly ISignalBus _arduino;

    public ScalemonService(
        ILogger<ScalemonService> logger,
        IScaleProcessor scale,
        IStateMachine fsm,
        IDataAccess db,
        ISignalBus arduino)
    {
        _logger = logger;
        _scale = scale;
        _fsm = fsm;
        _db = db;
        _arduino = arduino;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Подписываемся на события весов
        _scale.ConnectionEstablished += () => _arduino.Send(ArduinoSignalCode.LinkOn);
        _scale.ConnectionLost += () => _arduino.Send(ArduinoSignalCode.LinkOff);
        _scale.WeightReceived += raw => _fsm.HandleWeight(raw);
        _scale.Unstable += () => _arduino.Send(ArduinoSignalCode.Unstuble);
        _scale.ScaleAlarm += () => _arduino.Send(ArduinoSignalCode.ScaleAlarm);

        // 2. Подписываемся на события Arduino
        _arduino.ConnectionEstablished += () => _fsm.HandleArduinoConnectionEstablished();
        _arduino.ConnectionLost += () => _fsm.HandleArduinoConnectionLost();
        _arduino.ButtonPressed += () => _fsm.HandleButtonPressed();

        // 3. Обработка изменения состояния FSM
        _fsm.StateChanged += newState =>
        {
            _logger.LogInformation($"FSM перешёл в {newState}");
            switch (newState)
            {
                case ScaleState.Complited:
                    _arduino.Send(ArduinoSignalCode.Complited);
                    _db.SaveWeighing(_fsm.LastWeight, DateTime.Now);
                    break;
                case ScaleState.Idle:
                    _arduino.Send(ArduinoSignalCode.Idle);
                    break;
                case ScaleState.Weighing:
                    _arduino.Send(ArduinoSignalCode.Unstuble);
                    break;
                case ScaleState.ComplitedSmall:
                    _arduino.Send(ArduinoSignalCode.ComplitedSmall);
                    break;
                case ScaleState.ResetToZero:
                    _scale.ResetToZero();
                    break;
            }
        };

        // 4. Запускаем компоненты
        _scale.Start();
        _arduino.Start();

        // 5. Держим сервис «живым» до остановки
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }


    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Служба ScalemonService останавливается.");
        _scale.Stop();
        _arduino.Stop();
        return base.StopAsync(cancellationToken);
    }
}
