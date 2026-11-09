using ComplicatedMarketBoard.Integrations.Universalis;

namespace ComplicatedMarketBoard.Market;

public static class MarketProvisionalListingPolicy
{
    public static readonly TimeSpan RecentUploadWindow = TimeSpan.FromMinutes(5);

    public static Dictionary<string, MarketFreshnessProbe> ReconciliationFences(UniversalisResponse previous, string requestedScope)
    {
        var cache = new Dictionary<string, MarketFreshnessProbe>(StringComparer.OrdinalIgnoreCase);
        if (MarketWorldPartitionPolicy.SelectPreviousVerifiedResponse(previous, requestedScope) is null)
            return cache;
        foreach (var probe in previous.VerificationProbes)
            if (!previous.ProvisionalWorlds.ContainsKey(probe.TargetName)
                && !previous.DeferredWorlds.ContainsKey(probe.TargetName))
                cache[probe.TargetName] = probe;
        return cache;
    }

    public static void Classify(
        UniversalisResponse detailed,
        IReadOnlyCollection<MarketFreshnessGap> gaps,
        long nowUtcMs)
    {
        detailed.ProvisionalWorlds.Clear();
        if (detailed.ConflictingListingIdentities.Count > 0)
            return;
        foreach (var world in gaps.GroupBy(gap => gap.WorldName, StringComparer.OrdinalIgnoreCase))
        {
            if (!world.All(gap => gap.Kind == MarketFreshnessGapKind.AggregateAhead
                                  && gap.DetailedUploadTime > 0
                                  && gap.AggregateUploadTime > gap.DetailedUploadTime
                                  && nowUtcMs >= gap.AggregateUploadTime
                                  && nowUtcMs - gap.AggregateUploadTime <= RecentUploadWindow.TotalMilliseconds))
                continue;

            var latest = world.MaxBy(gap => gap.AggregateUploadTime)!;
            detailed.ProvisionalWorlds[world.Key] = new(latest.AggregateUploadTime, latest.DetailedUploadTime);
        }
    }

    public static string Summary(UniversalisResponse response)
        => $"Results available; {response.ProvisionalWorlds.Count} world(s) awaiting corroboration" +
           (response.DeferredWorlds.Count > 0 ? $"; {response.DeferredWorlds.Count} world(s) deferred" : "") +
           (response.ListingCoverage == MarketListingCoverageStatus.DuplicateLimited
               ? $"; showing {response.Listings.Count} of {response.RequestedListingCount} requested listings"
               : "");
}
