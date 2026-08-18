using ComplicatedMarketBoard.Integrations.Universalis;

namespace ComplicatedMarketBoard.Market;

public static class MarketListingCoveragePolicy
{
    public static bool IsDuplicateLimited(
        UniversalisResponse response,
        int requestedListingCount)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.Status == UniversalisResponseStatus.Success
               && requestedListingCount > 0
               && response.Listings.Count < requestedListingCount
               && response.RawListingCount > response.Listings.Count
               && response.ListingPageMayBeTruncated;
    }

    public static UniversalisResponse Classify(
        UniversalisResponse response,
        int requestedListingCount,
        bool forceDuplicateLimited = false)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.RequestedListingCount = Math.Max(0, requestedListingCount);
        response.ListingCoverage = response.Status == UniversalisResponseStatus.Success
                                   && (forceDuplicateLimited || IsDuplicateLimited(response, requestedListingCount))
            ? MarketListingCoverageStatus.DuplicateLimited
            : MarketListingCoverageStatus.Complete;
        return response;
    }
}
