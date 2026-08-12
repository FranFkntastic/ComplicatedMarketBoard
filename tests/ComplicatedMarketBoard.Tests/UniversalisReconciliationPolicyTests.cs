using ComplicatedMarketBoard.Integrations.Universalis;
using ComplicatedMarketBoard.Market;

namespace ComplicatedMarketBoard.Tests;

public sealed class UniversalisReconciliationPolicyTests
{
    [Fact]
    public void OrdinaryCompletePageSchedulesNoOverfetch()
    {
        var next = UniversalisListingFetchPolicy.GetNextRequestLimit(
            desiredUniqueListings: 70,
            currentRequestLimit: 70,
            rawListingCount: 70,
            uniqueListingCount: 70);

        Assert.Null(next);
    }

    [Fact]
    public void DuplicateLimitedPageExpandsUntilExhaustionIsObservable()
    {
        Assert.Equal(140, UniversalisListingFetchPolicy.GetNextRequestLimit(70, 70, 70, 28));
        Assert.Equal(280, UniversalisListingFetchPolicy.GetNextRequestLimit(70, 140, 140, 28));
        Assert.Null(UniversalisListingFetchPolicy.GetNextRequestLimit(70, 280, 252, 28));
    }

    [Fact]
    public void AdaptiveOverfetchIsBounded()
    {
        Assert.Equal(
            UniversalisListingFetchPolicy.MaximumListingRequestLimit,
            UniversalisListingFetchPolicy.GetNextRequestLimit(999, 700, 700, 28));
        Assert.Null(UniversalisListingFetchPolicy.GetNextRequestLimit(999, 999, 999, 28));
    }

    [Fact]
    public void UnchangedRevisionPairDoesNotRetry()
    {
        var previous = Gap(aggregate: 2_000, detailed: 1_000);
        var current = Gap(aggregate: 2_000, detailed: 1_000);

        Assert.False(MarketFreshnessRetryPolicy.HasRevisionChange([previous], [current]));
    }

    [Fact]
    public void AdvancedDetailRevisionPermitsAnotherTargetedAttempt()
    {
        var previous = Gap(aggregate: 2_000, detailed: 1_000);
        var current = Gap(aggregate: 2_000, detailed: 1_500);

        Assert.True(MarketFreshnessRetryPolicy.HasRevisionChange([previous], [current]));
    }

    private static MarketFreshnessGap Gap(long aggregate, long detailed)
        => new(
            "Siren",
            aggregate,
            detailed,
            MarketFreshnessGapKind.AggregateAhead,
            "fixture");
}
