using ComplicatedMarketBoard.Integrations.Universalis;

namespace ComplicatedMarketBoard.Tests;

public sealed class MarketListingReconcilerTests
{
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

    private static MarketDataListing Listing(string id, string world, long price, long quantity = 1)
        => new()
        {
            ListingId = id,
            WorldName = world,
            PricePerUnit = price,
            Quantity = quantity,
            LastReviewTime = 1,
        };
}
