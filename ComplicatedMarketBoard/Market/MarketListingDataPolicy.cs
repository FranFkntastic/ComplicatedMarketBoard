using ComplicatedMarketBoard.Integrations.Universalis;

namespace ComplicatedMarketBoard.Market;

public static class MarketListingDataPolicy
{
    public static void ClassifyWorlds(
        IReadOnlyCollection<MarketFreshnessProbe> probes,
        UniversalisResponse detailed,
        bool hqOnly)
    {
        detailed.WorldsWithoutListingData.Clear();
        foreach (var probe in probes)
        {
            if (probe.Hq is not null || probe.Nq is not null
                || detailed.Listings.Any(listing => MarketListingReconciler.MatchesWorld(listing, probe.TargetName)
                                                   && (!hqOnly || listing.Hq)))
                continue;
            if (detailed.HasData == false
                || !detailed.WorldUploadTimes.TryGetValue(probe.TargetName, out var revision)
                || revision <= 0)
                detailed.WorldsWithoutListingData.Add(probe.TargetName);
        }
    }

    public static bool HasContradictoryDataFlag(MarketDataCurrent data)
        => data.HasData == false && data.Listings.Count > 0;

    public static string DescribeUnavailable(UniversalisResponse response)
    {
        var worlds = response.WorldsWithoutListingData;
        var label = worlds.Count <= 3 ? string.Join(", ", worlds.Order(StringComparer.OrdinalIgnoreCase)) : $"{worlds.Count} worlds";
        return $"No listing data returned for {label}";
    }

    public static string? CompletionSummary(UniversalisResponse response, string itemName)
    {
        var summary = response.WorldsWithoutListingData.Count > 0
            ? $"Results available; {DescribeUnavailable(response)}"
            : response.Listings.Count == 0 ? $"No listings returned for {itemName}" : null;
        if (summary is null)
            return null;
        if (response.DeferredWorlds.Count > 0)
            summary += $"; {response.DeferredWorlds.Count} world(s) not corroborated";
        if (response.ListingCoverage == MarketListingCoverageStatus.DuplicateLimited)
            summary += $"; showing {response.Listings.Count} of {response.RequestedListingCount} requested listings";
        return summary;
    }
}
