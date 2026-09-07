namespace Scalemon.Common.Updates;

/// <summary>Считает незавершённые записи независимо от того, находятся ли они в очереди или уже выполняются.</summary>
public sealed class PendingWriteRegistry
{
    private int _pending;
    public int Count => Volatile.Read(ref _pending);
    /// <summary>Регистрирует запись до первой попытки сохранения.</summary>
    public Ticket Begin() { Interlocked.Increment(ref _pending); return new(this); }
    /// <summary>Одна запись остаётся учтённой через все повторные попытки.</summary>
    public sealed class Ticket(PendingWriteRegistry owner)
    {
        private int _completed;
        /// <summary>Подтверждает сохранение; повторное подтверждение не меняет счётчик.</summary>
        public void Complete()
        { if (Interlocked.Exchange(ref _completed, 1) == 0) Interlocked.Decrement(ref owner._pending); }
    }
}
