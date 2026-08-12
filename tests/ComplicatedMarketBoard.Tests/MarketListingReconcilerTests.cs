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

    private static MarketDataListing Listing(string id, string world, long price)
        => new()
        {
            ListingId = id,
            WorldName = world,
            PricePerUnit = price,
            Quantity = 1,
            LastReviewTime = 1,
        };
}
