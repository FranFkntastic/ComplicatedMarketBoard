using System.Text.Json;
using ComplicatedMarketBoard.Integrations.Universalis;
using ComplicatedMarketBoard.Market;

namespace ComplicatedMarketBoard.Tests;

public sealed class MarketListingDataPolicyTests
{
    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("null", null)]
    public void WireDataFlagPreservesFalseTrueAndUnknown(string value, bool? expected)
    {
        var data = JsonSerializer.Deserialize<MarketDataCurrent>($"{{\"hasData\":{value}}}")!;
        Assert.Equal(expected, data.HasData);
        Assert.Null(JsonSerializer.Deserialize<MarketDataCurrent>("{}")!.HasData);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1_794_085_000_000)]
    [InlineData(true, 1_794_085_000_000)]
    public void EmptyContentDoesNotScheduleRepairForTimestampOnlyGap(bool hqOnly, long detailRevision)
    {
        // Captured item 21796 shape: both world minima absent; aggregate still has upload timestamps.
        var probes = new MarketFreshnessProbe[]
        {
            new("Malboro", null, null, 1_794_087_545_718),
            new("Kraken", null, null, 1_794_170_403_324),
        };
        var detailed = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            WorldUploadTimes = new() { ["Malboro"] = detailRevision, ["Kraken"] = detailRevision },
        };
        var match = MarketWorldPartitionPolicy.CompareEligibleScope(probes, detailed, hqOnly, 70,
            new Dictionary<string, string>());
        Assert.True(match.IsCurrent);
        Assert.Empty(match.Gaps); // GetCurrentDataForTarget returns here, before either repair branch.
        Assert.Empty(detailed.DeferredWorlds);
        Assert.Empty(detailed.ProvisionalWorlds);
        Assert.Equal(detailRevision == 0 ? 2 : 0, detailed.WorldsWithoutListingData.Count);
    }

    [Fact]
    public void ConfirmedEmptyAndUnavailableHaveDifferentCompletionText()
    {
        var empty = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success, HasData = true,
            WorldUploadTimes = new() { ["Malboro"] = 2_000 },
        };
        var probes = new MarketFreshnessProbe[] { new("Malboro", null, null, 3_000) };
        MarketWorldPartitionPolicy.CompareEligibleScope(probes, empty, false, 70, new Dictionary<string, string>());
        Assert.Empty(empty.WorldsWithoutListingData);
        Assert.Equal("No listings returned for Potion", MarketListingDataPolicy.CompletionSummary(empty, "Potion"));

        var noData = new UniversalisResponse { Status = UniversalisResponseStatus.Success, HasData = false };
        MarketWorldPartitionPolicy.CompareEligibleScope(probes, noData, false, 70, new Dictionary<string, string>());
        Assert.Contains("Malboro", noData.WorldsWithoutListingData);
        Assert.Equal("Results available; No listing data returned for Malboro", MarketListingDataPolicy.CompletionSummary(noData, "Potion"));
        Assert.DoesNotContain("pending", MarketListingDataPolicy.CompletionSummary(noData, "Potion")!);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void RealListingPresenceDisagreementStillRequiresVerification(bool aggregateHasMinimum, bool detailHasListing, bool expectedCurrent)
    {
        var probe = new MarketFreshnessProbe("Malboro",
            aggregateHasMinimum ? new(false, 100, 0, "Malboro", 3_000) : null, null, 3_000);
        var detailed = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            WorldUploadTimes = new() { ["Malboro"] = 3_000 },
            Listings = detailHasListing ? [new() { WorldName = "Malboro", PricePerUnit = 200 }] : [],
        };
        var result = MarketFreshnessMatcher.CompareScope([probe], detailed, false, 70);
        Assert.Equal(expectedCurrent, result.IsCurrent);
    }

    [Fact]
    public void AggregateListingMissingFromDetailsStillRequiresRepair()
    {
        var probe = new MarketFreshnessProbe("Kraken", new(false, 100, 0, "Kraken", 3_000), null, 3_000);
        var result = MarketFreshnessMatcher.CompareScope([probe], new UniversalisResponse(), false, 70);
        Assert.False(result.IsCurrent);
        Assert.NotEmpty(result.Gaps);
    }

    [Fact]
    public void NoDataIsNotVerifiedContinuityAndMissingWireFieldDoesNotRejectRows()
    {
        Assert.False(MarketListingDataPolicy.HasContradictoryDataFlag(new() { Listings = [new()] }));
        Assert.True(MarketListingDataPolicy.HasContradictoryDataFlag(new() { HasData = false, Listings = [new()] }));
        var previous = new UniversalisResponse
        {
            WorldUploadTimes = new() { ["Malboro"] = 0 },
            WorldsWithoutListingData = new(StringComparer.OrdinalIgnoreCase) { "Malboro" },
        };
        var next = new UniversalisResponse();
        MarketListingReconciler.ApplyDeferredWorldPartitions(next, previous,
            new Dictionary<string, string> { ["Malboro"] = "Network unavailable" });
        Assert.False(next.DeferredWorlds["Malboro"].RetainedPreviousPartition);
    }

    [Fact]
    public void NoDataSummaryPreservesIndependentCoverageAndConflictQualifications()
    {
        var response = new UniversalisResponse
        {
            WorldsWithoutListingData = new() { "Malboro" },
            DeferredWorlds = new() { ["Kraken"] = new("Conflict", false) },
            ListingCoverage = MarketListingCoverageStatus.DuplicateLimited,
            RequestedListingCount = 70,
        };
        var summary = MarketListingDataPolicy.CompletionSummary(response, "Potion")!;
        Assert.Contains("No listing data returned for Malboro", summary);
        Assert.Contains("1 world(s) not corroborated", summary);
        Assert.Contains("0 of 70 requested", summary);
    }

    [Fact]
    public void FailedWorldIsNotRelabeledAsNoUploadedData()
    {
        var response = new UniversalisResponse();
        MarketWorldPartitionPolicy.CompareEligibleScope([new("Malboro", null, null, 3_000)], response, false, 70,
            new Dictionary<string, string> { ["Malboro"] = "HTTP 503" });
        Assert.Empty(response.WorldsWithoutListingData);
    }
}
