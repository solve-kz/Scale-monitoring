using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Scalemon.Common;

namespace Scalemon.SerialLink
{
    public sealed class ScaleProcessor : IScaleProcessor
    {
        private readonly ILogger<ScaleProcessor> _log;
        private readonly IScaleDriver _driver;

        private readonly string _portName;
        private readonly int _pollingIntervalMs;

        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        // состояние
        private volatile bool _connected;               // текущее устойчивое состояние
        private long _lastGoodTick;                     // когда был последний удачный кадр
        private int _goodStreak;                        // сколько удачных подряд
        private int _openPortErrorLogged = 0;           // одноразовый лог TryOpenPort
        private int _connectionBreakdownLogged = 0;     // одноразовый лог connection breakdown

        private int _consecutiveZeroPacketCount = 0;

        // Порог для установления связи: сколько удачных кадров подряд нужно
        private readonly int _connectStreakThreshold = 2; // можно 2–3

        // Таймаут потери связи: после какого молчания считаем "потеряно"
        private int LostTimeoutMs => Math.Max(5 * _pollingIntervalMs, 2000);
        public ScaleProcessor(
            ILogger<ScaleProcessor> log,
            IScaleDriver driver,
            string portName,
            int pollingIntervalMs)
        {
            _log = log;
            _driver = driver;

            _portName = portName ?? "COM1";
            _pollingIntervalMs = Math.Max(50, pollingIntervalMs);

            _log.LogDebug(
                "Библиотека ScaleProcessor инициализирована: PollInterval={interval}ms",
                _pollingIntervalMs);
        }

        public event Func<ScaleDataPoint, Task>? DataReceived;

        public void Start()
        {
            if (_loopTask != null) return;

            _driver.PortConnection = _portName;

            _cts = new CancellationTokenSource();
            _loopTask = Task.Run(() => LoopAsync(_cts.Token));
            _log.LogInformation("Библиотека ScaleProcessor запущена");
        }

        public void Stop()
        {
            try { _cts?.Cancel(); _loopTask?.Wait(); }
            catch { /* ignore */ }
            finally { _cts?.Dispose(); _cts = null; _loopTask = null; }
        }

        public void Dispose()
        {
            Stop();
            _driver.CloseConnection();
        }

        public Task<ScaleCommandResult> ResetToZeroAsync(int timeoutMs, CancellationToken ct = default) =>
            ExecuteCommandAsync(() => _driver.SetToZero(timeoutMs), "ZERO", ct);

        public Task<ScaleCommandResult> SetTareAsync(
            decimal tareKg,
            int timeoutMs,
            CancellationToken ct = default) =>
            ExecuteCommandAsync(() => _driver.SetTare(tareKg, timeoutMs), "TARE", ct);

        public Task<ScaleCommandResult> TareCurrentWeightAsync(int timeoutMs, CancellationToken ct = default) =>
            ExecuteCommandAsync(() => _driver.TareCurrentWeight(timeoutMs), "TARE CURRENT", ct);

        private Task<ScaleCommandResult> ExecuteCommandAsync(
            Func<ScaleCommandResult> command,
            string operation,
            CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var result = command();
                if (result.Succeeded)
                {
                    _log.LogInformation("Команда {Operation} выполнена", operation);
                }
                else if (result.IsTransportError)
                {
                    _connected = false;
                    _goodStreak = 0;
                    _log.LogDebug(
                        "Ошибка связи при выполнении {Operation}: {Error}",
                        operation,
                        result.ResponseText);
                }
                else
                {
                    _log.LogDebug(
                        "Терминал отклонил {Operation}: {Error} (code={Code})",
                        operation,
                        result.ResponseText,
                        result.ResponseCode);
                }

                return result;
            }, ct);
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollingIntervalMs));
            while (!ct.IsCancellationRequested)
            {
                var started = Environment.TickCount64;
                try
                {
                    // 1) Открыть порт (повторно вызывать безопасно)
                    TryOpenPort();

                    // 2) ВСЕГДА пробуем опросить весы, даже если _connected==false
                    _driver.ReadWeight();

                    // 3) Разбор результата
                    ProcessConnectionTransition(nowConnected: _driver.IsConnected);
                }
                catch (Exception ex)
                {
                    // --- НАЧАЛО ИЗМЕНЕНИЙ ---
                    // Любое исключение при обмене — считаем обрывом связи НЕМЕДЛЕННО
                    if (_connected)
                    {
                        _connected = false;
                        _log.LogWarning("Соединение с весами потеряно (по причине: {message})", ex.Message);
                        // Сбрасываем счетчики, чтобы не было ложного таймаута в ProcessConnectionTransition
                        _goodStreak = 0;
                    }
                    // Одноразовый лог "обрыв связи"
                    if (Interlocked.Exchange(ref _connectionBreakdownLogged, 1) == 0)
                        _log.LogInformation("Обрыв связи: {message}", ex.Message);
                    // --- КОНЕЦ ИЗМЕНЕНИЙ ---
                }
                finally
                {
                    // В любом случае (успех или ошибка) отправляем подписчикам актуальное состояние
                    if (DataReceived != null)
                    {
                        var dataPoint = new ScaleDataPoint(
                            weightKg: _driver.Weight,
                            isStable: _driver.Stable,
                            isConnected: _connected, // <-- Используем актуальное состояние из процессора!
                            isAlarm: _driver.IsScaleAlarm,
                            hasFreshMeasurement: _driver.HasFreshMeasurement,
                            divisionKg: _driver.DivisionKg,
                            isTerminalZero: _driver.IsTerminalZero,
                            isNet: _driver.IsNet,
                            tareKg: _driver.TareKg
                        );

                        if (dataPoint.WeightKg != 0)
                        {
                            _consecutiveZeroPacketCount = 0;
                            _log.LogDebug("Отправляю пакет данных: Вес={weight}, Стабилен={stable}, Подключено={conn}, Авария={alarm}",
                                          dataPoint.WeightKg, dataPoint.IsStable, dataPoint.IsConnected, dataPoint.IsAlarm);
                        }
                        else
                        {
                            _consecutiveZeroPacketCount++;
                            if (_consecutiveZeroPacketCount <= 3)
                            {
                                _log.LogDebug("Отправляю пакет данных: Вес={weight}, Стабилен={stable}, Подключено={conn}, Авария={alarm}",
                                              dataPoint.WeightKg, dataPoint.IsStable, dataPoint.IsConnected, dataPoint.IsAlarm);
                            }
                        }
                        await InvokeAll(DataReceived, dataPoint);
                    }

                    var took = (int)(Environment.TickCount64 - started);
                    _log.LogTrace("Iteration took {ms} ms", took);
                }

                // ждём следующий тик
                if (!await timer.WaitForNextTickAsync(ct)) break;
            }
        }

        private void TryOpenPort()
        {
            try
            {
                _driver.OpenConnection(); // безопасно вызывать многократно
                // Ничего не логируем здесь, чтобы не спамить
            }
            catch (Exception ex)
            {
                // Одноразовый лог "не удалось открыть порт"
                if (Interlocked.Exchange(ref _openPortErrorLogged, 1) == 0)
                    _log.LogInformation("Не удалось открыть порт: {message}", ex.Message);

                // Генерируем исключение выше, чтобы его поймал основной обработчик
                throw;
            }
        }

        private void ProcessConnectionTransition(bool nowConnected)
        {
            var now = Environment.TickCount64;

            if (nowConnected)
            {
                // успешный кадр
                _goodStreak++;
                _lastGoodTick = now;

                // если были в "отключено" и набрали порог — фиксируем "подключено"
                if (!_connected && _goodStreak >= _connectStreakThreshold)
                {
                    _connected = true;
                    // разрешаем в будущем снова единоразово логировать ошибки
                    Interlocked.Exchange(ref _openPortErrorLogged, 0);
                    Interlocked.Exchange(ref _connectionBreakdownLogged, 0);

                    _log.LogInformation("Соединение с весами установлено");
                }
            }
            else
            {
                // неуспешный кадр — сбрасываем серию удач
                _goodStreak = 0;

                // если были в "подключено" — ждём таймаут молчания, затем считаем "потеряно"
                // Исключения (полный обрыв) обрабатываются в LoopAsync и приводят к немедленному разрыву
                if (_connected)
                {
                    var msSinceGood = unchecked((int)(now - _lastGoodTick));
                    if (msSinceGood >= LostTimeoutMs)
                    {
                        _connected = false;
                        _log.LogWarning("Соединение с весами потеряно (таймаут ответа)");
                    }
                }
                // если уже "отключено" — ничего не пишем
            }
        }


        // ==== безопасный вызов событий ====

        private static async Task InvokeAll(Func<ScaleDataPoint, Task> ev, ScaleDataPoint arg)
        {
            foreach (var d in ev.GetInvocationList())
                await ((Func<ScaleDataPoint, Task>)d)(arg);
        }
    }
}
