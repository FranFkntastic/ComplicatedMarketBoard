namespace ComplicatedMarketBoard.Market;

public static class MarketFreshnessRetryPolicy
{
    public const int MaxTargetedRepairPasses = 4;
    public const int MinimumScopeRepairWorlds = 4;
    public const int AbsoluteScopeRepairWorldThreshold = 8;

    public static bool ShouldUseScopeRepair(int aggregateAheadWorldCount, int scopeWorldCount)
        => aggregateAheadWorldCount >= MinimumScopeRepairWorlds
           && (aggregateAheadWorldCount >= AbsoluteScopeRepairWorldThreshold
               || aggregateAheadWorldCount * 2 >= scopeWorldCount);

    public static bool HasRevisionChange(
        IEnumerable<MarketFreshnessGap> previous,
        IEnumerable<MarketFreshnessGap> current)
        => !string.Equals(
            BuildRevisionFingerprint(previous),
            BuildRevisionFingerprint(current),
            StringComparison.Ordinal);

    public static TimeSpan GetBackoff(
        int completedPasses,
        TimeSpan initialDelay,
        TimeSpan remaining)
    {
        var requested = TimeSpan.FromMilliseconds(
            initialDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, completedPasses - 1)));
        return remaining < requested ? remaining : requested;
    }

    private static string BuildRevisionFingerprint(IEnumerable<MarketFreshnessGap> gaps)
        => string.Join(
            "|",
            gaps.OrderBy(gap => gap.WorldName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(gap => gap.Kind)
                .Select(gap =>
                    $"{gap.WorldName.ToUpperInvariant()}:{gap.Kind}:{gap.AggregateUploadTime}:{gap.DetailedUploadTime}"));
}
