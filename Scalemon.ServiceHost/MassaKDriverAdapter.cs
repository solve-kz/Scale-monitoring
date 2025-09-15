using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Scalemon.Common;

namespace Scalemon.ServiceHost
{
    public class MassaKDriverAdapter : IScaleDriver
    {
        private readonly Scalemon.MassaKInterop.IScaleDriver _inner;
        private readonly ILogger<MassaKDriverAdapter> _logger;

        // Слежение за состоянием соединения (null = ещё не знаем)
        private bool? _wasConnected = null;

        // Чтобы не «шуметь» при ретраях OpenConnection
        private readonly object _sync = new();
        private DateTime _lastOpenInfoAt = DateTime.MinValue;
        private readonly TimeSpan _openInfoCooldown = TimeSpan.FromSeconds(10);

        public MassaKDriverAdapter(Scalemon.MassaKInterop.IScaleDriver inner,
                                   ILogger<MassaKDriverAdapter> logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string PortConnection
        {
            get => _inner.PortConnection;
            set => _inner.PortConnection = value;
        }

        public decimal Weight => _inner.Weight;
        public bool Stable => _inner.Stable;
        public string LastResponseText => _inner.LastResponseText;
        public long LastResponseNum => _inner.LastResponseNum;
        public bool IsConnected => _inner.isConnected;
        public bool IsScaleAlarm => _inner.isScaleAlarm;

        public void OpenConnection()
        {
            lock (_sync)
            {
                try
                {
                    // «Открываем…» пишем только в Debug, а в Info — не чаще, чем раз в _openInfoCooldown
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Открыто подключение к весам на порту '{Port}'...", _inner.PortConnection);
                    }
                    else if ((DateTime.UtcNow - _lastOpenInfoAt) >= _openInfoCooldown)
                    {
                        _lastOpenInfoAt = DateTime.UtcNow;
                        _logger.LogInformation("openInfoCooldown. Открыто подключение к весам на порту  '{Port}'...", _inner.PortConnection);
                    }

                    _inner.OpenConnection();
                    UpdateConnectionState(nameof(OpenConnection));
                }
                catch (Exception ex)
                {
                    LogConnectionError(nameof(OpenConnection), ex);
                    throw;
                }
            }
        }

        public void CloseConnection()
        {
            lock (_sync)
            {
                try
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Закрыто подключение к весам на порту '{Port}'...", _inner.PortConnection);

                    _inner.CloseConnection();
                    UpdateConnectionState(nameof(CloseConnection));
                }
                catch (Exception ex)
                {
                    LogConnectionError(nameof(CloseConnection), ex);
                    throw;
                }
            }
        }

        public void SetToZero()
        {
            try
            {
                _inner.SetToZero();
                _logger.LogInformation($"Сброс на ноль: {nameof(SetToZero)}");
                LogResponseDebug(nameof(SetToZero));
            }
            catch (Exception ex)
            {
                LogCommandError(nameof(SetToZero), ex);
                throw;
            }
        }

        public void ReadWeight()
        {
            try
            {
                _inner.ReadWeight();
                LogResponseDebug(nameof(ReadWeight));
                _logger.LogDebug("Вес считан");
                _logger.LogDebug($" Взвешено: {nameof(ReadWeight)}");

            }
            catch (Exception ex)
            {
                LogCommandError(nameof(ReadWeight), ex);
                throw;
            }
        }

        // ===== Helpers =====

        private void UpdateConnectionState(string operation)
        {
            bool connected = _inner.isConnected;

            if (_wasConnected == null)
            {
                // Первичное состояние — один раз
                _logger.LogInformation("Подключение весов {State} после {Operation}. IsConnected={Connected}",
                    connected ? "OK" : "FAILED", operation, connected);
            }
            else if (_wasConnected.Value != connected)
            {
                // Логируем ТОЛЬКО на переходах
                if (connected)
                    _logger.LogWarning("Подключение к весам RESTORED на порту '{Port}'", _inner.PortConnection);
                else
                    _logger.LogError("Подключение к весам LOST на порту '{Port}'", _inner.PortConnection);
            }

            _wasConnected = connected;

            if (_logger.IsEnabled(LogLevel.Debug))
                LogResponseDebug(operation);
        }

        private void LogConnectionError(string operation, Exception ex)
        {
            // Ошибку соединения логируем только при переходе в «сломано»
            if (_wasConnected != false)
                _logger.LogError(ex, "{Operation} не выполнено на порту '{Port}'", operation, _inner.PortConnection);

            _wasConnected = false;
        }

        private void LogCommandError(string operation, Exception ex)
        {
            // Команды не меняют состояние соединения, но если ещё не зафиксировано «сломано», отметим
            if (_wasConnected != false)
                _logger.LogError(ex, "{Operation} не выполнено на порту '{Port}'", operation, _inner.PortConnection);

            _wasConnected = false;
        }

        private void LogResponseDebug(string operation)
        {
            if (!_logger.IsEnabled(LogLevel.Debug)) return;

            _logger.LogDebug(
                "{Operation} -> LastResponse: {Code}:{Text}; Weight={Weight}; Stable={Stable}; IsConnected={IsConnected}; Alarm={Alarm}",
                operation,
                _inner.LastResponseNum,
                Safe(_inner.LastResponseText),
                _inner.Weight,
                _inner.Stable,
                _inner.isConnected,
                _inner.isScaleAlarm
            );
        }

        private static string Safe(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
    }
}
