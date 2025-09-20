using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalemon.Common;
using Scalemon.FSM;
using Scalemon.SerialLink;
using Scalemon.SignalBus;
using Scalemon.SqlDataAccess;
using Serilog;
using System.Threading;
using System.Threading.Tasks;


/// <summary>
/// Фоновый сервис, который связывает все компоненты системы: 
/// - опрос весов (_scale)
/// - конечный автомат обработки состояний (_fsm)
/// - запись в базу данных (_db)
/// - управление индикаторами через Arduino (_arduino)
/// </summary>
    public class ScalemonService : BackgroundService
    {
        private readonly ILogger<ScalemonService> _logger;
        private readonly IScaleProcessor _scale;
        private readonly IScaleStateMachine _fsm;
        private readonly IDataAccess _db;
        private readonly ISignalBus _arduino;

    
    
    private readonly SemaphoreSlim _fsmGate = new(1, 1);
    private int _consecutiveZeroServiceCount = 0;

    /// <summary>
    /// Внедрение зависимостей через DI:
    /// - logger: логирование событий сервиса
    /// - scale: компонент опроса весов
    /// - fsm: конечный автомат обработки весов
    /// - db: хранилище данных (SQL)
    /// - arduino: шина сигналов для индикаторов
    /// </summary>
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

    // Новый обработчик
    private async Task HandleDataAsync(ScaleDataPoint data)
    {
        if (!_fsmGate.Wait(0)) return;
        try
        {
            // Получаем всё из одного пакета! Никаких флагов.
            await _fsm.SetConnectionAsync(data.IsConnected);
            await _fsm.SetAlarmAsync(data.IsAlarm);

            // Отправляем вес в FSM, только если весы стабильны
            if (data.IsStable)
            {
                if (data.WeightKg != 0)
                {
                    _consecutiveZeroServiceCount = 0;
                    _logger.LogDebug("Получен стабильный вес {weightKg} кг. Передаю в FSM.", data.WeightKg);
                }
                else
                {
                    _consecutiveZeroServiceCount++;
                    if (_consecutiveZeroServiceCount <= 3)
                    {
                        _logger.LogDebug("Получен стабильный вес {weightKg} кг. Передаю в FSM.", data.WeightKg);
                    }
                }
                await _fsm.OnWeightSampleAsync(data.WeightKg);
            }
        }
        finally
        {
            _fsmGate.Release();
        }
    }

    /// <summary>
    /// Основной метод, запускающийся при старте службы.
    /// Здесь мы:
    /// 1) Подписываемся на события от компонентов
    /// 2) Запускаем опрос весов и Arduino
    /// 3) Блокируем поток до остановки службы
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 1) события ScaleProcessor → обновляем снимок и сразу же шлём в FSM
        _scale.DataReceived += HandleDataAsync;

        // 3) Arduino/БД как было
        _arduino.SubscribeButtonPressed(_fsm.OnButtonPressedAsync);
        _db.DatabaseFailed += async ex => await _fsm.OnDatabaseFailedAsync(ex);
        _db.DatabaseRestored += async () => await _fsm.OnDatabaseRestoredAsync();

        _scale.Start();
        _arduino.Start();

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// Метод вызывается при остановке службы.
    /// Производится корректная остановка всех компонентов.
    /// </summary>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ScalemonService: остановка службы.");

        _scale.DataReceived -= HandleDataAsync; // Отписка

        _scale.Stop();
        _arduino.Stop();
        return base.StopAsync(cancellationToken);
    }


}

