using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Scalemon.SerialLink
{

    /// <summary>
/// Компонент, выполняющий периодический опрос весов через драйвер,
/// определяет, стабилизировался ли вес, и передаёт соответствующие события подписчикам.
/// </summary>
    public class ScaleProcessor : Common.IScaleProcessor, IDisposable
    {

        // Списки обработчиков для событий
        private readonly List<Func<decimal, Task>> _weightHandlers = new List<Func<decimal, Task>>();
        private readonly List<Func<Task>> _unstableHandlers = new List<Func<Task>>();
        private readonly List<Func<Task>> _connectionLostHandlers = new List<Func<Task>>();
        private readonly List<Func<Task>> _connectionEstablishedHandlers = new List<Func<Task>>();
        private readonly List<Func<Task>> _scaleAlarmHandlers = new List<Func<Task>>();

        // Драйвер весов
        private readonly Common.IScaleDriver _driver;

        // Логгер для событий и ошибок
        private readonly ILogger<ScaleProcessor> _logger;

        // Параметры, считываемые из конфигурации
        private readonly int _stableThreshold;
        private readonly int _unstableThreshold;
        private readonly int _pollingInterval;

        // Для управления жизненным циклом фоновой задачи
        private CancellationTokenSource _cts;
        private Task _processingTask;
        private bool disposedValue;

        /// <summary>
    /// Конструктор: инициализирует драйвер и параметры из конфигурации.
    /// </summary>
        public ScaleProcessor(ILogger<ScaleProcessor> logger, Common.IScaleDriver driver, string portName, int stableThreshold, int unstableThreshold, int pollingIntervalMs)
        {
            _logger = logger;
            _driver = driver;
            driver.PortConnection = portName;
            _stableThreshold = stableThreshold;
            _unstableThreshold = unstableThreshold;
            _pollingInterval = pollingIntervalMs;
            _logger.LogInformation("Библиотека ScaleProcessor инициализирована: PollInterval={interval}ms, StableThreshold={stable}, UnstableThreshold={unstable}", _pollingInterval, _stableThreshold, _unstableThreshold);
        }

        /// <summary>
    /// Запускает фоновую задачу опроса весов.
    /// </summary>
        public void Start()
        {
            _cts = new CancellationTokenSource();
            _processingTask = ProcessLoopAsync(_cts.Token);
            _logger.LogInformation("Библиотека ScaleProcessor запущена");
        }

        /// <summary>
    /// Главный цикл опроса весов, выполняется с интервалом _pollingInterval.
    /// </summary>
        private async Task ProcessLoopAsync(CancellationToken token)
        {
            var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollingInterval));
            int stableCount = 0;
            int unstableCount = 0;
            var swLoop = new Stopwatch();
            var swRead = new Stopwatch();
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    swLoop.Restart();
                    bool shouldNotifyLost = false;
                    try
                    {
                        // Подключение к весам при необходимости
                        if (!_driver.IsConnected)
                        {
                            _logger.LogInformation("Нет подключения в начале цикла взвешивания");
                            _driver.OpenConnection();
                            if (_driver.IsConnected)
                            {
                                await RaiseAllAsync(_connectionEstablishedHandlers);
                            }
                        }

                        if (_driver.IsConnected)
                        {
                            swRead.Restart();
                            _driver.ReadWeight();
                            swRead.Stop();
                            _logger.LogInformation("ReadWeight() took {ms} ms", swRead.ElapsedMilliseconds);
                            switch (_driver.LastResponseNum)
                            {
                                case 0L:
                                    {
                                        // Ответ корректный: проверка на стабилизацию веса
                                        if (_driver.Stable)
                                        {
                                            stableCount += 1;
                                            unstableCount = 0;
                                            if (stableCount == _stableThreshold)
                                            {
                                                _logger.LogInformation($"Вес считан: {_driver.Weight}");
                                                await RaiseAllAsync(_weightHandlers, _driver.Weight);
                                                stableCount = 0;
                                            }
                                        }
                                        else
                                        {
                                            unstableCount += 1;
                                            stableCount = 0;
                                            if (unstableCount == _unstableThreshold)
                                            {
                                                await RaiseAllAsync(_unstableHandlers);
                                                // unstableCount = 0
                                            }
                                        }

                                        break;
                                    }

                                case 1L:
                                    {
                                        // Весы вернули ошибку — разрываем соединение
                                        _driver.CloseConnection();
                                        shouldNotifyLost = true;
                                        break;
                                    }

                                default:
                                    {
                                        // Аппаратная ошибка (например, ALARM)
                                        await RaiseAllAsync(_scaleAlarmHandlers);
                                        break;
                                    }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Любая ошибка — считаем потерей связи
                        _logger.LogError(ex, "Ошибка потери связи в цикле взвешивания");
                        _driver.CloseConnection();
                        shouldNotifyLost = true;
                    }

                    // Отдельно уведомляем о потере связи (вне Catch)
                    if (shouldNotifyLost)
                    {
                        await RaiseAllAsync(_connectionLostHandlers);
                    }
                    swLoop.Stop();
                    _logger.LogInformation("Iteration took {ms} ms", swLoop.ElapsedMilliseconds);
                }
            }
            // Ожидаемая отмена при остановке
            catch (OperationCanceledException ocex)
            {
            }
            finally
            {
                timer.Dispose();
            }
        }

        /// <summary>
    /// Останавливает опрос весов и закрывает соединение.
    /// </summary>
        public void Stop()
        {
            if (_cts is not null)
            {
                _cts.Cancel();
                try
                {
                    _processingTask?.Wait();
                }
                catch (AggregateException ex)
                {
                    // Игнорируем отмену
                }
            }
            _driver.CloseConnection();
            _logger.LogInformation("ScaleProcessor остановлен");
        }

        /// <summary>
    /// Отправляет команду сброса веса в 0.
    /// </summary>
        public async Task ResetToZeroAsync()
        {
            _driver.SetToZero();
            if (_driver.LastResponseNum > 0L)
            {
                _logger.LogError("Процессор. Ошибка сброса на ноль: {text}", _driver.LastResponseText);
                throw new InvalidOperationException($"Error resetting to zero: {_driver.LastResponseText}");
            }
            await Task.CompletedTask;
        }

        /// <summary>
    /// Корректное освобождение ресурсов и остановка фоновой задачи.
    /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (_cts is not null)
                    {
                        _cts.Cancel();
                        _cts.Dispose();
                    }
                    _driver.CloseConnection();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
    /// Безопасный вызов всех обработчиков события с параметром.
    /// </summary>
        private async Task RaiseAllAsync<T>(IEnumerable<Func<T, Task>> handlers, T arg)
        {
            foreach (var h in handlers.ToArray())
            {
                try
                {
                    await h(arg);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка обработчика с параметрами");
                }
            }
        }

        /// <summary>
    /// Безопасный вызов всех обработчиков события без параметров.
    /// </summary>
        private async Task RaiseAllAsync(IEnumerable<Func<Task>> handlers)
        {
            foreach (var h in handlers.ToArray())
            {
                try
                {
                    await h();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка обработчика без параметров");
                }
            }
        }

        // Методы подписки/отписки на события:
        public void SubscribeWeightReceived(Func<decimal, Task> handler)
        {
            _weightHandlers.Add(handler);
        }
        public void UnsubscribeWeightReceived(Func<decimal, Task> handler)
        {
            _weightHandlers.Remove(handler);
        }

        public void SubscribeUnstable(Func<Task> handler)
        {
            _unstableHandlers.Add(handler);
        }
        public void UnsubscribeUnstable(Func<Task> handler)
        {
            _unstableHandlers.Remove(handler);
        }

        public void SubscribeConnectionLost(Func<Task> handler)
        {
            _connectionLostHandlers.Add(handler);
        }
        public void UnsubscribeConnectionLost(Func<Task> handler)
        {
            _connectionLostHandlers.Remove(handler);
        }

        public void SubscribeConnectionEstablished(Func<Task> handler)
        {
            _connectionEstablishedHandlers.Add(handler);
        }
        public void UnsubscribeConnectionEstablished(Func<Task> handler)
        {
            _connectionEstablishedHandlers.Remove(handler);
        }

        public void SubscribeScaleAlarm(Func<Task> handler)
        {
            _scaleAlarmHandlers.Add(handler);
        }
        public void UnsubscribeScaleAlarm(Func<Task> handler)
        {
            _scaleAlarmHandlers.Remove(handler);
        }
    }
}