using ComplicatedMarketBoard.Integrations.Universalis;
using ComplicatedMarketBoard.Market;

namespace ComplicatedMarketBoard.Tests;

public sealed class MarketProvisionalListingPolicyTests
{
    private const long Now = 1_800_000_000_000;

    [Fact]
    public void FailedSingleWorldRecheckExpiresDisplayedQualificationWithoutPromotingOrDiscardingRows()
    {
        var row = new MarketDataListing { ListingId = "last-observation", WorldName = "Siren", PricePerUnit = 100 };
        var displayed = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success, ScopeName = "Siren", FetchTime = Now,
            Listings = [row], WorldUploadTimes = new() { ["Siren"] = Now - 2_000 },
            ProvisionalWorlds = new() { ["Siren"] = new(Now, Now - 2_000) },
        };
        // StaleData/transport failure supplies no replacement: the displayed response must age independently.
        Assert.False(MarketProvisionalListingPolicy.ExpireDisplayedQualifications(displayed, Now + 300_000));
        Assert.True(MarketProvisionalListingPolicy.ExpireDisplayedQualifications(displayed, Now + 300_001));
        Assert.True(displayed.ProvisionalWorlds["Siren"].EligibilityExpired);
        Assert.Contains("upload window elapsed", MarketProvisionalListingPolicy.Summary(displayed));
        Assert.Contains("Uncorroborated", displayed.ProvisionalWorlds["Siren"].Explanation);
        Assert.Same(row, Assert.Single(displayed.Listings));
        Assert.Equal(Now, displayed.FetchTime);
        Assert.Empty(MarketProvisionalListingPolicy.ReconciliationFences(displayed, "Siren"));
        Assert.False(MarketProvisionalListingPolicy.ExpireDisplayedQualifications(displayed, Now + 600_000));
        Assert.Equal(TimeSpan.FromSeconds(30), MarketProvisionalListingPolicy.NextRecheckDelay(displayed, Now + 600_000));
    }

    [Fact]
    public void RecheckWakesAtQualificationDeadlineRatherThanLeavingThirtySecondGracePeriod()
    {
        var displayed = new UniversalisResponse { ProvisionalWorlds = new() { ["Siren"] = new(Now, Now - 2_000) } };
        Assert.Equal(TimeSpan.FromMilliseconds(11), MarketProvisionalListingPolicy.NextRecheckDelay(displayed, Now + 299_990));
    }

    [Fact]
    public void BackgroundRechecksOnlyUnresolvedWorldsAndNeverReusesOtherScopeFences()
    {
        var response = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success, FetchTime = Now, ScopeName = "North-America",
            VerificationProbes = [new("Siren", null, null), new("Golem", null, null), new("Faerie", null, null)],
            ProvisionalWorlds = new() { ["Siren"] = new(Now, Now - 2_000) },
            DeferredWorlds = new() { ["Faerie"] = new("Unavailable", false) },
        };
        Assert.Equal("Golem", Assert.Single(MarketProvisionalListingPolicy.ReconciliationFences(response, "North-America")).Key);
        Assert.Empty(MarketProvisionalListingPolicy.ReconciliationFences(response, "Aether"));
    }

    [Fact]
    public void SelectionFollowsIdentityAcrossReorderingAndClearsOnRemoval()
    {
        var previous = new MarketDataListing { ListingId = "one", WorldName = "Siren", PricePerUnit = 100 };
        var replacement = new MarketDataListing { ListingId = "one", WorldName = "Siren", PricePerUnit = 200 };
        var other = new MarketDataListing { ListingId = "two", WorldName = "Siren", PricePerUnit = 50 };
        Assert.Equal(1, MarketListingSelectionPolicy.FindIndex([other, replacement], previous));
        Assert.Equal(-1, MarketListingSelectionPolicy.FindIndex([other], previous));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(299_999, true)]
    [InlineData(300_000, true)]
    [InlineData(300_001, false)]
    [InlineData(-1, false)]
    public void OnlyRecentKnownAggregateAheadRevisionsQualify(long age, bool expected)
    {
        var response = new UniversalisResponse();
        var aggregate = Now - age;
        MarketProvisionalListingPolicy.Classify(response,
            [new("Siren", aggregate, aggregate - 2_000, MarketFreshnessGapKind.AggregateAhead, "lag")], Now);
        Assert.Equal(expected, response.ProvisionalWorlds.ContainsKey("Siren"));
        Assert.Empty(response.Listings); // No row is invented from an aggregate price.
    }

    [Theory]
    [InlineData(0, MarketFreshnessGapKind.AggregateAhead)]
    [InlineData(Now - 10, MarketFreshnessGapKind.Conflict)]
    [InlineData(Now, MarketFreshnessGapKind.AggregateAhead)]
    public void UnknownDetailConflictAndNonIncreasingFenceNeverQualify(long detail, MarketFreshnessGapKind kind)
    {
        var response = new UniversalisResponse();
        MarketProvisionalListingPolicy.Classify(response, [new("Siren", Now, detail, kind, "gap")], Now);
        Assert.Empty(response.ProvisionalWorlds);
    }

    [Fact]
    public void OneConflictingQualityDisqualifiesTheWholeWorld()
    {
        var response = new UniversalisResponse();
        MarketProvisionalListingPolicy.Classify(response,
            [new("Siren", Now, Now - 2_000, MarketFreshnessGapKind.AggregateAhead, "lag"),
             new("siren", Now, Now, MarketFreshnessGapKind.Conflict, "conflict")], Now);
        Assert.Empty(response.ProvisionalWorlds);
    }

    [Fact]
    public void RecentGapPublishesExactRowsThenClearsQualificationOnConvergence()
    {
        var row = new MarketDataListing { WorldName = "Siren", PricePerUnit = 100, Quantity = 2 };
        var response = new UniversalisResponse
        {
            Listings = [row], WorldUploadTimes = new() { ["Siren"] = Now - 2_000 },
        };
        var probe = new MarketFreshnessProbe("Siren", new(false, 200, 0, "Siren", Now), null, Now);
        var accepted = MarketWorldPartitionPolicy.CompareEligibleScope([probe], response, false, 70,
            new Dictionary<string, string>(), Now);
        Assert.True(accepted.IsCurrent);
        Assert.Same(row, Assert.Single(response.Listings));
        Assert.Single(response.ProvisionalWorlds);
        Assert.Contains("awaiting corroboration", MarketProvisionalListingPolicy.Summary(response));

        row.PricePerUnit = 200;
        response.WorldUploadTimes["Siren"] = Now;
        Assert.True(MarketWorldPartitionPolicy.CompareEligibleScope([probe], response, false, 70,
            new Dictionary<string, string>(), Now).IsCurrent);
        Assert.Empty(response.ProvisionalWorlds);
    }

    [Fact]
    public void ExpiredGapReturnsToStrictComparison()
    {
        var response = new UniversalisResponse
        {
            Listings = [new() { WorldName = "Siren", PricePerUnit = 100 }],
            WorldUploadTimes = new() { ["Siren"] = Now - 2_000 },
        };
        var probe = new MarketFreshnessProbe("Siren", new(false, 200, 0, "Siren", Now), null, Now);
        Assert.False(MarketWorldPartitionPolicy.CompareEligibleScope([probe], response, false, 70,
            new Dictionary<string, string>(), Now + 300_001).IsCurrent);
        Assert.Empty(response.ProvisionalWorlds);
    }

    [Fact]
    public void PreviousProvisionalPartitionCannotBecomeVerifiedContinuity()
    {
        var previous = new UniversalisResponse
        {
            Listings = [new() { WorldName = "Siren", PricePerUnit = 100 }],
            WorldUploadTimes = new() { ["Siren"] = Now - 2_000 },
            ProvisionalWorlds = new() { ["Siren"] = new(Now, Now - 2_000) },
        };
        var current = new UniversalisResponse();
        MarketListingReconciler.ApplyDeferredWorldPartitions(current, previous,
            new Dictionary<string, string> { ["Siren"] = "Conflict" });
        Assert.Empty(current.Listings);
        Assert.False(current.DeferredWorlds["Siren"].RetainedPreviousPartition);
    }
}
