namespace Scalemon.Common.Updates;

/// <summary>Общий барьер изменений данных, включая операции из Blazor circuit.</summary>
public sealed class MaintenanceGate
{
    public static MaintenanceGate Shared { get; } = new();
    private readonly object _sync = new();
    private readonly AsyncLocal<int> _depth = new();
    private bool _closed;
    private int _active;
    public bool IsClosed { get { lock (_sync) return _closed; } }
    public int Active { get { lock (_sync) return _active; } }
    /// <summary>Начинает операцию записи; вложенные вызовы завершаются под исходным разрешением.</summary>
    public IDisposable Enter()
    {
        lock (_sync)
        {
            if (_depth.Value == 0 && _closed) throw new InvalidOperationException("Scalemon готовится к обновлению. Изменения временно недоступны.");
            _active++; _depth.Value++;
            return new Lease(this);
        }
    }
    /// <summary>Запрещает новые операции до ожидания существующих.</summary>
    public void Close() { lock (_sync) _closed = true; }
    /// <summary>Возвращает рабочий режим.</summary>
    public void Open() { lock (_sync) _closed = false; }
    /// <summary>Ожидает завершения допущенных операций.</summary>
    public async Task WaitAsync(CancellationToken ct)
    { while (Active != 0) await Task.Delay(50, ct); }
    private sealed class Lease(MaintenanceGate gate) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (gate._sync)
            {
                if (_disposed) return;
                _disposed = true; gate._active--; gate._depth.Value--;
            }
        }
    }
}
