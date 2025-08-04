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

        /// <summary>
        /// Основной метод, запускающийся при старте службы.
        /// Здесь мы:
        /// 1) Подписываемся на события от компонентов
        /// 2) Запускаем опрос весов и Arduino
        /// 3) Блокируем поток до остановки службы
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 1. Подписка на события ScaleProcessor → FSM
            _scale.SubscribeConnectionEstablished(_fsm.OnScaleConnectedAsync);
            _scale.SubscribeConnectionLost(_fsm.OnScaleDisconnectedAsync);
            _scale.SubscribeUnstable(_fsm.OnScaleUnstableAsync);
            _scale.SubscribeScaleAlarm(_fsm.OnScaleAlarmAsync);
            _scale.SubscribeWeightReceived(raw => _fsm.OnWeightReceivedAsync(raw));

            // 2. Подписка на событие кнопки сброса на Arduino → FSM
            _arduino.SubscribeButtonPressed(_fsm.OnButtonPressedAsync);

            // 3. Подписка на события работы с базой данных → FSM
            //    Используем анонимные асинхронные обработчики
            _db.DatabaseFailed += async (ex) => await _fsm.OnDatabaseFailedAsync(ex);
            _db.DatabaseRestored += async () => await _fsm.OnDatabaseRestoredAsync();

            // 4. Пуск компонентов
            _scale.Start();    // Начать опрос весов
            _arduino.Start();  // Инициализировать связь с Arduino

            // 5. Держим службу «живой», пока не придёт отмена из ОС
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }

        /// <summary>
        /// Метод вызывается при остановке службы.
        /// Производится корректная остановка всех компонентов.
        /// </summary>
        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("ScalemonService: остановка службы.");

            _scale.Stop();    // Остановить опрос весов
            _arduino.Stop();  // Остановить связь с Arduino

            return base.StopAsync(cancellationToken);
        }
    }

