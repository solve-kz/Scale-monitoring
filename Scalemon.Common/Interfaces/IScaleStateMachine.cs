using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Scalemon.Common.Enums;

namespace Scalemon.Common
{
    public interface IScaleStateMachine
    {
        FsmState CurrentState { get; }

        /// <summary>
        /// Показывает, что технологическая ошибка защёлкнута до подтверждённого нуля.
        /// </summary>
        bool HasLatchedProcessError { get; }

        Task SetConnectionAsync(bool isConnected);
        Task SetAlarmAsync(bool isAlarm);

        /// <summary>
        /// Передаёт в FSM полный снимок свежего ответа весового терминала.
        /// </summary>
        Task OnScaleSampleAsync(ScaleDataPoint sample);

        Task OnDatabaseFailedAsync(Exception ex); // без изменений в остальной системе
        Task OnDatabaseRestoredAsync();
        Task OnButtonPressedAsync();
    }
}
