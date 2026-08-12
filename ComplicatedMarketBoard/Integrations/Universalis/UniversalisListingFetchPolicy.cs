namespace ComplicatedMarketBoard.Integrations.Universalis;

public static class UniversalisListingFetchPolicy
{
    public const int MaximumListingRequestLimit = 999;

    public static int? GetNextRequestLimit(
        int desiredUniqueListings,
        int currentRequestLimit,
        int rawListingCount,
        int uniqueListingCount,
        int? previousUniqueListingCount = null)
    {
        if (desiredUniqueListings <= 0
            || uniqueListingCount >= desiredUniqueListings
            || rawListingCount < currentRequestLimit
            || (previousUniqueListingCount.HasValue
                && uniqueListingCount <= previousUniqueListingCount.Value)
            || currentRequestLimit >= MaximumListingRequestLimit)
        {
            return null;
        }

        return Math.Min(
            MaximumListingRequestLimit,
            Math.Max(currentRequestLimit + 1, currentRequestLimit * 2));
    }
}
