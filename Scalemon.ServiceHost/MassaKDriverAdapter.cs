using Scalemon.Common;

namespace Scalemon.ServiceHost
{
    public class MassaKDriverAdapter : IScaleDriver
    {
        private readonly Scalemon.MassaKInterop.IScaleDriver _inner;

        public MassaKDriverAdapter(Scalemon.MassaKInterop.IScaleDriver inner)
        {
            _inner = inner;
        }

        public string PortConnection
        {
            get => _inner.PortConnection;
            set => _inner.PortConnection = value;
        }

        public decimal Weight => _inner.Weight;
        public bool Stable => _inner.Stable;
        public string LastResponseText => _inner.LastResponseText;
        public long LastResponseNum => _inner.LastResponseNum;
        public bool IsConnected => _inner.isConnected;
        public bool IsScaleAlarm => _inner.isScaleAlarm;

        public void OpenConnection() => _inner.OpenConnection();
        public void CloseConnection() => _inner.CloseConnection();
        public void SetToZero() => _inner.SetToZero();
        public void ReadWeight() => _inner.ReadWeight();
    }
}
