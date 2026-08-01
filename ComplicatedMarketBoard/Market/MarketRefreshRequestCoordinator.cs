using System.Threading;

namespace ComplicatedMarketBoard.Market;

public sealed record MarketRefreshRequestKey(
    ulong ItemId,
    string TargetScope,
    bool RequireCurrentDetails);

public sealed record MarketRefreshRequestContext(
    int Version,
    MarketRefreshRequestKey Key,
    CancellationTokenSource Cancellation);

public sealed class MarketRefreshRequestCoordinator : IDisposable
{
    private readonly object requestLock = new();
    private MarketRefreshRequestContext? activeRequest;
    private int requestVersion;

    public MarketRefreshRequestContext BeginSuperseding(MarketRefreshRequestKey key)
    {
        lock (requestLock)
            return BeginRequest(key);
    }

    public MarketRefreshRequestContext? TryBeginCoalesced(MarketRefreshRequestKey key)
    {
        lock (requestLock)
        {
            if (activeRequest is { } current
                && !current.Cancellation.IsCancellationRequested
                && current.Key == key)
            {
                return null;
            }

            return BeginRequest(key);
        }
    }

    public bool IsCurrent(MarketRefreshRequestContext request)
    {
        lock (requestLock)
            return request.Version == requestVersion
                   && !request.Cancellation.IsCancellationRequested;
    }

    public void End(MarketRefreshRequestContext request)
    {
        lock (requestLock)
        {
            if (request.Version == requestVersion)
                activeRequest = null;
        }

        request.Cancellation.Dispose();
    }

    public void Dispose()
    {
        lock (requestLock)
        {
            activeRequest?.Cancellation.Cancel();
            activeRequest = null;
        }
    }

    private MarketRefreshRequestContext BeginRequest(MarketRefreshRequestKey key)
    {
        activeRequest?.Cancellation.Cancel();
        var cancellation = new CancellationTokenSource();
        var request = new MarketRefreshRequestContext(++requestVersion, key, cancellation);
        activeRequest = request;
        return request;
    }
}
