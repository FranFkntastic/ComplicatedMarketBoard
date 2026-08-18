using ComplicatedMarketBoard.Integrations.Universalis;

namespace ComplicatedMarketBoard.Tests;

public sealed class MarketListingReconcilerTests
{
    [Fact]
    public void DeferredWorldRetainsOnlyPreviousVerifiedPartition()
    {
        var freshGolem = Listing("fresh-golem", "Golem", 300);
        var unverifiedSiren = Listing("unverified-siren", "Siren", 100);
        var current = Response(
            [freshGolem, unverifiedSiren],
            ("Golem", 3_000),
            ("Siren", 3_000));
        var previousSiren = Listing("previous-siren", "Siren", 200);
        var previous = Response(
            [Listing("old-golem", "Golem", 400), previousSiren],
            ("Golem", 1_000),
            ("Siren", 2_000));

        MarketListingReconciler.ApplyDeferredWorldPartitions(
            current,
            previous,
            new Dictionary<string, string> { ["Siren"] = "Still percolating." });

        Assert.Equal([previousSiren, freshGolem], current.Listings);
        Assert.DoesNotContain(unverifiedSiren, current.Listings);
        Assert.Equal(3_000, current.WorldUploadTimes["Golem"]);
        Assert.Equal(2_000, current.WorldUploadTimes["Siren"]);
        var deferred = Assert.Single(current.DeferredWorlds).Value;
        Assert.True(deferred.RetainedPreviousPartition);
        Assert.Equal(2_000, deferred.RetainedUploadTime);
    }

    [Fact]
    public void DeferredWorldWithoutPreviousEvidenceIsOmitted()
    {
        var freshGolem = Listing("fresh-golem", "Golem", 300);
        var unverifiedSiren = Listing("unverified-siren", "Siren", 100);
        var current = Response(
            [freshGolem, unverifiedSiren],
            ("Golem", 3_000),
            ("Siren", 3_000));

        MarketListingReconciler.ApplyDeferredWorldPartitions(
            current,
            previous: null,
            new Dictionary<string, string> { ["Siren"] = "Still percolating." });

        Assert.Equal([freshGolem], current.Listings);
        Assert.False(current.WorldUploadTimes.ContainsKey("Siren"));
        var deferred = Assert.Single(current.DeferredWorlds).Value;
        Assert.False(deferred.RetainedPreviousPartition);
        Assert.Equal(0, deferred.RetainedUploadTime);
    }

    [Fact]
    public void ReplaceWorldPartitionPreservesOtherWorldsAndAdvancesExactRows()
    {
        var staleSiren = Listing("old-siren", "Siren", 495_000);
        var faerie = Listing("faerie", "Faerie", 570_000);
        var scope = new UniversalisResponse
        {
            Listings = [staleSiren, faerie],
            WorldUploadTimes = new Dictionary<string, long>
            {
                ["Siren"] = 1_786_496_062_803,
                ["Faerie"] = 1_786_496_062_700,
            },
        };
        var currentSiren = Listing("new-siren", "Siren", 569_999);
        var partition = new UniversalisResponse
        {
            Listings = [currentSiren],
            WorldUploadTimes = new Dictionary<string, long>
            {
                ["Siren"] = 1_786_496_062_810,
            },
        };

        MarketListingReconciler.ReplaceWorldPartition(scope, "Siren", partition);

        Assert.Equal([currentSiren, faerie], scope.Listings);
        Assert.DoesNotContain(staleSiren, scope.Listings);
        Assert.Equal(1_786_496_062_810, scope.WorldUploadTimes["Siren"]);
        Assert.Equal(1_786_496_062_700, scope.WorldUploadTimes["Faerie"]);
    }

    [Fact]
    public void FinalizeVerifiedResponseBindsMetricsToTheirOwningFeed()
    {
        var listings = new UniversalisResponse
        {
            FetchTime = 100,
            UnitsForSale = 999,
            AveragePrice = 1,
            Velocity = 1,
            Listings =
            [
                Listing("third", "Faerie", 300, quantity: 30),
                Listing("first", "Siren", 100, quantity: 10),
                Listing("second", "Faerie", 200, quantity: 20),
            ],
        };
        var sale = new MarketDataEntry { PricePerUnit = 42 };
        var history = new UniversalisResponse
        {
            FetchTime = 200,
            AveragePrice = 42,
            AveragePriceNq = 41,
            AveragePriceHq = 43,
            Velocity = 3,
            VelocityNq = 2,
            VelocityHq = 1,
            Entries = [sale],
        };

        var result = MarketListingReconciler.FinalizeVerifiedResponse(listings, history, listingLimit: 2);

        Assert.Equal(["first", "second"], result.Listings.Select(listing => listing.ListingId));
        Assert.Equal(30, result.UnitsForSale);
        Assert.Same(sale, Assert.Single(result.Entries));
        Assert.Equal(42, result.AveragePrice);
        Assert.Equal(41, result.AveragePriceNq);
        Assert.Equal(43, result.AveragePriceHq);
        Assert.Equal(3, result.Velocity);
        Assert.Equal(2, result.VelocityNq);
        Assert.Equal(1, result.VelocityHq);
        Assert.Equal(200, result.FetchTime);
    }

    [Fact]
    public void FinalizeVerifiedResponseKeepsOnlyFreshUniqueRowsWithoutAStaleTail()
    {
        var fresh = Enumerable.Range(0, 22)
            .Select(index => Listing($"fresh-{index}", "Faerie", 100 + index, quantity: index + 1))
            .ToList();
        var listings = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            RawListingCount = 200,
            ListingRequestLimit = 200,
            ListingPageMayBeTruncated = true,
            Listings = fresh,
        };

        var result = MarketListingReconciler.FinalizeVerifiedResponse(
            listings,
            new UniversalisResponse(),
            listingLimit: 70);

        Assert.Equal(22, result.Listings.Count);
        Assert.Equal(fresh.Sum(listing => listing.Quantity), result.UnitsForSale);
        Assert.All(result.Listings, listing => Assert.StartsWith("fresh-", listing.ListingId));
    }

    private static MarketDataListing Listing(string id, string world, long price, long quantity = 1)
        => new()
        {
            ListingId = id,
            WorldName = world,
            PricePerUnit = price,
            Quantity = quantity,
            LastReviewTime = 1,
        };

    private static UniversalisResponse Response(
        IList<MarketDataListing> listings,
        params (string World, long UploadTime)[] revisions)
        => new()
        {
            Status = UniversalisResponseStatus.Success,
            Listings = listings,
            WorldUploadTimes = revisions.ToDictionary(revision => revision.World, revision => revision.UploadTime),
        };
}
