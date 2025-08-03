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

        _scale.SubscribeConnectionEstablished(_fsm.OnScaleConnectedAsync);
        _scale.SubscribeConnectionLost(_fsm.OnScaleDisconnectedAsync);
        _scale.SubscribeUnstable(_fsm.OnScaleUnstableAsync);
        _scale.SubscribeScaleAlarm(_fsm.OnScaleAlarmAsync);
        _scale.SubscribeWeightReceived(raw =>
            _fsm.OnWeightReceivedAsync(raw)
        );

        // 2. Подписываемся на события Arduino
        _arduino.SubscribeButtonPressed(_fsm.OnButtonPressedAsync);

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
