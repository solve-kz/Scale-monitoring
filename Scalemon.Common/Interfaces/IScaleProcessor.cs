namespace Scalemon.Common
{
    public interface IScaleProcessor : IDisposable
    {
        /// <summary>
        /// Запускает фоновый опрос весового терминала.
        /// </summary>
        void Start();

        /// <summary>
        /// Останавливает фоновый опрос весового терминала.
        /// </summary>
        void Stop();

        // Заменяем старые события на одно, более информативное
        event Func<ScaleDataPoint, Task>? DataReceived;

        /// <summary>
        /// Выполняет одну команду установки нуля.
        /// </summary>
        Task<ScaleCommandResult> ResetToZeroAsync(int timeoutMs, CancellationToken ct = default);

        /// <summary>
        /// Выполняет одну команду установки указанной массы тары.
        /// </summary>
        Task<ScaleCommandResult> SetTareAsync(decimal tareKg, int timeoutMs, CancellationToken ct = default);

        /// <summary>
        /// Выполняет одну команду тарирования текущей нагрузки.
        /// </summary>
        Task<ScaleCommandResult> TareCurrentWeightAsync(int timeoutMs, CancellationToken ct = default);
    }
}
