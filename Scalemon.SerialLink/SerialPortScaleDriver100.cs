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
                    _log.LogInformation("Открыт порт {Port} для весов (4800, Even, 8N1)", _portName);
                }
                catch (Exception ex)
                {
                    _connected = false;
                    SetStatus(1, $"Не удалось открыть порт: {ex.Message}");
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
                EnsureOpen();

                try
                {
                    // Запрос: F8 55 CE | 01 00 | 23 | CRC(lo hi)
                    var frame = new byte[8];
                    frame[0] = 0xF8; frame[1] = 0x55; frame[2] = 0xCE;
                    frame[3] = 0x01; frame[4] = 0x00;           // Len = 1
                    frame[5] = 0x23;                            // CMD_GET_MASSA
                    ushort crc = Crc16Ccitt(frame, 5, 1);
                    frame[6] = (byte)(crc & 0xFF);              // CRC LSB
                    frame[7] = (byte)(crc >> 8);                // CRC MSB

                    _sp!.DiscardInBuffer();
                    _sp.DiscardOutBuffer();
                    _sp.Write(frame, 0, frame.Length);

                    var body = ReadFrame(_sp);

                    byte cmd = body[0];
                    if (cmd == 0x24) // CMD_ACK_MASSA
                    {
                        // [0]=0x24, [1..4]=Int32 Weight, [5]=Division, [6]=Stable, [7]=Net, [8]=Zero, [9..12]?=Tare
                        int raw = BitConverter.ToInt32(body, 1);
                        byte division = body[5];
                        double stepKg = division switch
                        {
                            0 => 0.0001, // 100 мг
                            1 => 0.001,  // 1 г
                            2 => 0.01,   // 10 г
                            3 => 0.1,    // 100 г
                            4 => 1.0,    // 1 кг
                            _ => 0.001
                        };

                        _weight = (decimal)(raw * stepKg);
                        _stable = body[6] != 0;

                        _scaleAlarm = false;
                        _connected = true;
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

        public void SetToZero()
        {
            lock (_sync)
            {
                EnsureOpen();
                try
                {
                    WriteCommand(_sp!, 0x72, null);           // CMD_SET_ZERO
                    var body = ReadFrame(_sp!);
                    byte cmd = body[0];

                    if (cmd == 0x27) // CMD_ACK_SET
                    {
                        _connected = true;
                        SetStatus(0, "Установлен >0<");
                        return;
                    }
                    if (cmd == 0x28)
                    {
                        byte err = body.Length > 1 ? body[1] : (byte)0xF0;
                        _connected = err != 0x17;
                        MapError(err); // 0x15 — «Установка >0< невозможна»
                        return;
                    }
                    if (cmd == 0xF0)
                    {
                        _connected = true;
                        SetStatus(7, "Команда не поддерживается (NACK)");
                        return;
                    }

                    _connected = true;
                    SetStatus(2, $"Неожиданный ответ на ZERO: 0x{cmd:X2}");
                }
                catch (TimeoutException)
                {
                    _connected = false;
                    SetStatus(1, "Таймаут при установке >0<");
                    throw;
                }
                catch (Exception ex)
                {
                    _connected = false;
                    SetStatus(2, $"Ошибка при установке >0<: {ex.Message}");
                    throw;
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
            // поиск заголовка F8 55 CE
            int b;
            do { b = sp.ReadByte(); } while (b != 0xF8);
            if (sp.ReadByte() != 0x55) throw new TimeoutException();
            if (sp.ReadByte() != 0xCE) throw new TimeoutException();

            int lenLo = sp.ReadByte();
            int lenHi = sp.ReadByte();
            int len = lenLo | (lenHi << 8);

            var body = new byte[len];
            ReadExact(sp, body, 0, len);

            var crc = new byte[2];
            ReadExact(sp, crc, 0, 2);

            ushort calc = Crc16Ccitt(body, 0, body.Length);
            ushort got = (ushort)(crc[0] | (crc[1] << 8));
            if (calc != got)
                throw new TimeoutException(); // трактуем как ошибку обмена

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
