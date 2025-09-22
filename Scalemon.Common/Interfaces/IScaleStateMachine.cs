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
        Task SetConnectionAsync(bool isConnected);
        Task SetAlarmAsync(bool isAlarm);
        Task OnWeightSampleAsync(decimal weightKg);

        Task OnDatabaseFailedAsync(Exception ex); // без изменений в остальной системе
        Task OnDatabaseRestoredAsync();
        Task OnButtonPressedAsync();
    }
}
