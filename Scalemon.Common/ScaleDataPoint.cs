using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scalemon.Common
{
    public class ScaleDataPoint
    {
        public decimal WeightKg { get; }
        public bool IsStable { get; }
        public bool IsConnected { get; }
        public bool IsAlarm { get; }

        public ScaleDataPoint(decimal weightKg, bool isStable, bool isConnected, bool isAlarm)
        {
            WeightKg = weightKg;
            IsStable = isStable;
            IsConnected = isConnected;
            IsAlarm = isAlarm;
        }
    }

}