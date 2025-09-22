using Microsoft.Extensions.Logging;
using Scalemon.Common;
using System;
using System.Threading.Tasks;
using static Scalemon.Common.Enums;

namespace Scalemon.FSM
{
    public sealed class PlateauZeroFsmAdapter : IScaleStateMachine
    {
        private readonly PlateauZeroStateMachine _core;
        private readonly ILogger _log;

        public PlateauZeroFsmAdapter(PlateauZeroStateMachine core, ILogger logger)
        {
            _core = core;
            _log = logger;
        }
        public FsmState CurrentState => _core.CurrentState;
        public Task SetConnectionAsync(bool isConnected) 
        { 
            _core.SetConnection(isConnected); 
            return Task.CompletedTask; 
        }
        public Task SetAlarmAsync(bool isAlarm) => _core.SetAlarmAsync(isAlarm);
        public Task OnWeightSampleAsync(decimal weightKg) => _core.OnSampleAsync(weightKg);

        public Task OnDatabaseFailedAsync(Exception ex) { _log.LogError(ex, "DB failed"); return Task.CompletedTask; }
        public Task OnDatabaseRestoredAsync() { _log.LogInformation("DB restored"); return Task.CompletedTask; }
        public Task OnButtonPressedAsync() { _log.LogInformation("Button pressed"); return Task.CompletedTask; }
    }
}
