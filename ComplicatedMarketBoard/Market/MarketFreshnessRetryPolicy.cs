namespace ComplicatedMarketBoard.Market;

public static class MarketFreshnessRetryPolicy
{
    public static bool HasRevisionChange(
        IEnumerable<MarketFreshnessGap> previous,
        IEnumerable<MarketFreshnessGap> current)
        => !string.Equals(
            BuildRevisionFingerprint(previous),
            BuildRevisionFingerprint(current),
            StringComparison.Ordinal);

    private static string BuildRevisionFingerprint(IEnumerable<MarketFreshnessGap> gaps)
        => string.Join(
            "|",
            gaps.OrderBy(gap => gap.WorldName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(gap => gap.Kind)
                .Select(gap =>
                    $"{gap.WorldName.ToUpperInvariant()}:{gap.Kind}:{gap.AggregateUploadTime}:{gap.DetailedUploadTime}"));
}
