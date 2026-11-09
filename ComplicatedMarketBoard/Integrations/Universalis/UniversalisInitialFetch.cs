namespace ComplicatedMarketBoard.Integrations.Universalis;

internal static class UniversalisInitialFetch
{
    internal static async Task<(T Listings, T History, TProbes Probes)> RunAsync<T, TProbes>(
        Func<Task<T>> listings, Func<Task<T>> history, Func<Task<TProbes>> probes)
    {
        // Admit exact downloads before the probe fan-out fills the shared queue.
        var listingsTask = Start(listings);
        var historyTask = Start(history);
        var probesTask = Start(probes);
        // Observe every started branch, including when a factory throws synchronously.
        await Task.WhenAll(listingsTask, historyTask, probesTask).ConfigureAwait(false);
        return (await listingsTask, await historyTask, await probesTask);
    }

    private static Task<T> Start<T>(Func<Task<T>> factory)
    {
        try { return factory(); }
        catch (Exception exception) { return Task.FromException<T>(exception); }
    }
}
