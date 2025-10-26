using Microsoft.Extensions.Logging;
using Scalemon.Common;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using static Scalemon.Common.Enums;

namespace Scalemon.FSM
{
    /// <summary>
    /// FSM "плато → ноль" для автоматической фиксации взвешиваний.
    /// Подключается поверх существующего цикла опроса.
    /// </summary>
    public sealed class PlateauZeroStateMachine
    {

        [Flags]
        public enum Flags
        {
            None = 0,
            ResidualTared = 1 << 0, // был положительный хвост; сделали автоноль
            NegativeTared = 1 << 1, // было стабильное отрицательное; сделали автоноль
            TareFailed = 1 << 2  // автоноль не удался
        }

        public sealed record Settings(
            decimal ZeroBandKg,       // Z: |w| ≤ Z — "ноль"
            decimal ResidualBandKg,   // R: 0 < w ≤ R — "хвост"
            decimal NegativeBandKg,   // N: −N ≤ w < 0 — "малое отрицательное"
            decimal MinWeightKg,      // минимально валидный вес продукта
            int PlateauStableSamples, // M
            int ZeroStableSamples,    // K
            TimeSpan TareTimeout,     // ожидание нуля после SetToZero()
            int TareMaxRetries        // повторы автонуля
        );

        // Внешние зависимости (инъекции):
        private readonly ILogger _log;
        private readonly Settings _cfg;
        private readonly Func<decimal, Task> _onRecordAsync;
        private readonly Action _sendTare;
        private readonly ISignalBus _bus;// SetToZero() на драйвер

        // Служебные поля:
        private FsmState _st = FsmState.Disconnected;
        private bool _connected;
        private bool _alarm;
        private int _consecutiveZeroCount = 0;

        private decimal _peak;          // пик на плато
        private decimal _minStableWeight; // минимальный стабильный вес на плато
        private decimal _tail;          // положительный хвост (0..R)
        private bool _plateauConfirmed; // плато подтверждено (≥M)
        private bool _needTareForNegative; // нужно ли тарировать из-за отрицательных

        private int _stableCount;       // счётчик стабильности текущей "классификации"
        private Class _lastClass = Class.Unknown;

        private DateTime _tareDeadlineUtc;
        private int _tareRetries;

        private enum Class { Unknown, Zero, ResidualPos, Negative, InvalidLight, ValidHeavy }

        public PlateauZeroStateMachine(
            Settings cfg,
            ILogger logger,
            ISignalBus bus, // <-- ПРИНИМАЕМ ЗАВИСИМОСТЬ
            Func<decimal, Task> onRecordAsync,
            Action sendTare)
        {
            _cfg = cfg;
            _log = logger;
            _bus = bus; // <-- СОХРАНЯЕМ
            _onRecordAsync = onRecordAsync;
            _sendTare = sendTare;

            // Валидация порогов
            if (_cfg.MinWeightKg <= _cfg.ResidualBandKg)
                _log.LogWarning("MinWeight ({min}) ≤ ResidualBand ({res}). Рекомендую повысить MinWeight.", _cfg.MinWeightKg, _cfg.ResidualBandKg);

            _log.LogInformation(
                "FSM thresholds: ZeroBand={zero}kg, ResidualBand={res}kg, NegativeBand={neg}kg, MinWeight={min}kg, M={M}, K={K}, Ttare={T}s, MaxRetries={R}",
                _cfg.ZeroBandKg, _cfg.ResidualBandKg, _cfg.NegativeBandKg, _cfg.MinWeightKg,
                _cfg.PlateauStableSamples, _cfg.ZeroStableSamples, (int)_cfg.TareTimeout.TotalSeconds, _cfg.TareMaxRetries);
        }

        // --- ДОБАВЛЯЕМ НОВОЕ СОСТОЯНИЕ В ENUM ---
        // Важно: этот enum скорее всего лежит в отдельном файле, например, Scalemon.Common/Enums.cs
        // Тебе нужно будет добавить `InvalidWeightState` туда.
        // public enum FsmState { Disconnected, IdleZero, ..., InvalidWeightState }

        public FsmState CurrentState => _st;

        // Вызывай это из твоего цикла опроса каждый раз, когда обновился статус соединения
        public void SetConnection(bool nowConnected)
        {
            if (_connected == nowConnected) return;
            _connected = nowConnected;

            if (!_connected)
            {
                Transition(FsmState.Disconnected);
            }
            else
            {
                // В момент восстановления не насилуем переходы — класс определит OnSample
                _log.LogInformation("Связь с весами восстановлена");
            }
        }

        // Вызывай при смене аварийного статуса
        public Task SetAlarmAsync(bool alarmOn)
        {
            if (_alarm == alarmOn) return Task.CompletedTask; // <-- Добавили возврат здесь
            _alarm = alarmOn;

            if (_alarm)
            {
                Transition(FsmState.Alarm);
            }
            else
            {
                _log.LogInformation("Снята авария весов, продолжаю работу");
            }

            return Task.CompletedTask; // <-- Добавили возврат здесь
        }

        /// <summary>
        /// Главный вход: передавай СТАБИЛИЗИРОВАННЫЕ образцы веса сюда (т.е. уже отфильтрованные твоими M/K или сырые — тогда используем внутренний счётчик).
        /// Рекомендуется вызывать каждые 200мс, как сейчас.
        /// </summary>
        public async Task OnSampleAsync(decimal w)
        {
            if (w != 0)
            {
                // Если вес не нулевой, сбрасываем счетчик и всегда пишем в лог
                _consecutiveZeroCount = 0;
                _log.LogInformation("FSM получил семпл: Вес={weight}, Текущее состояние={state}", w, _st);
            }
            else
            {
                // Если вес нулевой, увеличиваем счетчик
                _consecutiveZeroCount++;
                // и пишем в лог, только если он не больше 3
                if (_consecutiveZeroCount <= 3)
                {
                    _log.LogInformation("FSM получил семпл: Вес={weight}, Текущее состояние={state}", w, _st);
                }
            }
            if (!_connected)
            {
                if (_st != FsmState.Disconnected) Transition(FsmState.Disconnected);
                return;
            }
            if (_alarm)
            {
                if (_st != FsmState.Alarm) Transition(FsmState.Alarm);
                return;
            }

            // 1) Классификация и внутренняя стабильность
            var cls = Classify(w);
            UpdateStability(cls);

            // 2) Тиковая обработка тайм-аута тарирования
            if (_st == FsmState.WaitZeroAfterTare && DateTime.UtcNow >= _tareDeadlineUtc)
            {
                if (_tareRetries < _cfg.TareMaxRetries)
                {
                    _tareRetries++;
                    _log.LogWarning("Автоноль: тайм-аут, повтор {try}/{max}", _tareRetries, _cfg.TareMaxRetries);
                    SendTareAndWait();
                }
                else
                {
                    _log.LogError("Автоноль: все попытки исчерпаны");
                    Transition(FsmState.ZeroFailed);
                }
                return;
            }

            // 3) Логика состояний
            switch (_st)
            {
                case FsmState.Disconnected:
                    if (IsZeroStable()) Transition(FsmState.IdleZero);
                    else if (IsValidPlateauStart(cls)) Transition(FsmState.Weighing);
                    break;

                case FsmState.Alarm:
                    // Ждём снятия аварии; переход обработается SetAlarmAsync
                    break;

                case FsmState.IdleZero:
                    if (IsValidPlateauStart(cls))
                    {
                        _peak = w;
                        _minStableWeight = _minStableWeight == 0m ? w : Math.Min(_minStableWeight, w);
                        _plateauConfirmed = false;
                        Transition(FsmState.Weighing);
                    }
                    else if (IsResidualStable())
                    {
                        _log.LogInformation("Обнаружен стабильный остаточный вес ({weight} кг) в состоянии готовности. Инициирую автоноль.", w);
                        SendTareAndWait();
                    }
                    else if (IsNegativeStable())
                    {
                        _log.LogInformation("Обнаружен стабильный отрицательный вес ({weight} кг) в состоянии готовности. Инициирую автоноль.", w);
                        SendTareAndWait();
                    }
                    // --- НАЧАЛО ИЗМЕНЕНИЙ ---
                    else if (IsInvalidLightStable())
                    {
                        _log.LogWarning("Обнаружен стабильный, но невалидный вес ({weight} кг) в диапазоне ({min}-{max} кг). Переход в состояние ошибки.",
                            w, _cfg.ResidualBandKg, _cfg.MinWeightKg);
                        Transition(FsmState.InvalidWeightState);
                    }
                    // --- КОНЕЦ ИЗМЕНЕНИЙ ---
                    break;

                // --- НАЧАЛО ИЗМЕНЕНИЙ ---
                case FsmState.InvalidWeightState:
                    if (cls != Class.InvalidLight)
                    {
                        _log.LogInformation("Вес изменился ({weight} кг), выход из состояния ошибки.", w);
                        Transition(FsmState.IdleZero);
                    }
                    break;
                // --- КОНЕЦ ИЗМЕНЕНИЙ ---

                case FsmState.Weighing:
                    if (cls == Class.ValidHeavy)
                    {
                        _minStableWeight = _minStableWeight == 0m ? w : Math.Min(_minStableWeight, w);
                        _peak = Math.Max(_peak, w);
                        if (IsPlateauStable())
                        {
                            _plateauConfirmed = true;
                            Transition(FsmState.AwaitUnload);
                        }
                    }
                    else if (IsZeroishStable(cls))
                    {
                        // плато не подтвердилось — сбрасываем цикл, возвращаемся к нулю
                        Transition(FsmState.IdleZero);
                    }
                    // иначе игнорируем промежуточное "InvalidLight"
                    break;

                case FsmState.AwaitUnload:
                    if (cls == Class.ValidHeavy)
                    {
                        _minStableWeight = _minStableWeight == 0m ? w : Math.Min(_minStableWeight, w);
                        _peak = Math.Max(_peak, w); // продолжаем копить пик
                    }
                    else if (IsZeroStable())
                    {
                        _tail = 0m;
                        await PrepareRecordAsync(cls, w);
                    }
                    else if (IsResidualStable())
                    {
                        _tail = w; // 0 < w ≤ R
                        await PrepareRecordAsync(cls, w);
                    }
                    else if (IsNegativeStable())
                    {
                        _tail = 0m;
                        _needTareForNegative = true;
                        await PrepareRecordAsync(cls, w);
                    }
                    break;

                case FsmState.PostUnload:
                    // входное действие PostUnload вызывает PrepareRecordAsync → OnRecord → далее в Tare/IdleZero
                    break;

                case FsmState.TarePending:
                case FsmState.WaitZeroAfterTare:
                    if (IsZeroStable())
                    {
                        _log.LogInformation("Автоноль подтверждён стабильным нулём");
                        Transition(FsmState.IdleZero);
                    }
                    break;

                case FsmState.ZeroFailed:
                    if (IsZeroStable())
                    {
                        _log.LogInformation("Ноль подтверждён, выходим из ZeroFailed");
                        Transition(FsmState.IdleZero);
                    }
                    break;
            }
        }

        private async Task PrepareRecordAsync(Class cls, decimal w)
        {

            if (_minStableWeight < _cfg.MinWeightKg)
            {
                Transition(FsmState.IdleZero);
                return;
            }

            Transition(FsmState.PostUnload);

            // 1) Расчёт net
            decimal net = _minStableWeight - _tail;
            net = RoundToScaleStep(net);

            if (net < _cfg.MinWeightKg)
            {
                _log.LogWarning("FSM ПРОПУСК ЗАПИСИ: Рассчитанный вес НЕТТО ({net} кг) меньше минимального порога ({minWeight} кг). Минимум={minWeightStable}, Пик={peak}, Хвост={tail}",
                        net, _cfg.MinWeightKg, _minStableWeight, _peak, _tail);
                await _bus.SendAsync(Enums.ArduinoSignalCode.YellowRedOn); // <-- ОШИБКА ВЗВЕШИВАНИЯ
                // Возврат в IdleZero или Tare в зависимости от хвоста/отрицательного
                if (_tail > 0m || _needTareForNegative) SendTareAndWait();
                else Transition(FsmState.IdleZero);
                return;
            }

            // 2) Запись
            var flags = Flags.None;
            if (_tail > 0m) flags |= Flags.ResidualTared;
            if (_needTareForNegative) flags |= Flags.NegativeTared;

            try
            {
                _log.LogInformation("Запись взвешивания: net={net:0.###}kg (recorded={recorded:0.###}, peak={peak:0.###}, tail={tail:0.###}, flags={flags})",
                    net, _minStableWeight, _peak, _tail, flags);
                if (_onRecordAsync != null)
                    await _onRecordAsync(net);

            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка записи взвешивания в БД");
                // при ошибке записи не тарируем автоматически
                Transition(FsmState.IdleZero);
                return;
            }

            // 3) Автоноль (если нужно)
            if (_tail > 0m || _needTareForNegative)
                SendTareAndWait();
            else
                Transition(FsmState.IdleZero);
        }

        private void SendTareAndWait()
        {
            _needTareForNegative = false; // сбрасываем флаг: тарирование будет выполнено
            _minStableWeight = 0m;
            Transition(FsmState.TarePending);
            try
            {
                _sendTare?.Invoke();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Ошибка отправки SetToZero()");
                Transition(FsmState.ZeroFailed);
                return;
            }
            _tareDeadlineUtc = DateTime.UtcNow + _cfg.TareTimeout;
            Transition(FsmState.WaitZeroAfterTare);
        }

        // ----------------- Вспомогательные методы -----------------

        private void Transition(FsmState to)
        {

            if (_st == to) return;

            var from = _st;
            _st = to;
            _log.LogDebug("FSM: Переход из [{from}] в [{to}]", from, to);

            // При входе в ключевые состояния — один раз в лог
            switch (to)
            {
                case FsmState.Disconnected:
                    _log.LogWarning("Связь с весами потеряна");
                    _bus.SendAsync(Enums.ArduinoSignalCode.LinkOff); // <-- ВЫКЛЮЧИТЬ ВСЁ
                    break;
                case FsmState.IdleZero:
                    _peak = 0m; _tail = 0m; _minStableWeight = 0m; _plateauConfirmed = false; _tareRetries = 0; _needTareForNegative = false;
                    _log.LogInformation("→ IdleZero");
                    break;
                case FsmState.InvalidWeightState: // --- ДОБАВЛЕНО ---
                    _log.LogWarning("→ InvalidWeightState (обнаружен некорректный стабильный вес)");
                    break;
                case FsmState.Weighing:
                    _log.LogDebug("→ Weighing (start), peak={peak:0.###}", _peak);
                    break;
                case FsmState.AwaitUnload:
                    _log.LogDebug("→ AwaitUnload (plateau confirmed), peak≈{peak:0.###}", _peak);
                    break;
                case FsmState.PostUnload:
                    _log.LogDebug("→ PostUnload");
                    break;
                case FsmState.TarePending:
                    _minStableWeight = 0m;
                    _log.LogDebug("→ TarePending");
                    break;
                case FsmState.WaitZeroAfterTare:
                    _log.LogDebug("→ WaitZeroAfterTare (timeout at {deadline:o})", _tareDeadlineUtc);
                    break;
                case FsmState.ZeroFailed:
                    _minStableWeight = 0m;
                    _log.LogError("→ ZeroFailed (автоноль не удался)");
                    break;
                case FsmState.Alarm:
                    _log.LogError("→ Alarm");
                    break;
            }

            // Сброс счётчиков стабильности при смене режимов
            _lastClass = Class.Unknown;
            _stableCount = 0;

            _log.LogTrace("FSM: {from} → {to}", from, to);
        }

        private Class Classify(decimal w)
        {
            if (w < 0m)
            {
                if (w >= -_cfg.NegativeBandKg) return Class.Negative;
                return Class.InvalidLight; // "сильно отрицательный" считаем шумом/ошибкой датчика
            }

            if (w <= _cfg.ZeroBandKg) return Class.Zero;
            if (w <= _cfg.ResidualBandKg) return Class.ResidualPos;
            if (w < _cfg.MinWeightKg) return Class.InvalidLight; // --- ИЗМЕНЕНО --- было w <= ...
            return Class.ValidHeavy;
        }

        private void UpdateStability(Class cls)
        {
            if (cls == _lastClass) _stableCount++;
            else { _lastClass = cls; _stableCount = 1; }
        }

        private bool IsPlateauStable() => _stableCount >= _cfg.PlateauStableSamples && _lastClass == Class.ValidHeavy;
        private bool IsZeroStable() => _stableCount >= _cfg.ZeroStableSamples && _lastClass == Class.Zero;
        private bool IsResidualStable() => _stableCount >= _cfg.ZeroStableSamples && _lastClass == Class.ResidualPos;
        private bool IsNegativeStable() => _stableCount >= _cfg.ZeroStableSamples && _lastClass == Class.Negative;
        // --- ДОБАВЛЕНО ---
        private bool IsInvalidLightStable() => _stableCount >= _cfg.ZeroStableSamples && _lastClass == Class.InvalidLight;

        private static decimal RoundToScaleStep(decimal x)
        {
            // Настрой при необходимости под дискретность АЦП. По умолчанию — 0.01 кг.
            const decimal step = 0.01m;
            return Math.Round(x / step, MidpointRounding.AwayFromZero) * step;
        }

        private bool IsZeroishStable(Class cls) => IsZeroStable() || IsResidualStable() || IsNegativeStable();

        private bool IsValidPlateauStart(Class cls) => cls == Class.ValidHeavy; // старт по первому валидному семплу
    }
}