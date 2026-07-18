using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scalemon.Common
{
    /// <summary>
    /// Снимок результата одного обмена с весовым терминалом.
    /// </summary>
    public sealed class ScaleDataPoint
    {
        public decimal WeightKg { get; }
        public bool IsStable { get; }
        public bool IsConnected { get; }
        public bool IsAlarm { get; }
        public bool HasFreshMeasurement { get; }
        public decimal DivisionKg { get; }
        public bool IsTerminalZero { get; }
        public bool IsNet { get; }
        public decimal? TareKg { get; }

        /// <summary>
        /// Создаёт снимок состояния весового терминала.
        /// </summary>
        public ScaleDataPoint(
            decimal weightKg,
            bool isStable,
            bool isConnected,
            bool isAlarm,
            bool hasFreshMeasurement = true,
            decimal divisionKg = 0.01m,
            bool isTerminalZero = false,
            bool isNet = false,
            decimal? tareKg = null)
        {
            WeightKg = weightKg;
            IsStable = isStable;
            IsConnected = isConnected;
            IsAlarm = isAlarm;
            HasFreshMeasurement = hasFreshMeasurement;
            DivisionKg = divisionKg;
            IsTerminalZero = isTerminalZero;
            IsNet = isNet;
            TareKg = tareKg;
        }
    }

}
