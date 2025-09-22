using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.Extensions.Logging;
using Scalemon.Common;

namespace Scalemon.SignalBus
{
    public class SignalBus : ISignalBus, IDisposable
    {
        private readonly List<Func<Task>> _buttonpressedHandlers = new List<Func<Task>>();
        private SerialPort? _serialPort;
        private readonly string _portName;
        private readonly int _baudRate;
        private readonly int _reconnectIntervalMs;
        private Timer? _timer;
        private bool _isConnected = false;
        private bool _openPortErrorLogged = false;
        private bool disposedValue;
        private readonly ILogger<SignalBus> _logger;

        public SignalBus(ILogger<SignalBus> logger, string portName, int baudRate, int reconnectIntervalMs)
        {
            _logger = logger;
            _portName = portName;
            _baudRate = baudRate;
            _reconnectIntervalMs = reconnectIntervalMs;
            _logger.LogInformation(
                "SignalBus инициализирован: Порт={port}, Скорость={baud}, Интервал переподключения={interval}мс",
                _portName, _baudRate, _reconnectIntervalMs);
        }

        public event Action? ConnectionEstablished;
        public event Action? ConnectionLost;

        public void Start()
        {
            _logger.LogInformation("Запуск SignalBus на порту {port}...", _portName);
            _serialPort = new SerialPort
            {
                PortName = _portName,
                BaudRate = _baudRate,
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                ReadTimeout = 500
            };

            _serialPort.DataReceived += OnDataReceived;
            TryOpenPort();
            _timer = new Timer(_reconnectIntervalMs);
            _timer.Elapsed += (sender, e) => { if (!_isConnected) TryOpenPort(); };
            _timer.Start();
        }

        public void Stop()
        {
            _logger.LogInformation("Остановка SignalBus.");
            if (_serialPort != null)
            {
                _serialPort.DataReceived -= OnDataReceived;
                if (_serialPort.IsOpen)
                {
                    _serialPort.Close();
                }
            }
            _timer?.Stop();
        }

        private void TryOpenPort()
        {
            try
            {
                if (_serialPort == null || _serialPort.IsOpen) return;

                _logger.LogDebug("Попытка открыть порт {port}...", _portName);
                _serialPort.Open();

                if (!_isConnected)
                {
                    _isConnected = true;
                    _logger.LogInformation("Соединение с Arduino на порту {port} установлено.", _portName);

                    // Сбрасываем флаг ошибки, так как мы успешно подключились
                    _openPortErrorLogged = false; // <-- ДОБАВЛЕНО

                    ConnectionEstablished?.Invoke();
                }
            }
            catch (Exception ex)
            {
                if (_isConnected)
                {
                    _isConnected = false;
                    _logger.LogWarning("Потеряно соединение с Arduino на порту {port}.", _portName);
                    ConnectionLost?.Invoke();
                }
                else
                {
                    // --- НАЧАЛО ИЗМЕНЕНИЙ ---
                    // Пишем в лог только если мы еще не сообщали об этой ошибке
                    if (!_openPortErrorLogged)
                    {
                        _logger.LogWarning("Не удалось открыть порт {port}: {message}. Следующая попытка через {interval} мс.", _portName, ex.Message, _reconnectIntervalMs);
                        // Ставим флаг, чтобы больше не писать об этом до успешного подключения
                        _openPortErrorLogged = true;
                    }
                    // --- КОНЕЦ ИЗМЕНЕНИЙ ---
                }
            }
        }

        private async void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (_serialPort == null) return;
                int data = _serialPort.ReadByte();
                if (data == 0x20) // сигнал кнопки
                {
                    _logger.LogInformation("Получен сигнал нажатия кнопки (0x20) от Arduino.");
                    await RaiseAllAsync(_buttonpressedHandlers);
                }
                else
                {
                    _logger.LogDebug("Получены неопознанные данные от Arduino: 0x{data:X2}", data);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при чтении данных от Arduino");
            }
        }

        public async Task SendAsync(Enums.ArduinoSignalCode cmd)
        {
            if (_serialPort?.IsOpen == true)
            {
                try
                {
                    _logger.LogInformation("Отправка команды на Arduino: {command} (0x{commandCode:X2})", cmd, (byte)cmd);
                    await Task.Run(() => _serialPort.Write(new byte[] { (byte)cmd }, 0, 1));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при отправке команды {command} на Arduino", cmd);
                }
            }
            else
            {
                _logger.LogWarning("Не удалось отправить команду {command}: порт {port} не открыт.", cmd, _portName);
            }
        }

        #region Boilerplate Code (Subscribe/Dispose)
        public void SubscribeButtonPressed(Func<Task> handler) => _buttonpressedHandlers.Add(handler);
        public void UnsubscribeButtonPressed(Func<Task> handler) => _buttonpressedHandlers.Remove(handler);
        private async Task RaiseAllAsync(IEnumerable<Func<Task>> handlers)
        {
            foreach (var h in handlers.ToArray()) await h();
        }

        // ... Остальной код (Dispose, деструктор и т.д.) без изменений ...
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Stop();
                    if (_serialPort is not null)
                    {
                        _serialPort.Dispose();
                        _serialPort = null;
                    }
                    if (_timer is not null)
                    {
                        _timer.Dispose();
                        _timer = null;
                    }
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        ~SignalBus()
        {
            Dispose(disposing: false);
        }
        #endregion
    }
}