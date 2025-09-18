namespace Scalemon.Common
{
    public interface IScaleProcessor : IDisposable
    {
        void Start();
        void Stop();

        // События вместо Subscribe*/Unsubscribe*
        event Func<decimal, Task>? WeightReceived;    // стабильный вес
        event Func<Task>? Unstable;          // нестабильно после порога
        event Func<Task>? Connected;         // связь появилась
        event Func<Task>? Disconnected;      // связь пропала
        event Func<Task>? ScaleAlarm;        // авария (перегруз и т.п.)

        Task ResetToZeroAsync(CancellationToken ct = default);
    }
}
