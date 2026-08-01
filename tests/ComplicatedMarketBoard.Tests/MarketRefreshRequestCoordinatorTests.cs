using ComplicatedMarketBoard.Market;

namespace ComplicatedMarketBoard.Tests;

public sealed class MarketRefreshRequestCoordinatorTests
{
    [Fact]
    public void SameItemAndScope_CoalescesWithoutCancellingActiveRefresh()
    {
        using var coordinator = new MarketRefreshRequestCoordinator();
        var key = new MarketRefreshRequestKey(22528, "Siren", RequireCurrentDetails: true);
        var first = Assert.IsType<MarketRefreshRequestContext>(coordinator.TryBeginCoalesced(key));

        var duplicate = coordinator.TryBeginCoalesced(key);

        Assert.Null(duplicate);
        Assert.False(first.Cancellation.IsCancellationRequested);
        Assert.True(coordinator.IsCurrent(first));
        coordinator.End(first);
    }

    [Fact]
    public void ChangedScope_SupersedesAndCancelsActiveRefresh()
    {
        using var coordinator = new MarketRefreshRequestCoordinator();
        var first = Assert.IsType<MarketRefreshRequestContext>(
            coordinator.TryBeginCoalesced(new MarketRefreshRequestKey(22528, "Siren", RequireCurrentDetails: true)));

        var second = Assert.IsType<MarketRefreshRequestContext>(
            coordinator.TryBeginCoalesced(new MarketRefreshRequestKey(22528, "Aether", RequireCurrentDetails: true)));

        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(first));
        Assert.True(coordinator.IsCurrent(second));
        coordinator.End(first);
        coordinator.End(second);
    }

    [Fact]
    public void VerifiedRefresh_SupersedesUnverifiedLookupForSameItemAndScope()
    {
        using var coordinator = new MarketRefreshRequestCoordinator();
        var lookup = coordinator.BeginSuperseding(
            new MarketRefreshRequestKey(22528, "Siren", RequireCurrentDetails: false));

        var refresh = Assert.IsType<MarketRefreshRequestContext>(
            coordinator.TryBeginCoalesced(
                new MarketRefreshRequestKey(22528, "Siren", RequireCurrentDetails: true)));

        Assert.True(lookup.Cancellation.IsCancellationRequested);
        Assert.True(coordinator.IsCurrent(refresh));
        coordinator.End(lookup);
        coordinator.End(refresh);
    }
}
