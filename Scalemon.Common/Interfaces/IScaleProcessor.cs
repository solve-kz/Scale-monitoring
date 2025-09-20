namespace Scalemon.Common
{
    public interface IScaleProcessor : IDisposable
    {
        void Start();
        void Stop();

        // Заменяем старые события на одно, более информативное
        event Func<ScaleDataPoint, Task>? DataReceived;

        Task ResetToZeroAsync(CancellationToken ct = default);
    }
}
