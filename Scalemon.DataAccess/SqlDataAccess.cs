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
        private readonly ConcurrentQueue<decimal> _retryQueue = new ConcurrentQueue<decimal>();
        private readonly IHostApplicationLifetime _lifetime;
        private readonly string _connString;
        private readonly string _tableName;
        private readonly int _maxQueueSize;
        private bool _isDbDown = false; // Флаг, указывающий на состояние БД
        private decimal _retryCount;
        private int _alarmSize;
        private readonly object _syncLock = new object();

        public event DatabaseFailedEventHandler DatabaseFailed;
        public event DatabaseRestoredEventHandler DatabaseRestored;

        public SqlDataAccess(ILogger<SqlDataAccess> logger, string connString, string tableName, int maxQueueSize, int alarmsize, IHostApplicationLifetime lifetime)
        {

            _logger = logger;
            _connString = connString;
            _tableName = tableName;
            _lifetime = lifetime; // <-- Сохраняем зависимость
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

            Task.Run(async () => { try { while (!cancellationToken.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(30d), cancellationToken); var localList = new List<decimal>(); decimal w; while (_retryQueue.TryDequeue(out w)) localList.Add(w); foreach (var weight in localList) { try { await WriteToDatabaseAsync(weight); bool wasDown = false; lock (_syncLock) { if (_isDbDown) { _isDbDown = false; wasDown = true; } } if (wasDown) { DatabaseRestored?.Invoke(); } } catch { _retryQueue.Enqueue(weight); } } } } catch (TaskCanceledException ex) { } finally { var finalItems = new List<decimal>(); decimal w; while (_retryQueue.TryDequeue(out w)) finalItems.Add(w); foreach (var weight in finalItems) { try { using (var conn = new SqlConnection(_connString)) { conn.Open(); string sql = $"INSERT INTO {_tableName} (Weight, RecordedAt) VALUES (@weight, GETDATE());"; using (var cmd = new SqlCommand(sql, conn)) { cmd.Parameters.Add("@weight", SqlDbType.Decimal).Value = weight; cmd.ExecuteNonQuery(); } } } catch { _logger.LogError("Потеря данных при выключении: {weight}", weight); } } } });
        }

        public async Task SaveWeighingAsync(decimal weight)
        {
            try
            {
                // попытка записать сразу
                await WriteToDatabaseAsync(weight);
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
                    _retryQueue.Enqueue(weight);
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

        private async Task WriteToDatabaseAsync(decimal weight)
        {
            using (var conn = new SqlConnection(_connString))
            {
                await conn.OpenAsync();
                string sql = $"INSERT INTO {_tableName} (Weight, RecordedAt) VALUES (@weight, GETDATE());";
                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.Add("@weight", SqlDbType.Decimal).Value = weight;
                    await cmd.ExecuteNonQueryAsync();
                }
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