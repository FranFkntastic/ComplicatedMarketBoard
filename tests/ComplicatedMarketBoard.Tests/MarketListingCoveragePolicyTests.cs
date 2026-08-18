using ComplicatedMarketBoard.Integrations.Universalis;
using ComplicatedMarketBoard.Market;

namespace ComplicatedMarketBoard.Tests;

public sealed class MarketListingCoveragePolicyTests
{
    [Fact]
    public void SaturatedDuplicatePageIsAcceptedAsDuplicateLimited()
    {
        var response = Response(rawCount: 200, requestLimit: 200, uniqueCount: 22);

        MarketListingCoveragePolicy.Classify(response, requestedListingCount: 70);

        Assert.Equal(UniversalisResponseStatus.Success, response.Status);
        Assert.Equal(MarketListingCoverageStatus.DuplicateLimited, response.ListingCoverage);
        Assert.Equal(70, response.RequestedListingCount);
        Assert.Equal(22, response.Listings.Count);
    }

    [Fact]
    public void CapturedDuplicateShapeCollapsesTwoHundredRowsToTwentyTwoCurrentListings()
    {
        var rawListings = Enumerable.Range(0, 200)
            .Select(index => new MarketDataListing
            {
                ListingId = $"listing-{index % 22}",
                WorldName = "Maduin",
                LastReviewTime = index % 22,
                PricePerUnit = 1_236_900 + (index % 22),
                Quantity = 1,
            })
            .ToList();
        var normalization = MarketListingNormalizer.Analyze(rawListings);
        var response = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            RawListingCount = rawListings.Count,
            ListingRequestLimit = 200,
            ListingPageMayBeTruncated = true,
            ConflictingListingIdentities = normalization.Conflicts,
            Listings = normalization.Listings.ToList(),
        };

        MarketListingCoveragePolicy.Classify(response, requestedListingCount: 50);

        Assert.Empty(response.ConflictingListingIdentities);
        Assert.Equal(22, response.Listings.Count);
        Assert.Equal(MarketListingCoverageStatus.DuplicateLimited, response.ListingCoverage);
    }

    [Fact]
    public void ShortUnsaturatedMarketIsComplete()
    {
        var response = Response(rawCount: 22, requestLimit: 70, uniqueCount: 22);

        MarketListingCoveragePolicy.Classify(response, requestedListingCount: 70);

        Assert.Equal(MarketListingCoverageStatus.Complete, response.ListingCoverage);
    }

    [Fact]
    public void FullUniquePageIsComplete()
    {
        var response = Response(rawCount: 70, requestLimit: 70, uniqueCount: 70);

        MarketListingCoveragePolicy.Classify(response, requestedListingCount: 70);

        Assert.Equal(MarketListingCoverageStatus.Complete, response.ListingCoverage);
    }

    [Fact]
    public void ForcedDuplicateLimitedCoverageSurvivesACompositeTopNMerge()
    {
        var response = Response(rawCount: 70, requestLimit: 70, uniqueCount: 70);

        MarketListingCoveragePolicy.Classify(
            response,
            requestedListingCount: 70,
            forceDuplicateLimited: true);

        Assert.Equal(MarketListingCoverageStatus.DuplicateLimited, response.ListingCoverage);
    }

    [Fact]
    public void FailedResponseNeverClaimsPartialSuccess()
    {
        var response = Response(rawCount: 200, requestLimit: 200, uniqueCount: 22);
        response.Status = UniversalisResponseStatus.InvalidData;

        MarketListingCoveragePolicy.Classify(response, requestedListingCount: 70, forceDuplicateLimited: true);

        Assert.Equal(MarketListingCoverageStatus.Complete, response.ListingCoverage);
    }

    private static UniversalisResponse Response(int rawCount, int requestLimit, int uniqueCount)
        => new()
        {
            Status = UniversalisResponseStatus.Success,
            RawListingCount = rawCount,
            ListingRequestLimit = requestLimit,
            ListingPageMayBeTruncated = rawCount >= requestLimit,
            Listings = Enumerable.Range(0, uniqueCount)
                .Select(index => new MarketDataListing { ListingId = $"listing-{index}" })
                .ToList(),
        };
}
