using Microsoft.Extensions.Logging;
using Scalemon.Common;
using Stateless;
using System;
using System.Threading;
using System.Threading.Tasks;
using static Scalemon.Common.Enums;

namespace Scalemon.FSM
{


    public class ScaleStateMachine : IScaleStateMachine
    {

        private readonly StateMachine<Enums.ScalesState, Enums.Trigger> _fsm;
        private readonly StateMachine<Enums.ScalesState, Enums.Trigger>.TriggerWithParameters<decimal> _weightReceivedTrigger;
        private readonly Func<Task> _onResetToZero;
        private readonly Func<Task> _onError;
        private readonly ILogger<ScaleStateMachine> _logger;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private readonly decimal _hystWeight;
        private readonly decimal _minWeight;

        private bool _zeroFlag;
        private bool _errorFlag;
        private bool _isInvalidWeight;
        private decimal _lastRaw;
        private int _semaphoreTime;

        public ScaleStateMachine(ILogger<ScaleStateMachine> logger, double minWeight, double hystWeight, int semaphoreTimeMs, Func<Task> onConnected, Func<Task> onDisconnected, Func<Task> onUnstable, Func<Task> onResetToZero, Func<Task> onZeroState, Func<Task> onInvalidWeight, Func<Task> onError, Func<Task> onResetAlarm, Func<decimal, Task> onRecord)
        {

            // Читаем настройки
            _hystWeight = (decimal)hystWeight;
            _minWeight = (decimal)minWeight;
            _semaphoreTime = semaphoreTimeMs;

            // Создаём FSM
            _fsm = new StateMachine<Enums.ScalesState, Enums.Trigger>(Enums.ScalesState.Disconnected);
            // "Оборачиваем" обычный enum-триггер WeightReceived в параметризованный:
            _weightReceivedTrigger = _fsm.SetTriggerParameters<decimal>(Enums.Trigger.WeightReceived);
            _onResetToZero = onResetToZero;
            _onError = onError;
            _logger = logger;

            // 1) Подключение/отключение
            _fsm.Configure(Enums.ScalesState.Disconnected)
                .Ignore(Trigger.ScaleUnstable)
                .Ignore(Trigger.ScaleAlarm)
                .Ignore(Enums.Trigger.WeightReceived)
                .Permit(Enums.Trigger.ScaleConnected, Enums.ScalesState.Connected)
                .Permit(Enums.Trigger.DatabaseFailure, Enums.ScalesState.DatabaseError);


            _fsm.Configure(Enums.ScalesState.Connected)
                .Permit(Enums.Trigger.ScaleDisconnected, Enums.ScalesState.Disconnected)
                .Permit(Enums.Trigger.ScaleAlarm, Enums.ScalesState.ScaleError)
                .Permit(Enums.Trigger.DatabaseFailure, Enums.ScalesState.DatabaseError)
                .Permit(Enums.Trigger.ScaleUnstable, Enums.ScalesState.Unstable)
                .PermitDynamic(_weightReceivedTrigger, DetermineStateFromWeight)
                .OnEntryAsync(async () => await onConnected())
                .OnExitAsync(async () => await onDisconnected());

            // 2) Нестабильное состояние
            _fsm.Configure(Enums.ScalesState.Unstable)
                .SubstateOf(Enums.ScalesState.Connected)
                .OnEntryFromAsync(Enums.Trigger.ArduinoButtonPressed, async () => { if (_errorFlag) { _errorFlag = false; await onResetAlarm(); } }).OnEntryAsync(async () =>
                    {
                        if (_isInvalidWeight)
                            {
                                // Сбрасываем сигнализацию, если была ошибка взвешивания
                                _isInvalidWeight = false;
                                await onResetAlarm();
                            }
                    await onUnstable();
                    });

            // 3) Стабилизированное состояние — суперкласс для весовых подкатегорий
            _fsm.Configure(Enums.ScalesState.Stabilized).SubstateOf(Enums.ScalesState.Connected)
                .Permit(Enums.Trigger.ScaleUnstable, Enums.ScalesState.Unstable)
                .PermitDynamic(_weightReceivedTrigger, DetermineStateFromWeight);



            // 4) Категории внутри Stabilized
            _fsm.Configure(Enums.ScalesState.NegativeWeight)
                .SubstateOf(Enums.ScalesState.Stabilized)
                .OnEntryAsync(HandleResetAttemptAsync);


            _fsm.Configure(Enums.ScalesState.ZeroWeight)
                .SubstateOf(Enums.ScalesState.Stabilized)
                .OnEntryAsync(async () =>

                                {
                                    _zeroFlag = true;
                                    await onZeroState();
                                });

            _fsm.Configure(Enums.ScalesState.LightWeight)
                .SubstateOf(Enums.ScalesState.Stabilized)
                .OnEntryAsync(HandleResetAttemptAsync);

            _fsm.Configure(Enums.ScalesState.InvalidWeight)
                .SubstateOf(Enums.ScalesState.Stabilized)
                .OnEntryAsync(async () =>

                                {
                                    _isInvalidWeight = true;
                                    await onInvalidWeight();
                                });


            _fsm.Configure(Enums.ScalesState.Recorded)
                .SubstateOf(Enums.ScalesState.Stabilized)
                .OnEntryAsync(async () =>
                                {
                                    _zeroFlag = false;
                                    await onRecord(_lastRaw);
                                });

            _fsm.Configure(Enums.ScalesState.ErrorAfterWeighing)
                .SubstateOf(Enums.ScalesState.Stabilized)
                .Permit(Enums.Trigger.ArduinoButtonPressed, Enums.ScalesState.Unstable)
                .OnEntryAsync(async () =>
                                {
                                    _errorFlag = true;
                                    await onInvalidWeight();
                                });

            // 5) Аппаратная ошибка весов
            _fsm.Configure(Enums.ScalesState.ScaleError)
                .SubstateOf(Enums.ScalesState.Connected)
                .Permit(Enums.Trigger.ScaleUnstable, Enums.ScalesState.Unstable)
                .Permit(Enums.Trigger.ArduinoButtonPressed, Enums.ScalesState.Unstable)
                .PermitDynamic(_weightReceivedTrigger, DetermineStateFromWeight)
                .OnEntryAsync(async () => await onError());

            // 6) Ошибка базы данных
            _fsm.Configure(Enums.ScalesState.DatabaseError)
                .OnEntryAsync(async () => await onError())
                .Permit(Enums.Trigger.DatabaseRestored, Enums.ScalesState.Unstable);
        }

        private Enums.ScalesState DetermineStateFromWeight(decimal raw)
        {
            _lastRaw = raw;
            if (raw < 0m)
            {
                return Enums.ScalesState.NegativeWeight;
            }
            else if (raw == 0m)
            {
                return Enums.ScalesState.ZeroWeight;
            }
            else if (raw <= _hystWeight)
            {
                return Enums.ScalesState.LightWeight;
            }
            else if (raw <= _minWeight)
            {
                return Enums.ScalesState.InvalidWeight;
            }
            else if (_zeroFlag)
            {
                return Enums.ScalesState.Recorded;
            }
            else
            {
                return Enums.ScalesState.ErrorAfterWeighing;
            }
        }

        public async Task OnScaleConnectedAsync()
        {
            if (!await _semaphore.WaitAsync(2000))
            {
                _logger.LogCritical("Не удалось захватить семафор FSM в течение 2 секунд. Автомат может быть заблокирован.");
                return;
            }
            try
            {
                await _fsm.FireAsync(Enums.Trigger.ScaleConnected);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task OnScaleDisconnectedAsync()
        {
            if (!await _semaphore.WaitAsync(_semaphoreTime))
            {
                _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.");
                return;
            }
            try
            {
                await _fsm.FireAsync(Enums.Trigger.ScaleDisconnected);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private async Task HandleResetAttemptAsync()
        {
            Exception resetException = null;
            try
            {
                // Пытаемся выполнить асинхронную операцию
                await _onResetToZero();
            }
            catch (Exception ex)
            {
                // В блоке Catch только сохраняем исключение
                resetException = ex;
            }
            // Проверяем, была ли ошибка, уже ПОСЛЕ блока Catch
            if (resetException is not null)
            {
                _logger.LogError(resetException, "Автоматический сброс веса не удался.");
                await _onError(); // Включаем красную лампу
                await _fsm.FireAsync(Enums.Trigger.ScaleAlarm); // Переходим в состояние ошибки FSM
            }
        }

        public async Task OnScaleUnstableAsync()
        {
            if (!await _semaphore.WaitAsync(_semaphoreTime))
            {
                _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.");
                return;
            }
            try
            {
                await _fsm.FireAsync(Enums.Trigger.ScaleUnstable);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task OnScaleAlarmAsync()
        {
            if (!await _semaphore.WaitAsync(_semaphoreTime))
            {
                _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.");
                return;
            }
            try
            {
                await _fsm.FireAsync(Enums.Trigger.ScaleAlarm);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task OnWeightReceivedAsync(decimal raw)
        {
            if (!await _semaphore.WaitAsync(_semaphoreTime))
            {
                _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.");
                return;
            }
            try
            {
                await _fsm.FireAsync(_weightReceivedTrigger, raw);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task OnButtonPressedAsync()
        {
            if (!await _semaphore.WaitAsync(_semaphoreTime))
            {
                _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.");
                return;
            }
            try
            {
                await _fsm.FireAsync(Enums.Trigger.ArduinoButtonPressed);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task OnDatabaseFailedAsync(Exception ex)
        {
            if (!await _semaphore.WaitAsync(_semaphoreTime))
            {
                _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.");
                return;
            }
            try
            {
                _logger.LogError(ex, "Получен сигнал о сбое в базе данных.");
                await _fsm.FireAsync(Enums.Trigger.DatabaseFailure);
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task OnDatabaseRestoredAsync()
        {
            if (!await _semaphore.WaitAsync(_semaphoreTime))
            {
                _logger.LogCritical("Не удалось захватить семафор FSM в течение _semaphoreTime секунд. Автомат может быть заблокирован.");
                return;
            }
            try
            {
                _logger.LogInformation("Получен сигнал о восстановлении базы данных.");
                await _fsm.FireAsync(Enums.Trigger.DatabaseRestored);
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}