using ComplicatedMarketBoard.Integrations.Universalis;

namespace ComplicatedMarketBoard.Market;

public static class MarketWorldPartitionPolicy
{
    public static UniversalisResponse? SelectPreviousVerifiedResponse(
        UniversalisResponse previous,
        string requestedScope)
        => previous.Status == UniversalisResponseStatus.Success
           && previous.FetchTime > 0
           && string.Equals(previous.ScopeName, requestedScope, StringComparison.OrdinalIgnoreCase)
            ? previous
            : null;

    public static MarketFreshnessMatch CompareEligibleScope(
        IReadOnlyCollection<MarketFreshnessProbe> worldProbes,
        UniversalisResponse detailed,
        bool hqOnly,
        int listingLimit,
        IReadOnlyDictionary<string, string> deferredWorlds,
        long? nowUtcMs = null)
    {
        var fullMatch = MarketFreshnessMatcher.CompareScope(
            worldProbes.Where(probe => !deferredWorlds.ContainsKey(probe.TargetName)).ToArray(),
            detailed, hqOnly, listingLimit);
        MarketProvisionalListingPolicy.Classify(
            detailed, fullMatch.Gaps, nowUtcMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var eligibleProbes = worldProbes
            .Where(probe => !deferredWorlds.ContainsKey(probe.TargetName)
                            && !detailed.ProvisionalWorlds.ContainsKey(probe.TargetName))
            .ToArray();
        return eligibleProbes.Length == 0
            ? MarketFreshnessMatch.Current()
            : MarketFreshnessMatcher.CompareScope(
                eligibleProbes,
                detailed,
                hqOnly,
                listingLimit);
    }

    public static void DeferGaps(
        IEnumerable<MarketFreshnessGap> gaps,
        IDictionary<string, string> deferredWorlds)
    {
        foreach (var gap in gaps)
            deferredWorlds[gap.WorldName] = gap.Detail;
    }

    public static int CountVerifiedWorlds(
        IReadOnlyCollection<MarketFreshnessProbe> worldProbes,
        IReadOnlyDictionary<string, string> deferredWorlds)
        => worldProbes.Count(probe => !deferredWorlds.ContainsKey(probe.TargetName));
}
