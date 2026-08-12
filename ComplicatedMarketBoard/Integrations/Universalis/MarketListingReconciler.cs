namespace ComplicatedMarketBoard.Integrations.Universalis;

public static class MarketListingReconciler
{
    public static void ReplaceWorldPartition(
        UniversalisResponse scope,
        string worldName,
        UniversalisResponse partition)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);
        ArgumentNullException.ThrowIfNull(partition);

        var merged = scope.Listings
            .Where(listing => !MatchesWorld(listing, worldName))
            .Concat(partition.Listings);
        var normalization = MarketListingNormalizer.Analyze(merged);
        scope.Listings = normalization.Listings
            .OrderBy(listing => listing.PricePerUnit)
            .ThenBy(listing => listing.Quantity)
            .ToList();
        scope.ConflictingListingIdentities = scope.ConflictingListingIdentities
            .Concat(partition.ConflictingListingIdentities)
            .Concat(normalization.Conflicts)
            .Distinct()
            .ToArray();

        if (partition.WorldUploadTimes.TryGetValue(worldName, out var uploadTime))
            scope.WorldUploadTimes[worldName] = uploadTime;
        if (partition.WorldOutOfDate.TryGetValue(worldName, out var outOfDate))
            scope.WorldOutOfDate[worldName] = outOfDate;
        scope.LatestUploadTime = scope.WorldUploadTimes.Count > 0
            ? scope.WorldUploadTimes.Values.Max()
            : scope.LatestUploadTime;
    }

    public static bool MatchesWorld(MarketDataListing listing, string worldName)
        => string.Equals(listing.WorldName, worldName, StringComparison.OrdinalIgnoreCase);
}
