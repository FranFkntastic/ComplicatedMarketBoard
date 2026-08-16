using ComplicatedMarketBoard.Integrations.Universalis;
using ComplicatedMarketBoard.Market;

namespace ComplicatedMarketBoard.Tests;

public sealed class UniversalisReconciliationPolicyTests
{
    [Theory]
    [InlineData("Aether", true)]
    [InlineData("Crystal", false)]
    public void PreviousPartitionReuseRequiresExactScope(string requestedScope, bool expectedReuse)
    {
        var previous = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            FetchTime = 1_000,
            ScopeName = "Aether",
        };

        var selected = MarketWorldPartitionPolicy.SelectPreviousVerifiedResponse(
            previous,
            requestedScope);

        Assert.Equal(expectedReuse, selected is not null);
    }

    [Fact]
    public void OneStaleWorldDoesNotPoisonVerifiedWorlds()
    {
        var probes = new[]
        {
            Probe("Golem", 300, 3_000),
            Probe("Siren", 100, 3_000),
        };
        var detailed = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            Listings =
            [
                Listing("Golem", 300),
                Listing("Siren", 200),
            ],
            WorldUploadTimes = new Dictionary<string, long>
            {
                ["Golem"] = 3_000,
                ["Siren"] = 2_000,
            },
        };
        var deferred = new Dictionary<string, string>();

        var initial = MarketWorldPartitionPolicy.CompareEligibleScope(
            probes,
            detailed,
            hqOnly: false,
            listingLimit: 70,
            deferred);
        MarketWorldPartitionPolicy.DeferGaps(initial.Gaps, deferred);
        var healthy = MarketWorldPartitionPolicy.CompareEligibleScope(
            probes,
            detailed,
            hqOnly: false,
            listingLimit: 70,
            deferred);

        Assert.False(initial.IsCurrent);
        Assert.Equal("Siren", Assert.Single(deferred).Key);
        Assert.True(healthy.IsCurrent);
        Assert.Equal(1, MarketWorldPartitionPolicy.CountVerifiedWorlds(probes, deferred));
    }

    [Fact]
    public void OrdinaryThirtyTwoWorldIsolationPathRemainsCpuCheap()
    {
        var worldNames = Enumerable.Range(1, 32).Select(index => $"World {index}").ToArray();
        var probes = worldNames.Select(worldName => Probe(worldName, 100, 3_000)).ToArray();
        var detailed = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            Listings = worldNames.Select(worldName => Listing(worldName, 100)).ToList(),
            WorldUploadTimes = worldNames.ToDictionary(worldName => worldName, _ => 3_000L),
        };
        var deferred = new Dictionary<string, string>();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            Assert.True(MarketWorldPartitionPolicy.CompareEligibleScope(
                probes,
                detailed,
                hqOnly: false,
                listingLimit: 70,
                deferred).IsCurrent);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Isolation path took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void AllDeferredWorldsProvideNoCurrentRefreshEvidenceEvenWithPriorAvailable()
    {
        var probes = new[]
        {
            Probe("Golem", 300, 3_000),
            Probe("Siren", 100, 3_000),
        };
        var deferred = new Dictionary<string, string>
        {
            ["Golem"] = "Unavailable.",
            ["Siren"] = "Unavailable.",
        };
        var previous = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            FetchTime = 1_000,
            ScopeName = "Aether",
        };

        Assert.NotNull(MarketWorldPartitionPolicy.SelectPreviousVerifiedResponse(previous, "Aether"));
        Assert.Equal(0, MarketWorldPartitionPolicy.CountVerifiedWorlds(probes, deferred));
    }

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
    public void DuplicateLimitedPageStopsWhenLargerRequestAddsNoUniqueListings()
    {
        Assert.Null(UniversalisListingFetchPolicy.GetNextRequestLimit(70, 140, 140, 28, 28));
        Assert.Null(UniversalisListingFetchPolicy.GetNextRequestLimit(70, 140, 140, 20, 28));
        Assert.Equal(280, UniversalisListingFetchPolicy.GetNextRequestLimit(70, 140, 140, 29, 28));
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
    [InlineData(1, 32, false)]
    [InlineData(4, 32, false)]
    [InlineData(4, 8, true)]
    [InlineData(8, 32, true)]
    [InlineData(18, 32, true)]
    public void WidespreadDisagreementUsesOneScopeRepair(
        int aggregateAheadWorlds,
        int scopeWorlds,
        bool expected)
    {
        Assert.Equal(
            expected,
            MarketFreshnessRetryPolicy.ShouldUseScopeRepair(aggregateAheadWorlds, scopeWorlds));
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

    private static MarketFreshnessProbe Probe(string worldName, long price, long uploadTime)
        => new(
            worldName,
            new MarketMinimumProbe(false, price, 0, worldName, uploadTime),
            null,
            uploadTime);

    private static MarketDataListing Listing(string worldName, long price)
        => new()
        {
            WorldName = worldName,
            PricePerUnit = price,
            Quantity = 1,
        };
}
