using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalemon.Common;

namespace Scalemon.SqlDataAccess
{

    public class SqlDataAccess : IDataAccess, IDisposable
    {

        private readonly ILogger<SqlDataAccess> _logger;
        private readonly ConcurrentQueue<PendingWeighing> _retryQueue = new();
        private readonly IHostApplicationLifetime _lifetime;
        private readonly ISlaughterModeState _slaughterModeState;
        private readonly IWeighingModeStore _weighingModeStore;
        private readonly string _connString;
        private readonly string _tableName;
        private readonly int _maxQueueSize;
        private bool _isDbDown = false; // Флаг, указывающий на состояние БД
        private decimal _retryCount;
        private int _alarmSize;
        private readonly object _syncLock = new object();

        public event DatabaseFailedEventHandler DatabaseFailed;
        public event DatabaseRestoredEventHandler DatabaseRestored;

        private sealed record PendingWeighing(decimal Weight, SlaughterMode Mode);

        public SqlDataAccess(
            ILogger<SqlDataAccess> logger,
            string connString,
            string tableName,
            int maxQueueSize,
            int alarmsize,
            IHostApplicationLifetime lifetime,
            ISlaughterModeState slaughterModeState,
            IWeighingModeStore weighingModeStore)
        {

            _logger = logger;
            _connString = connString;
            _tableName = tableName;
            _lifetime = lifetime; // <-- Сохраняем зависимость
            _slaughterModeState = slaughterModeState;
            _weighingModeStore = weighingModeStore;
            _maxQueueSize = maxQueueSize;
            _alarmSize = alarmsize;
            StartRetryLoop(_lifetime.ApplicationStopping); // <-- Передаем токен отмены
        }

        private void StartRetryLoop(CancellationToken cancellationToken)
        {
            // Цикл работает, пока не поступит запрос на остановку
            // Ждем 30 секунд, но прерываем ожидание, если пришел сигнал остановки

            // убираем из очереди всё, что накопилось

            // ѕытаемс¤ снова
            // ЕСЛИ МЫ ЗДЕСЬ, ЗНАЧИТ ЗАПИСЬ ПРОШЛА УСПЕШНО
            // ПРОВЕРЯЕМ, БЫЛИ ЛИ МЫ В СОСТОЯНИИ СБОЯ

            // если не получилось, возвращаем в очередь

            // нормально завершаемся
            // Финальная попытка сохранить оставшиеся данные

            Task.Run(async () => { 
                try 
                { 
                    while (!cancellationToken.IsCancellationRequested) { 
                        await Task.Delay(TimeSpan.FromSeconds(30d), cancellationToken); 
                        var localList = new List<PendingWeighing>();
                        while (_retryQueue.TryDequeue(out var pending)) localList.Add(pending);
                        foreach (var item in localList) {
                            try { 
                                await WriteToDatabaseAsync(item);
                                bool wasDown = false; 
                                lock (_syncLock) { 
                                    if (_isDbDown) { 
                                        _isDbDown = false; 
                                        wasDown = true; 
                                    } 
                                } 
                                if (wasDown) { 
                                    DatabaseRestored?.Invoke(); 
                                } 
                            } 
                            catch { 
                                _retryQueue.Enqueue(item);
                            } 
                        } 
                    } 
                } 
                catch (TaskCanceledException ex) {
                    _logger.LogError(ex, "Ошибка записи");
                } 
                finally { 
                    var finalItems = new List<PendingWeighing>();
                    while (_retryQueue.TryDequeue(out var pending)) finalItems.Add(pending);
                    foreach (var item in finalItems) {
                        try { 
                            await WriteToDatabaseAsync(item);
                        } 
                        catch { 
                            _logger.LogError("Потеря данных при выключении: {weight}, режим {mode}", item.Weight, item.Mode);
                        } 
                    } 
                } 
            });
        }

        public async Task SaveWeighingAsync(decimal weight)
        {
            var mode = await _slaughterModeState.WaitUntilKnownAsync(_lifetime.ApplicationStopping);
            var pending = new PendingWeighing(weight, mode);
            try
            {
                // попытка записать сразу
                await WriteToDatabaseAsync(pending);
                lock (_syncLock)
                {
                    _retryCount = 0m;
                    if (_isDbDown)
                    {
                        _isDbDown = false;
                        DatabaseRestored?.Invoke();
                    }
                }
            }
            catch (Exception ex)
            {
                // ПРОВЕРКА ПЕРЕД ДОБАВЛЕНИЕМ В ОЧЕРЕДЬ
                if (_retryQueue.Count < _maxQueueSize)
                {
                    _retryQueue.Enqueue(pending);
                }
                else
                {
                    // ОЧЕРЕДЬ ПЕРЕПОЛНЕНА! Это критическая ситуация.
                    // Здесь мы вынуждены отбросить взвешивание, но должны
                    // обязательно залогировать это как КРИТИЧЕСКУЮ ОШИБКУ.
                    _logger.LogError("Очередь повторных попыток заполнена. Потеря данных: {weight}", weight);
                    // Фиксируем состояние недоступности БД и уведомляем FSM
                    lock (_syncLock)
                    {
                        if (!_isDbDown)
                        {
                            _isDbDown = true;
                            DatabaseFailed?.Invoke(ex);
                        }
                    }
                    throw new InvalidOperationException("Очередь повторной записи переполнена. Данные теряются.");
                }
                // Отмечаем первую недоступность БД
                bool notifyFailed = false;
                lock (_syncLock)
                {
                    if (!_isDbDown)
                    {
                        _isDbDown = true;
                        notifyFailed = true;
                    }
                    _retryCount += 1m;
                }

                if (notifyFailed || _retryCount > _alarmSize)
                {
                    DatabaseFailed?.Invoke(ex);
                }
            }
        }

        private async Task WriteToDatabaseAsync(PendingWeighing pending)
        {
            await using var conn = new SqlConnection(_connString);
            await conn.OpenAsync();
            await using var transaction = (SqlTransaction)await conn.BeginTransactionAsync();
            try
            {
                var sql = $"""
                    INSERT INTO {_tableName} (Weight, RecordedAt)
                    OUTPUT INSERTED.Id, INSERTED.RecordedAt
                    VALUES (@weight, GETDATE());
                    """;
                await using var cmd = new SqlCommand(sql, conn, transaction);
                var weightParameter = cmd.Parameters.Add("@weight", SqlDbType.Decimal);
                weightParameter.Precision = 18;
                weightParameter.Scale = 2;
                weightParameter.Value = pending.Weight;

                await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow);
                if (!await reader.ReadAsync())
                {
                    throw new DataException("SQL Server не вернул идентификатор сохранённого взвешивания.");
                }

                var weighingId = reader.GetInt32(0);
                var recordedAt = reader.GetDateTime(1);
                await reader.CloseAsync();

                await _weighingModeStore.UpsertAsync(
                    weighingId,
                    recordedAt,
                    pending.Weight,
                    pending.Mode);
                await transaction.CommitAsync();
                _logger.LogInformation(
                    "Записано взвешивание: {weight}, режим {mode}",
                    pending.Weight,
                    pending.Mode);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        public async Task DeleteLastWeighingAsync()
        {
            using (var conn = new SqlConnection(_connString))
            {
                await conn.OpenAsync();
                using (var cmd = new SqlCommand($"DELETE TOP (1) FROM {_tableName} ORDER BY RecordedAt DESC", conn))
                {
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }

        public void Dispose()
        {
            // «десь ничего не хранитс¤ между вызовами, так что можно оставить пустым.
        }
    }
}
