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
    public void UnchangedRevisionPairIsDetectedForTargetedBackoff()
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

    [Theory]
    [InlineData(1, 1_000)]
    [InlineData(2, 2_000)]
    [InlineData(3, 4_000)]
    [InlineData(4, 5_000)]
    public void TargetedRepairBackoffIsExponentialAndDeadlineBounded(
        int completedPasses,
        int expectedMilliseconds)
    {
        var delay = MarketFreshnessRetryPolicy.GetBackoff(
            completedPasses,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5));

        Assert.Equal(expectedMilliseconds, delay.TotalMilliseconds);
        Assert.Equal(4, MarketFreshnessRetryPolicy.MaxTargetedRepairPasses);
    }

    private static MarketFreshnessGap Gap(long aggregate, long detailed)
        => new(
            "Siren",
            aggregate,
            detailed,
            MarketFreshnessGapKind.AggregateAhead,
            "fixture");
}
