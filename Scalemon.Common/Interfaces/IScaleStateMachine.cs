using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scalemon.Common
{
    public interface IScaleStateMachine
    {
        Task SetConnectionAsync(bool isConnected);
        Task SetAlarmAsync(bool isAlarm);
        Task OnWeightSampleAsync(decimal weightKg);

        Task OnDatabaseFailedAsync(Exception ex); // без изменений в остальной системе
        Task OnDatabaseRestoredAsync();
        Task OnButtonPressedAsync();
    }
}
