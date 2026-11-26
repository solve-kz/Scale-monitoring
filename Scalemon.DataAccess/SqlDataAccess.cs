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
using Scalemon.Common.Updates;

namespace Scalemon.SqlDataAccess
{

    public class SqlDataAccess : IDataAccess, IDataWriteDrain, IDisposable
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
        private readonly SemaphoreSlim _retryGate = new(1, 1);
        private readonly CancellationTokenSource _retryStop = new();
        private readonly Task _retryTask;
        private readonly PendingWriteRegistry _pendingWrites = new();
        public int PendingWrites => _pendingWrites.Count;
        public bool DatabaseAvailable { get { lock (_syncLock) return !_isDbDown; } }
        /// <inheritdoc />
        public async Task DrainAsync(CancellationToken cancellationToken)
        {
            while (PendingWrites != 0)
            {
                await RetryOnceAsync(cancellationToken);
                if (PendingWrites != 0) await Task.Delay(250, cancellationToken);
            }
            await using var connection = new SqlConnection(_connString);
            await connection.OpenAsync(cancellationToken);
            await using (var command = new SqlCommand($"SELECT TOP (0) Id, Weight, RecordedAt FROM {_tableName}; SELECT HAS_PERMS_BY_NAME(@table, 'OBJECT', 'INSERT');", connection))
            {
                command.Parameters.AddWithValue("@table", _tableName);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.NextResultAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 1)
                    throw new UnauthorizedAccessException("Учётная запись службы не имеет права INSERT в таблицу взвешиваний.");
            }
            await _weighingModeStore.EnsureInitializedAsync(cancellationToken);
            lock (_syncLock) _isDbDown = false;
        }
        /// <inheritdoc />
        public async Task StopWritesAsync(CancellationToken cancellationToken)
        {
            _retryStop.Cancel();
            await _retryTask.WaitAsync(cancellationToken);
            await DrainAsync(cancellationToken);
        }

        public event DatabaseFailedEventHandler DatabaseFailed;
        public event DatabaseRestoredEventHandler DatabaseRestored;

        private sealed record PendingWeighing(decimal Weight, SlaughterMode Mode, PendingWriteRegistry.Ticket Ticket);

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
            _retryTask = RetryLoopAsync(_retryStop.Token);
        }

        private async Task RetryLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    await RetryOnceAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        private async Task RetryOnceAsync(CancellationToken ct)
        {
            await _retryGate.WaitAsync(ct);
            try
            {
                var count = _retryQueue.Count;
                for (var i = 0; i < count && _retryQueue.TryDequeue(out var pending); i++)
                {
                    try
                    {
                        await WriteToDatabaseAsync(pending);
                        pending.Ticket.Complete();
                        bool restored;
                        lock (_syncLock) { restored = _isDbDown; _isDbDown = false; }
                        if (restored) NotifyRestored();
                    }
                    catch
                    {
                        _retryQueue.Enqueue(pending);
                        lock (_syncLock) _isDbDown = true;
                    }
                    ct.ThrowIfCancellationRequested();
                }
            }
            finally { _retryGate.Release(); }
        }

        public async Task SaveWeighingAsync(decimal weight)
        {
            using var operation = MaintenanceGate.Shared.Enter();
            var mode = await _slaughterModeState.WaitUntilKnownAsync(_lifetime.ApplicationStopping);
            var pending = new PendingWeighing(weight, mode, _pendingWrites.Begin());
            try
            {
                // попытка записать сразу
                await WriteToDatabaseAsync(pending);
                pending.Ticket.Complete();
                lock (_syncLock)
                {
                    _retryCount = 0m;
                    if (_isDbDown)
                    {
                        _isDbDown = false;
                        NotifyRestored();
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

        private void NotifyRestored()
        {
            try { DatabaseRestored?.Invoke(); }
            catch (Exception ex) { _logger.LogError(ex, "Ошибка подписчика восстановления БД; запись уже сохранена"); }
        }

        public async Task DeleteLastWeighingAsync()
        {
            using var operation = MaintenanceGate.Shared.Enter();
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
