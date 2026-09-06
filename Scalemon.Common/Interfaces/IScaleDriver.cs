using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scalemon.Common
{
    /// <summary>
    /// Определяет низкоуровневый контракт для драйвера, напрямую взаимодействующего с COM-портом весов "Масса-К".
    /// </summary>
    public interface IScaleDriver
    {
        /// <summary>
        /// Открывает соединение с COM-портом.
        /// </summary>
        void OpenConnection();

        /// <summary>
        /// Закрывает соединение с COM-портом.
        /// </summary>
        void CloseConnection();

        /// <summary>
        /// Отправляет команду сброса веса на ноль.
        /// </summary>
        ScaleCommandResult SetToZero(int timeoutMs);

        /// <summary>
        /// Устанавливает указанную массу тары.
        /// </summary>
        ScaleCommandResult SetTare(decimal tareKg, int timeoutMs);

        /// <summary>
        /// Тарирует текущую нагрузку нулевым параметром CMD_SET_TARE.
        /// </summary>
        ScaleCommandResult TareCurrentWeight(int timeoutMs);

        /// <summary>
        /// Отправляет команду запроса текущего веса.
        /// </summary>
        void ReadWeight();

        /// <summary>
        /// Имя COM-порта для подключения (например, "COM1").
        /// </summary>
        string PortConnection { get; set; }

        /// <summary>
        /// Возвращает последнее считанное значение веса.
        /// </summary>
        decimal Weight { get; }

        /// <summary>
        /// Возвращает флаг, указывающий, является ли вес стабильным.
        /// </summary>
        bool Stable { get; }

        /// <summary>
        /// Возвращает цену деления последнего свежего измерения.
        /// </summary>
        decimal DivisionKg { get; }

        /// <summary>
        /// Возвращает штатный признак нуля терминала.
        /// </summary>
        bool IsTerminalZero { get; }

        /// <summary>
        /// Возвращает штатный признак NET терминала.
        /// </summary>
        bool IsNet { get; }

        /// <summary>
        /// Возвращает тару из ответа терминала, если поле присутствовало.
        /// </summary>
        decimal? TareKg { get; }

        /// <summary>
        /// Показывает, что последнее чтение дало новый корректно разобранный ответ массы.
        /// </summary>
        bool HasFreshMeasurement { get; }

        /// <summary>
        /// Возвращает числовой код последнего ответа от весов.
        /// </summary>
        long LastResponseNum { get; }

        /// <summary>
        /// Возвращает текстовое описание последнего ответа от весов.
        /// </summary>
        string LastResponseText { get; }

        /// <summary>
        /// Возвращает флаг, указывающий, установлено ли соединение с портом.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Возвращает флаг, указывающий на наличие аппаратной ошибки весов.
        /// </summary>
        bool IsScaleAlarm { get; }
    }
}
