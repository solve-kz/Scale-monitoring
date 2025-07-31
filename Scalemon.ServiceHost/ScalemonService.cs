using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalemon.Common;
using Scalemon.SqlDataAccess;
using Scalemon.FSM;
using Scalemon.SerialLink;
using Scalemon.SignalBus;
using System.Threading;
using System.Threading.Tasks;

public class ScalemonService : BackgroundService
{
    private readonly ILogger<ScalemonService> _logger;
    private readonly IScaleProcessor _scale;
    private readonly IScaleStateMachine _fsm;
    private readonly IDataAccess _db;
    private readonly ISignalBus _arduino;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1. Подписываемся на события весов
        _scale.ConnectionEstablished += _fsm.OnScaleConnected;
        _scale.ConnectionLost += _fsm .OnScaleDisconnected ;
        _scale.WeightReceived += raw => _fsm.OnWeightReceived (raw);
        _scale.Unstable += _fsm .OnScaleUnstable ;
        _scale.ScaleAlarm += _fsm .OnScaleAlarm ;

        // 2. Подписываемся на события Arduino
        _arduino.ButtonPressed +=  _fsm.OnButtonPressed ;

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
