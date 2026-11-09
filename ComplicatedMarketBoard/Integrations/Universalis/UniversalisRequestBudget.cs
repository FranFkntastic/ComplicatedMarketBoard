using System.Threading;

namespace ComplicatedMarketBoard.Integrations.Universalis;

// A preference change applies only between batches, never alongside an old budget.
internal sealed class UniversalisRequestBudget(Func<int> configuredConcurrency, Action? onDisposed = null) : IDisposable
{
    internal const int DefaultConcurrency = 3;
    internal const int MinimumConcurrency = 1;
    internal const int MaximumConcurrency = 8;
    private readonly object sync = new();
    private SemaphoreSlim? gate;
    private int concurrency;
    private int registered;
    private bool disposed;
    private bool resourcesDisposed;

    internal static int Normalize(int value) => Math.Clamp(value, MinimumConcurrency, MaximumConcurrency);

    internal int Concurrency
    {
        get
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                RefreshIdleBudget();
                return concurrency;
            }
        }
    }

    internal async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        SemaphoreSlim captured;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RefreshIdleBudget();
            registered++;
            captured = gate!;
        }

        try
        {
            await captured.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Exit(captured, acquired: false);
            throw;
        }
        return new Lease(this, captured);
    }

    private void RefreshIdleBudget()
    {
        if (registered != 0)
            return;
        var desired = Normalize(configuredConcurrency());
        if (gate is not null && desired == concurrency)
            return;
        gate?.Dispose();
        concurrency = desired;
        gate = new SemaphoreSlim(concurrency, concurrency);
    }

    private void Exit(SemaphoreSlim captured, bool acquired)
    {
        lock (sync)
        {
            if (acquired)
                captured.Release();
            registered--;
            if (registered == 0 && disposed)
                DisposeResources();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            if (registered == 0)
                DisposeResources();
        }
    }

    private void DisposeResources()
    {
        if (resourcesDisposed)
            return;
        resourcesDisposed = true;
        gate?.Dispose();
        onDisposed?.Invoke();
    }

    private sealed class Lease(UniversalisRequestBudget owner, SemaphoreSlim gate) : IDisposable
    {
        private UniversalisRequestBudget? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Exit(gate, acquired: true);
    }
}
