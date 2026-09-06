using System;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Scalemon.Common;

namespace Scalemon.SerialLink
{
    /// <summary>
    /// Прямой драйвер "Протокол 100" через RS-232/USB-COM.
    /// Реализует IScaleDriver для совместимости с существующим ScaleProcessor.
    /// </summary>
    public sealed class SerialPortScaleDriver100 : IScaleDriver, IDisposable
    {
        private readonly ILogger<SerialPortScaleDriver100> _log;
        private readonly object _sync = new();

        private SerialPort? _sp;
        private string _portName = "COM1";

        private int _lastResponseNum = 1;      // 0=OK; 1..=ошибки (см. LastResponseText)
        private string _lastResponseText = "Связь с весами не установлена";
        private bool _connected;               // истинное соединение подтверждаем только успешным обменом

        private decimal _weight;
        private bool _stable;
        private bool _scaleAlarm;
        private bool _hasFreshMeasurement;
        private decimal _divisionKg = 0.01m;
        private bool _isTerminalZero;
        private bool _isNet;
        private decimal? _tareKg;

        // Настройки порта (Протокол 2)
        private const int BaudRate = 4800;
        private const Parity PortParity = Parity.Even;
        private const int DataBits = 8;
        private const StopBits PortStopBits = StopBits.One;

        private const int ReadTimeoutMs = 3000;
        private const int WriteTimeoutMs = 500;

        public SerialPortScaleDriver100(ILogger<SerialPortScaleDriver100> log) => _log = log;

        public string PortConnection
        {
            get => _portName;
            set
            {
                lock (_sync)
                {
                    if (_sp?.IsOpen == true)
                        throw new InvalidOperationException("Нельзя менять порт при открытом соединении");
                    _portName = value ?? throw new ArgumentNullException(nameof(value));
                }
            }
        }

        public decimal Weight => _weight;
        public bool Stable => _stable;
        public decimal DivisionKg => _divisionKg;
        public bool IsTerminalZero => _isTerminalZero;
        public bool IsNet => _isNet;
        public decimal? TareKg => _tareKg;
        public bool HasFreshMeasurement => _hasFreshMeasurement;

        public long LastResponseNum => _lastResponseNum;
        public string LastResponseText => _lastResponseText;

        public bool IsConnected => _connected;
        public bool IsScaleAlarm => _scaleAlarm;

        public void OpenConnection()
        {
            lock (_sync)
            {
                try
                {
                    if (_sp?.IsOpen == true) return;

                    _sp = new SerialPort(_portName, BaudRate, PortParity, DataBits, PortStopBits)
                    {
                        ReadTimeout = ReadTimeoutMs,
                        WriteTimeout = WriteTimeoutMs,
                        DtrEnable = true,
                        RtsEnable = true
                    };
                    _sp.Open();

                    _connected = false; // подтвердим обменом
                    SetStatus(1, "Открыт COM, ожидается подтверждение обменом");
                    _log.LogDebug("Открыт порт {Port} для весов (4800, Even, 8N1)", _portName);
                }
                catch (Exception ex)
                {   
                    if (_connected)
                    SetStatus(1, $"Не удалось открыть порт: {ex.Message}");
                    _connected = false;

                    
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
                    if (_sp != null)
                    {
                        if (_sp.IsOpen) _sp.Close();
                        _sp.Dispose();
                    }
                }
                finally
                {
                    _sp = null;
                    _connected = false;
                    SetStatus(1, "Соединение закрыто");
                }
            }
        }

        public void ReadWeight()
        {
            lock (_sync)
            {
                _hasFreshMeasurement = false;
                _stable = false;

                try
                {
                    EnsureOpen();
                    // было: строили кадр и считали CRC сами
                    // стало: шлём проверенный кадр как в SpeedTest
                    byte[] cmdGetMassa = { 0xF8, 0x55, 0xCE, 0x01, 0x00, 0x23, 0x4E, 0x6E };

                    _sp!.DiscardInBuffer();
                    // _sp.DiscardOutBuffer(); // можно оставить или убрать; не критично
                    _sp.Write(cmdGetMassa, 0, cmdGetMassa.Length);

                    var body = ReadFrame(_sp);

                    if (body.Length == 0)
                    {
                        _connected = true;
                        SetStatus(2, "Пустой ответ весового терминала");
                        return;
                    }

                    byte cmd = body[0];
                    if (cmd == 0x24) // CMD_ACK_MASSA
                    {
                        if (!Protocol100MassParser.TryParse(body, out var reading, out var error))
                        {
                            _connected = true;
                            SetStatus(2, error);
                            return;
                        }

                        _weight = reading!.WeightKg;
                        _divisionKg = reading.DivisionKg;
                        _stable = reading.IsStable;
                        _isNet = reading.IsNet;
                        _isTerminalZero = reading.IsTerminalZero;
                        _tareKg = reading.TareKg;

                        _scaleAlarm = false;
                        _connected = true;
                        _hasFreshMeasurement = true;
                        SetStatus(0, "Ошибок нет");
                        return;
                    }
                    if (cmd == 0x28) // CMD_ERROR
                    {
                        byte err = body.Length > 1 ? body[1] : (byte)0xF0;
                        _scaleAlarm = err is 0x08 or 0x18 or 0x19;
                        _connected = err != 0x17;
                        MapError(err);
                        return;
                    }
                    if (cmd == 0xF0) // NACK
                    {
                        _connected = true;
                        SetStatus(7, "Команда не поддерживается (NACK)");
                        return;
                    }

                    _connected = true;
                    SetStatus(2, $"Неожиданный ответ 0x{cmd:X2}");
                }
                catch (TimeoutException)
                {
                    _connected = false;
                    SetStatus(1, "Таймаут чтения/ожидания ответа");
                    throw;
                }
                catch (Exception ex)
                {
                    _connected = false;
                    SetStatus(2, $"Ошибка обмена: {ex.Message}");
                    throw;
                }
            }
        }

        public ScaleCommandResult SetToZero(int timeoutMs) =>
            ExecuteControlCommand(
                command: 0x72,
                payload: null,
                successResponse: 0x27,
                directRejectResponse: null,
                operationName: "установке нуля",
                timeoutMs);

        public ScaleCommandResult SetTare(decimal tareKg, int timeoutMs)
        {
            int tareGrams;
            try
            {
                tareGrams = decimal.ToInt32(decimal.Round(
                    tareKg * 1000m,
                    0,
                    MidpointRounding.AwayFromZero));
            }
            catch (OverflowException)
            {
                return ScaleCommandResult.Rejected(0x0A, "Масса тары выходит за диапазон Int32");
            }

            if (tareGrams < 0)
                return ScaleCommandResult.Rejected(0x0A, "Явная масса тары не может быть отрицательной");

            return ExecuteControlCommand(
                command: 0xA3,
                payload: BitConverter.GetBytes(tareGrams),
                successResponse: 0x12,
                directRejectResponse: 0x15,
                operationName: $"установке тары {tareKg:0.###} кг",
                timeoutMs);
        }

        public ScaleCommandResult TareCurrentWeight(int timeoutMs) =>
            ExecuteControlCommand(
                command: 0xA3,
                payload: new byte[4],
                successResponse: 0x12,
                directRejectResponse: 0x15,
                operationName: "тарировании текущей нагрузки",
                timeoutMs);

        private ScaleCommandResult ExecuteControlCommand(
            byte command,
            byte[]? payload,
            byte successResponse,
            byte? directRejectResponse,
            string operationName,
            int timeoutMs)
        {
            lock (_sync)
            {
                _hasFreshMeasurement = false;
                var previousReadTimeout = ReadTimeoutMs;
                var previousWriteTimeout = WriteTimeoutMs;

                try
                {
                    EnsureOpen();
                    previousReadTimeout = _sp!.ReadTimeout;
                    previousWriteTimeout = _sp.WriteTimeout;
                    var commandTimeout = Math.Clamp(timeoutMs, 100, 3000);
                    _sp.ReadTimeout = commandTimeout;
                    _sp.WriteTimeout = commandTimeout;

                    WriteCommand(_sp, command, payload);
                    var body = ReadFrame(_sp);
                    if (body.Length == 0)
                    {
                        _connected = true;
                        SetStatus(2, $"Пустой ответ при {operationName}");
                        return ScaleCommandResult.Rejected(_lastResponseNum, _lastResponseText);
                    }

                    byte response = body[0];
                    if (response == successResponse)
                    {
                        _connected = true;
                        SetStatus(0, $"Команда выполнена: {operationName}");
                        return ScaleCommandResult.Success(response, _lastResponseText);
                    }

                    if (directRejectResponse.HasValue && response == directRejectResponse.Value)
                    {
                        _connected = true;
                        SetStatus(response, "Установка тары невозможна");
                        return ScaleCommandResult.Rejected(_lastResponseNum, _lastResponseText);
                    }

                    if (response == 0x28)
                    {
                        byte error = body.Length > 1 ? body[1] : (byte)0xF0;
                        _connected = error != 0x17;
                        MapError(error);
                        return error == 0x17
                            ? ScaleCommandResult.TransportFailure(_lastResponseText)
                            : ScaleCommandResult.Rejected(_lastResponseNum, _lastResponseText);
                    }

                    if (response == 0xF0)
                    {
                        _connected = true;
                        SetStatus(7, "Команда не поддерживается (NACK)");
                        return ScaleCommandResult.Rejected(_lastResponseNum, _lastResponseText);
                    }

                    _connected = true;
                    SetStatus(2, $"Неожиданный ответ при {operationName}: 0x{response:X2}");
                    return ScaleCommandResult.Rejected(_lastResponseNum, _lastResponseText);
                }
                catch (TimeoutException)
                {
                    _connected = false;
                    SetStatus(1, $"Тайм-аут при {operationName}");
                    return ScaleCommandResult.TransportFailure(_lastResponseText);
                }
                catch (Exception ex)
                {
                    _connected = false;
                    SetStatus(2, $"Ошибка обмена при {operationName}: {ex.Message}");
                    return ScaleCommandResult.TransportFailure(_lastResponseText);
                }
                finally
                {
                    if (_sp?.IsOpen == true)
                    {
                        _sp.ReadTimeout = previousReadTimeout;
                        _sp.WriteTimeout = previousWriteTimeout;
                    }
                }
            }
        }

        // ===== Helpers =====

        private void EnsureOpen()
        {
            if (_sp == null || !_sp.IsOpen)
                throw new InvalidOperationException("COM-порт не открыт");
        }

        private static void WriteCommand(SerialPort sp, byte command, byte[]? payload)
        {
            int payloadLen = payload?.Length ?? 0;
            int len = 1 + payloadLen;

            var hdr = new byte[5];
            hdr[0] = 0xF8; hdr[1] = 0x55; hdr[2] = 0xCE;
            hdr[3] = (byte)(len & 0xFF);
            hdr[4] = (byte)(len >> 8);

            ushort crc;
            if (payloadLen == 0)
            {
                crc = Crc16Ccitt(new[] { command }, 0, 1);
            }
            else
            {
                // посчитать CRC по [command|payload]
                var tmp = new byte[1 + payloadLen];
                tmp[0] = command;
                Buffer.BlockCopy(payload!, 0, tmp, 1, payloadLen);
                crc = Crc16Ccitt(tmp, 0, tmp.Length);
            }

            var tail = new byte[2] { (byte)(crc & 0xFF), (byte)(crc >> 8) };

            sp.DiscardInBuffer();
            sp.DiscardOutBuffer();

            sp.Write(hdr, 0, hdr.Length);
            sp.Write(new[] { command }, 0, 1);
            if (payloadLen > 0) sp.Write(payload!, 0, payloadLen);
            sp.Write(tail, 0, tail.Length);
        }

        private static byte[] ReadFrame(SerialPort sp)
        {
            int b;
            do { b = sp.ReadByte(); } while (b != 0xF8);
            if (sp.ReadByte() != 0x55) throw new TimeoutException();
            if (sp.ReadByte() != 0xCE) throw new TimeoutException();

            int lenLo = sp.ReadByte();
            int lenHi = sp.ReadByte();
            int len = lenLo | (lenHi << 8);
            if (len is < 1 or > 4096)
                throw new InvalidOperationException($"Некорректная длина кадра: {len}");

            var body = new byte[len];
            ReadExact(sp, body, 0, len);

            var crc = new byte[2];
            ReadExact(sp, crc, 0, 2); // пока просто вычитываем, без проверки

            return body;
        }

        private static void ReadExact(SerialPort sp, byte[] buf, int offset, int count)
        {
            int left = count;
            while (left > 0)
            {
                int read = sp.Read(buf, offset, left);
                if (read <= 0) throw new TimeoutException();
                offset += read;
                left -= read;
            }
        }

        // CRC-16-CCITT (poly=0x1021, init=0xFFFF, no reflect)
        private static ushort Crc16Ccitt(byte[] data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++)
            {
                crc ^= (ushort)(data[offset + i] << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((crc & 0x8000) != 0) crc = (ushort)((crc << 1) ^ 0x1021);
                    else crc = (ushort)(crc << 1);
                }
            }
            return crc;
        }

        private void MapError(byte err)
        {
            int num = err;
            string text = err switch
            {
                0x07 => "Команда не поддерживается",
                0x08 => "Нагрузка на весовом устройстве превышает НПВ",
                0x09 => "Весовое устройство не в режиме взвешивания",
                0x0A => "Ошибка входных данных",
                0x0B => "Ошибка сохранения данных",
                0x10 => "Интерфейс Wi-Fi не поддерживается",
                0x11 => "Интерфейс Ethernet не поддерживается",
                0x15 => "Установка >0< невозможна",
                0x17 => "Нет связи с модулем взвешивающим",
                0x18 => "Установлена нагрузка при включении",
                0x19 => "Весовое устройство неисправно",
                _ => "Неизвестная ошибка"
            };

            SetStatus(num, text);
        }

        private void SetStatus(int code, string text)
        {
            _lastResponseNum = code;
            _lastResponseText = text;
        }

        public void Dispose() => CloseConnection();
    }
}
