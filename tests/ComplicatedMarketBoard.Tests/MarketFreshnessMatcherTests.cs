using System.Text.Json;
using ComplicatedMarketBoard.Integrations.Universalis;
using ComplicatedMarketBoard.Market;

namespace ComplicatedMarketBoard.Tests;

public sealed class MarketFreshnessMatcherTests
{
    [Fact]
    public void AggregateResponse_DeserializesUniversalisWireShape()
    {
        const string json =
            """
            {
              "results": [{
                "itemId": 7,
                "nq": {
                  "minListing": {
                    "world": { "price": 42 },
                    "dc": { "price": 42, "worldId": 54 },
                    "region": { "price": 42, "worldId": 54 }
                  }
                },
                "hq": { "minListing": {} },
                "worldUploadTimes": [{ "worldId": 54, "timestamp": 1785093953296 }]
              }],
              "failedItems": []
            }
            """;

        var response = JsonSerializer.Deserialize<UniversalisAggregateResponse>(json);

        var item = Assert.Single(Assert.IsType<UniversalisAggregateResponse>(response).Results);
        Assert.Equal((ulong)7, item.ItemId);
        Assert.Equal(42, item.Nq.MinListing.World?.Price);
        Assert.Equal((uint)54, item.Nq.MinListing.DataCenter?.WorldId);
        Assert.Equal(1_785_093_953_296, Assert.Single(item.WorldUploadTimes).Timestamp);
    }

    [Fact]
    public void Compare_AcceptsMatchingMinimumAndRevision()
    {
        var probe = Probe(nqPrice: 100, nqUploadTime: 2_000);
        var detailed = Response(new MarketDataListing
        {
            PricePerUnit = 100,
            Quantity = 10,
            WorldID = 54,
            WorldName = "Faerie",
        }, uploadTime: 2_000);

        var result = MarketFreshnessMatcher.Compare(probe, detailed, hqOnly: false);

        Assert.True(result.IsCurrent);
    }

    [Fact]
    public void Compare_RejectsDetailedMinimumBehindAggregate()
    {
        var probe = Probe(nqPrice: 100, nqUploadTime: 2_000);
        var detailed = Response(new MarketDataListing
        {
            PricePerUnit = 150,
            Quantity = 10,
            WorldID = 54,
            WorldName = "Faerie",
        }, uploadTime: 1_000);

        var result = MarketFreshnessMatcher.Compare(probe, detailed, hqOnly: false);

        Assert.False(result.IsCurrent);
        Assert.Contains("100", result.Detail);
        Assert.Contains("150", result.Detail);
    }

    [Fact]
    public void Compare_RejectsMatchingPriceFromOlderRevision()
    {
        var probe = Probe(nqPrice: 100, nqUploadTime: 2_001);
        var detailed = Response(new MarketDataListing
        {
            PricePerUnit = 100,
            Quantity = 10,
            WorldID = 54,
            WorldName = "Faerie",
        }, uploadTime: 1_000);

        var result = MarketFreshnessMatcher.Compare(probe, detailed, hqOnly: false);

        Assert.False(result.IsCurrent);
        Assert.Contains("older", result.Detail);
    }

    [Fact]
    public void CompareScope_AcceptsMatchingPriceWithObservedNineMillisecondRevisionSkew()
    {
        var probe = new MarketFreshnessProbe(
            "Siren",
            new MarketMinimumProbe(false, 1_225_000, 64, "Siren", 1_785_615_927_911),
            null);
        var detailed = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            Listings =
            [
                new MarketDataListing
                {
                    PricePerUnit = 1_225_000,
                    Quantity = 1,
                    WorldID = 64,
                    WorldName = "Siren",
                },
            ],
            WorldUploadTimes = new Dictionary<string, long>
            {
                ["Siren"] = 1_785_615_927_902,
            },
        };

        var result = MarketFreshnessMatcher.CompareScope(
            [probe],
            detailed,
            hqOnly: false,
            listingLimit: 50);

        Assert.True(result.IsCurrent);
        Assert.Contains("9ms", result.Detail);
    }

    [Fact]
    public void CompareScope_StillRejectsPriceMismatchWithinRevisionTolerance()
    {
        var probe = new MarketFreshnessProbe(
            "Siren",
            new MarketMinimumProbe(false, 1_225_000, 64, "Siren", 1_785_615_927_911),
            null);
        var detailed = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            Listings =
            [
                new MarketDataListing
                {
                    PricePerUnit = 1_350_000,
                    Quantity = 1,
                    WorldID = 64,
                    WorldName = "Siren",
                },
            ],
            WorldUploadTimes = new Dictionary<string, long>
            {
                ["Siren"] = 1_785_615_927_902,
            },
        };

        var result = MarketFreshnessMatcher.CompareScope(
            [probe],
            detailed,
            hqOnly: false,
            listingLimit: 50);

        Assert.False(result.IsCurrent);
        Assert.Contains("1,225,000", result.Detail);
        Assert.Contains("1,350,000", result.Detail);
    }

    [Fact]
    public void Compare_RejectsListingWhenAggregateReportsNoMinimum()
    {
        var probe = new MarketFreshnessProbe("Faerie", null, null);
        var detailed = Response(new MarketDataListing
        {
            PricePerUnit = 100,
            Quantity = 10,
            WorldID = 54,
            WorldName = "Faerie",
        }, uploadTime: 2_000);

        var result = MarketFreshnessMatcher.Compare(probe, detailed, hqOnly: false);

        Assert.False(result.IsCurrent);
        Assert.Contains("no listings", result.Detail);
    }

    [Fact]
    public void Compare_HqOnlyIgnoresNqListings()
    {
        var probe = new MarketFreshnessProbe(
            "Faerie",
            new MarketMinimumProbe(false, 50, 54, "Faerie", 2_000),
            new MarketMinimumProbe(true, 200, 54, "Faerie", 2_000));
        var detailed = Response(
            new MarketDataListing
            {
                PricePerUnit = 999,
                Quantity = 10,
                WorldID = 54,
                WorldName = "Faerie",
            },
            new MarketDataListing
            {
                PricePerUnit = 200,
                Quantity = 10,
                WorldID = 54,
                WorldName = "Faerie",
                Hq = true,
            },
            uploadTime: 2_000);

        var result = MarketFreshnessMatcher.Compare(probe, detailed, hqOnly: true);

        Assert.True(result.IsCurrent);
    }

    [Fact]
    public void Compare_StandardQueryValidatesOverallMinimumOnly()
    {
        var probe = new MarketFreshnessProbe(
            "Faerie",
            new MarketMinimumProbe(false, 100, 54, "Faerie", 2_000),
            new MarketMinimumProbe(true, 500, 54, "Faerie", 2_000));
        var detailed = Response(new MarketDataListing
        {
            PricePerUnit = 100,
            Quantity = 10,
            WorldID = 54,
            WorldName = "Faerie",
        }, uploadTime: 2_000);

        var result = MarketFreshnessMatcher.Compare(probe, detailed, hqOnly: false);

        Assert.True(result.IsCurrent);
    }

    [Fact]
    public void Compare_StandardQueryValidatesSecondaryQualityWhenPresent()
    {
        var probe = new MarketFreshnessProbe(
            "Faerie",
            new MarketMinimumProbe(false, 100, 54, "Faerie", 2_000),
            new MarketMinimumProbe(true, 500, 54, "Faerie", 2_000));
        var detailed = Response(
            new MarketDataListing
            {
                PricePerUnit = 100,
                Quantity = 10,
                WorldID = 54,
                WorldName = "Faerie",
            },
            new MarketDataListing
            {
                PricePerUnit = 600,
                Quantity = 10,
                WorldID = 54,
                WorldName = "Faerie",
                Hq = true,
            },
            uploadTime: 2_000);

        var result = MarketFreshnessMatcher.Compare(probe, detailed, hqOnly: false);

        Assert.False(result.IsCurrent);
        Assert.Contains("500", result.Detail);
        Assert.Contains("600", result.Detail);
    }

    [Fact]
    public void CompareScope_RejectsStaleWorldThatIsNotTheScopeMinimum()
    {
        var probes = new[]
        {
            new MarketFreshnessProbe(
                "Golem",
                new MarketMinimumProbe(false, 33, 411, "Golem", 2_000),
                null),
            new MarketFreshnessProbe(
                "Balmung",
                new MarketMinimumProbe(false, 38, 91, "Balmung", 2_000),
                null),
        };
        var detailed = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            Listings =
            [
                new MarketDataListing
                {
                    PricePerUnit = 33,
                    Quantity = 1_000,
                    WorldID = 411,
                    WorldName = "Golem",
                },
                new MarketDataListing
                {
                    PricePerUnit = 34,
                    Quantity = 1_000,
                    WorldID = 91,
                    WorldName = "Balmung",
                },
            ],
            WorldUploadTimes = new Dictionary<string, long>
            {
                ["Golem"] = 2_000,
                ["Balmung"] = 2_000,
            },
        };

        var result = MarketFreshnessMatcher.CompareScope(
            probes,
            detailed,
            hqOnly: false,
            listingLimit: 70);

        Assert.False(result.IsCurrent);
        Assert.Contains("Balmung", result.Detail);
        Assert.Contains("38", result.Detail);
        Assert.Contains("34", result.Detail);
    }

    [Fact]
    public void CompareScope_AllowsWorldMinimumBeyondTruncatedCutoff()
    {
        var probes = new[]
        {
            new MarketFreshnessProbe(
                "Golem",
                new MarketMinimumProbe(false, 33, 411, "Golem", 2_000),
                null),
            new MarketFreshnessProbe(
                "Balmung",
                new MarketMinimumProbe(false, 100, 91, "Balmung", 2_000),
                null),
        };
        var detailed = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            RawListingCount = 1,
            RawListingCutoffPrice = 33,
            Listings =
            [
                new MarketDataListing
                {
                    PricePerUnit = 33,
                    Quantity = 1_000,
                    WorldID = 411,
                    WorldName = "Golem",
                },
            ],
            WorldUploadTimes = new Dictionary<string, long>
            {
                ["Golem"] = 2_000,
                ["Balmung"] = 2_000,
            },
        };

        var result = MarketFreshnessMatcher.CompareScope(
            probes,
            detailed,
            hqOnly: false,
            listingLimit: 1);

        Assert.True(result.IsCurrent);
    }

    [Theory]
    [InlineData(949_993, true)]
    [InlineData(500_000, false)]
    public void CompareScope_UsesRawPageCutoffAfterListingIdentityDeduplication(
        long adamantoiseMinimum,
        bool expectedCurrent)
    {
        var rawListings = Enumerable.Range(0, 50)
            .Select(index => new MarketDataListing
            {
                ListingId = $"listing-{index % 7}",
                LastReviewTime = index,
                PricePerUnit = index == 49 ? 600_001 : 33,
                Quantity = 1_000,
                WorldID = 411,
                WorldName = "Golem",
            })
            .ToList();
        var probes = new[]
        {
            new MarketFreshnessProbe(
                "Golem",
                new MarketMinimumProbe(false, 33, 411, "Golem", 2_000),
                null),
            new MarketFreshnessProbe(
                "Adamantoise",
                new MarketMinimumProbe(false, adamantoiseMinimum, 73, "Adamantoise", 2_000),
                null),
        };
        var detailed = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            RawListingCount = rawListings.Count,
            RawListingCutoffPrice = rawListings.Max(listing => listing.PricePerUnit),
            Listings = MarketListingNormalizer.Normalize(rawListings).ToList(),
            WorldUploadTimes = new Dictionary<string, long>
            {
                ["Golem"] = 2_000,
                ["Adamantoise"] = 2_000,
            },
        };

        var result = MarketFreshnessMatcher.CompareScope(
            probes,
            detailed,
            hqOnly: false,
            listingLimit: 50);

        Assert.Equal(7, detailed.Listings.Count);
        Assert.Equal(expectedCurrent, result.IsCurrent);
    }

    private static MarketFreshnessProbe Probe(long nqPrice, long nqUploadTime)
        => new(
            "Faerie",
            new MarketMinimumProbe(false, nqPrice, 54, "Faerie", nqUploadTime),
            null);

    private static UniversalisResponse Response(MarketDataListing listing, long uploadTime)
        => Response([listing], uploadTime);

    private static UniversalisResponse Response(
        MarketDataListing first,
        MarketDataListing second,
        long uploadTime)
        => Response([first, second], uploadTime);

    private static UniversalisResponse Response(
        IList<MarketDataListing> listings,
        long uploadTime)
        => new()
        {
            Status = UniversalisResponseStatus.Success,
            Listings = listings,
            WorldUploadTimes = new Dictionary<string, long>
            {
                ["Faerie"] = uploadTime,
            },
        };
}
