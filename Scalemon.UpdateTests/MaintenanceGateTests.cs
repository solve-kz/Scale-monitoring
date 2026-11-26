using Scalemon.Common.Updates;

namespace Scalemon.UpdateTests;

public sealed class MaintenanceGateTests
{
    [Fact]
    public void DequeuedAndRetryingWriteRemainsPendingUntilCommit()
    {
        var registry = new PendingWriteRegistry();
        var queue = new Queue<PendingWriteRegistry.Ticket>();
        queue.Enqueue(registry.Begin());
        var inFlight = queue.Dequeue();
        Assert.Empty(queue);
        Assert.Equal(1, registry.Count);
        queue.Enqueue(inFlight); // Неудачная попытка не подтверждает запись.
        Assert.Equal(1, registry.Count);
        queue.Dequeue().Complete();
        Assert.Equal(0, registry.Count);
        inFlight.Complete();
        Assert.Equal(0, registry.Count);
    }
    [Fact]
    public async Task PreparationWaitsForExistingWriterAndRejectsNewWriter()
    {
        var gate = new MaintenanceGate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () => { using var operation = gate.Enter(); entered.SetResult(); await finish.Task; });
        await entered.Task;
        gate.Close();
        Assert.Throws<InvalidOperationException>(() => gate.Enter());
        var wait = gate.WaitAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);
        finish.SetResult(); await writer;
        await wait.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, gate.Active);
        gate.Open(); using var next = gate.Enter(); Assert.Equal(1, gate.Active);
    }
    [Fact]
    public async Task NestedPersistenceCompletesEvenAfterBarrierCloses()
    {
        var gate = new MaintenanceGate();
        using (gate.Enter())
        {
            gate.Close(); await Task.Yield();
            using (gate.Enter()) Assert.Equal(2, gate.Active);
        }
        Assert.Equal(0, gate.Active);
        Assert.Throws<InvalidOperationException>(() => gate.Enter());
    }
    [Fact]
    public async Task DrainTimeoutDoesNotForgetOutstandingOperation()
    {
        var gate = new MaintenanceGate(); using var operation = gate.Enter(); gate.Close();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitAsync(cancelled.Token));
        Assert.Equal(1, gate.Active);
    }
}
