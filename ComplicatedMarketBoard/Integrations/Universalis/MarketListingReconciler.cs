namespace ComplicatedMarketBoard.Integrations.Universalis;

public static class MarketListingReconciler
{
    public static void ApplyDeferredWorldPartitions(
        UniversalisResponse current,
        UniversalisResponse? previous,
        IReadOnlyDictionary<string, string> deferredWorlds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(deferredWorlds);

        foreach (var deferred in deferredWorlds)
        {
            RemoveWorldPartition(current, deferred.Key);

            var retained = false;
            var retainedUploadTime = 0L;
            if (previous is not null
                && previous.WorldUploadTimes.TryGetValue(deferred.Key, out retainedUploadTime))
            {
                var previousPartition = new UniversalisResponse
                {
                    Listings = previous.Listings
                        .Where(listing => MatchesWorld(listing, deferred.Key))
                        .ToList(),
                    WorldUploadTimes = new Dictionary<string, long>
                    {
                        [deferred.Key] = retainedUploadTime,
                    },
                };
                if (previous.WorldOutOfDate.TryGetValue(deferred.Key, out var outOfDate))
                    previousPartition.WorldOutOfDate[deferred.Key] = outOfDate;

                ReplaceWorldPartition(current, deferred.Key, previousPartition);
                retained = true;
            }

            current.DeferredWorlds[deferred.Key] = new DeferredWorldPartition(
                deferred.Value,
                retained,
                retained ? retainedUploadTime : 0);
        }
    }

    public static UniversalisResponse FinalizeVerifiedResponse(
        UniversalisResponse listings,
        UniversalisResponse history,
        int listingLimit)
    {
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(history);

        listings.Listings = listings.Listings
            .OrderBy(listing => listing.PricePerUnit)
            .ThenBy(listing => listing.Quantity)
            .Take(Math.Max(0, listingLimit))
            .ToList();
        listings.UnitsForSale = listings.Listings.Sum(listing => listing.Quantity);
        listings.Entries = history.Entries;
        listings.AveragePrice = history.AveragePrice;
        listings.AveragePriceNq = history.AveragePriceNq;
        listings.AveragePriceHq = history.AveragePriceHq;
        listings.Velocity = history.Velocity;
        listings.VelocityNq = history.VelocityNq;
        listings.VelocityHq = history.VelocityHq;
        listings.FetchTime = Math.Max(listings.FetchTime, history.FetchTime);
        return listings;
    }

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

    public static void RemoveWorldPartition(
        UniversalisResponse scope,
        string worldName)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);

        var worldIds = scope.Listings
            .Where(listing => MatchesWorld(listing, worldName) && listing.WorldID > 0)
            .Select(listing => listing.WorldID)
            .Distinct()
            .ToArray();
        scope.Listings = scope.Listings
            .Where(listing => !MatchesWorld(listing, worldName))
            .ToList();
        scope.WorldUploadTimes.Remove(worldName);
        scope.WorldOutOfDate.Remove(worldName);
        scope.ConflictingListingIdentities = scope.ConflictingListingIdentities
            .Where(identity => !IdentityMatchesWorld(identity.World, worldName, worldIds))
            .ToArray();
        scope.LatestUploadTime = scope.WorldUploadTimes.Count > 0
            ? scope.WorldUploadTimes.Values.Max()
            : 0;
    }

    public static bool MatchesWorld(MarketDataListing listing, string worldName)
        => string.Equals(listing.WorldName, worldName, StringComparison.OrdinalIgnoreCase);

    private static bool IdentityMatchesWorld(
        string identityWorld,
        string worldName,
        IReadOnlyCollection<ulong> worldIds)
        => string.Equals(identityWorld, $"name:{worldName}", StringComparison.OrdinalIgnoreCase)
           || worldIds.Any(worldId => string.Equals(
               identityWorld,
               $"id:{worldId}",
               StringComparison.OrdinalIgnoreCase));
}
