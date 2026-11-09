using ComplicatedMarketBoard.Integrations.Universalis;

namespace ComplicatedMarketBoard.Tests;

public sealed class UniversalisSchedulingTests
{
    private static TaskCompletionSource<int> Barrier() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task DownloadsStartBeforeProbesAndAllBranchesOverlap()
    {
        var listings = Barrier();
        var history = Barrier();
        var probes = Barrier();
        var order = new List<string>();
        var result = UniversalisInitialFetch.RunAsync(
            () => { order.Add("listings"); return listings.Task; },
            () => { order.Add("history"); return history.Task; },
            () => { order.Add("probes"); return probes.Task; });
        Assert.Equal(["listings", "history", "probes"], order);
        Assert.False(result.IsCompleted);
        probes.SetResult(3);
        listings.SetResult(1);
        Assert.False(result.IsCompleted);
        history.SetResult(2);
        Assert.Equal((1, 2, 3), await result.WaitAsync(Timeout));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationStillSettlesOtherStartedBranches(bool cancel)
    {
        var history = Barrier();
        var probes = Barrier();
        var result = UniversalisInitialFetch.RunAsync(
            () => cancel ? Task.FromCanceled<int>(new CancellationToken(true)) : throw new InvalidOperationException("factory failed"),
            () => history.Task, () => probes.Task);
        history.SetResult(2);
        Assert.False(result.IsCompleted);
        probes.SetResult(3);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result.WaitAsync(Timeout));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => result.WaitAsync(Timeout));
        Assert.True(history.Task.IsCompletedSuccessfully);
        Assert.True(probes.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task SharedBudgetAdmitsExactlyConfiguredCapacity(int capacity)
    {
        using var budget = new UniversalisRequestBudget(() => capacity);
        var holders = new List<IDisposable>();
        for (var index = 0; index < capacity; index++)
            holders.Add(await budget.EnterAsync(default));
        var queued = budget.EnterAsync(default);
        Assert.False(queued.IsCompleted);
        holders[0].Dispose();
        holders[0].Dispose(); // A duplicate release must not create a permit.
        using var admitted = await queued.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var stillQueued = budget.EnterAsync(cancellation.Token);
        Assert.False(stillQueued.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stillQueued);
        foreach (var holder in holders)
            holder.Dispose();
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(4, 1)]
    public async Task PreferenceChangesWaitForHoldersAndQueueToDrain(int oldLimit, int newLimit)
    {
        var desired = oldLimit;
        using var budget = new UniversalisRequestBudget(() => desired);
        var holders = new List<IDisposable>();
        for (var index = 0; index < oldLimit; index++)
            holders.Add(await budget.EnterAsync(default));
        var queued = budget.EnterAsync(default);
        desired = newLimit;
        Assert.Equal(oldLimit, budget.Concurrency);
        Assert.False(queued.IsCompleted);
        holders[0].Dispose();
        using (var admitted = await queued.WaitAsync(Timeout))
        {
            foreach (var holder in holders)
                holder.Dispose();
            Assert.Equal(oldLimit, budget.Concurrency);
        }
        Assert.Equal(newLimit, budget.Concurrency);
        var resized = new List<IDisposable>();
        for (var index = 0; index < newLimit; index++)
            resized.Add(await budget.EnterAsync(default).WaitAsync(Timeout));
        using var cancellation = new CancellationTokenSource();
        var overflow = budget.EnterAsync(cancellation.Token);
        Assert.False(overflow.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => overflow);
        foreach (var holder in resized)
            holder.Dispose();
    }

    [Fact]
    public async Task CancelledWaiterDoesNotPreventResizeAndDisposeWaitsForRelease()
    {
        var desired = 1;
        var disposedCount = 0;
        var budget = new UniversalisRequestBudget(() => desired, () => disposedCount++);
        var holder = await budget.EnterAsync(default);
        using var cancellation = new CancellationTokenSource();
        var queued = budget.EnterAsync(cancellation.Token);
        desired = 4;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        holder.Dispose();
        Assert.Equal(4, budget.Concurrency);
        holder = await budget.EnterAsync(default);
        budget.Dispose();
        Assert.Equal(0, disposedCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => budget.EnterAsync(default));
        holder.Dispose();
        holder.Dispose();
        budget.Dispose();
        Assert.Equal(1, disposedCount);
    }

    [Fact]
    public async Task DisposeKeepsQueuedWaitersSafeUntilTheyCancel()
    {
        var disposedCount = 0;
        var budget = new UniversalisRequestBudget(() => 1, () => disposedCount++);
        var holder = await budget.EnterAsync(default);
        using var cancellation = new CancellationTokenSource();
        var queued = budget.EnterAsync(cancellation.Token);
        budget.Dispose();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(0, disposedCount);
        holder.Dispose();
        Assert.Equal(1, disposedCount);
    }

}
