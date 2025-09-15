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

        private SerialPort _serialPort;
        private string _portName = "COM1";
        private int _baudRate = 9600;
        private int _reconnectIntervalMs = 5000;
        private Timer _timer;
        private bool _isConnected = false;
        private bool disposedValue;
        private readonly ILogger<SignalBus> _logger;
        public SignalBus(ILogger<SignalBus> logger, string portName, int baudRate, int reconnectedIntervalMs)
        {
            _logger = logger;
            _portName = portName;
            _baudRate = baudRate;
            _reconnectIntervalMs = reconnectedIntervalMs;
        }


        public event Action ConnectionEstablished;
        public event Action ConnectionLost;

        public void Start()
        {
            _serialPort = new SerialPort();
            {
                ref var withBlock = ref _serialPort;
                withBlock.PortName = _portName;
                withBlock.BaudRate = _baudRate;
                withBlock.Parity = Parity.None;
                withBlock.DataBits = 8;
                withBlock.StopBits = StopBits.One;
                withBlock.ReadTimeout = 500;
            }

            _serialPort.DataReceived += OnDataReceived;
            TryOpenPort();
            _timer = new Timer(_reconnectIntervalMs);
            _timer.Elapsed += (sender, e) => { if (!_isConnected) TryOpenPort(); };
            _timer.Start();
        }

        public void Stop()
        {
            _serialPort.DataReceived -= OnDataReceived;
            if (_serialPort is not null && _serialPort.IsOpen)
            {
                _serialPort.Close();
            }
            _timer?.Stop();
        }

        private void TryOpenPort()
        {
            try
            {
                if (!_serialPort.IsOpen)
                    _serialPort.Open();
                if (!_isConnected)
                {
                    _isConnected = true;
                    ConnectionEstablished?.Invoke();
                }
            }
            catch
            {
                if (_isConnected)
                {
                    _isConnected = false;
                    ConnectionLost?.Invoke();
                }
            }
        }

        private async void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                int data = _serialPort.ReadByte();
                if (data == 0x20) // сигнал кнопки
                {
                    await RaiseAllAsync(_buttonpressedHandlers);
                }
            }
            catch (Exception ex)
            {
                // возможна¤ потер¤ соединени¤
                _logger.LogError(ex, "Ошибка при опросе Arduino");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // 1) ќстанавливаем логику Stop()
                    Stop();
                    // 2) ќсвобождаем SerialPort
                    if (_serialPort is not null)
                    {
                        _serialPort.Dispose();
                        _serialPort = null;
                    }
                    // 3) ќсвобождаем Timer
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
            // в финализаторе освобождаем только неуправл¤емые
            Dispose(disposing: false);
        }

        private async Task RaiseAllAsync<T>(IEnumerable<Func<T, Task>> handlers, T arg)
        {
            foreach (var h in handlers.ToArray())
                await h(arg);
        }

        private async Task RaiseAllAsync(IEnumerable<Func<Task>> handlers)
        {
            foreach (var h in handlers.ToArray())
                await h();
        }

        public void SubscribeButtonPressed(Func<Task> handler)
        {
            _buttonpressedHandlers.Add(handler);
        }

        public void UnsubscribeButtonPressed(Func<Task> handler)
        {
            _buttonpressedHandlers.Remove(handler);
        }

        public async Task SendAsync(Enums.ArduinoSignalCode cmd)
        {
            if (_serialPort.IsOpen)
            {
                await Task.Run(() => _serialPort.Write(new byte[] { (byte)cmd }, 0, 1));
            }
        }
    }
}