using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scalemon.Common
{
    
    /// <summary>
    /// Определяет контракт для конечного автомата (FSM), управляющего логикой взвешивания.
    /// Предоставляет методы-обработчики для всех внешних событий системы.
    /// </summary>
    public interface IScaleStateMachine
    {
        /// <summary>
        /// Обрабатывает событие установления связи с весами.
        /// </summary>
        Task OnScaleConnectedAsync();

        /// <summary>
        /// Обрабатывает событие потери связи с весами.
        /// </summary>
        Task OnScaleDisconnectedAsync();

        /// <summary>
        /// Обрабатывает событие, когда вес на платформе становится нестабильным.
        /// </summary>
        Task OnScaleUnstableAsync();

        /// <summary>
        /// Обрабатывает событие аппаратной ошибки весов.
        /// </summary>
        Task OnScaleAlarmAsync();

        /// <summary>
        /// Обрабатывает получение нового стабильного значения веса от процессора.
        /// </summary>
        /// <param name="raw">Необработанное значение веса.</param>
        Task OnWeightReceivedAsync(decimal raw);

        /// <summary>
        /// Обрабатывает событие сбоя при записи в базу данных.
        /// </summary>
        /// <param name="ex">Исключение, вызвавшее сбой.</param>
        Task OnDatabaseFailedAsync(Exception ex);

        /// <summary>
        /// Обрабатывает событие восстановления связи с базой данных.
        /// </summary>
        Task OnDatabaseRestoredAsync();

        /// <summary>
        /// Обрабатывает событие нажатия кнопки на пульте Arduino.
        /// </summary>
        Task OnButtonPressedAsync();
    }
}

