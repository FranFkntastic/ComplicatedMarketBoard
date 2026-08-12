using ComplicatedMarketBoard.Integrations.Universalis;

namespace ComplicatedMarketBoard.Market;

public sealed record MarketMinimumProbe(
    bool Hq,
    long PricePerUnit,
    uint WorldId,
    string WorldName,
    long UploadTime);

public sealed record MarketFreshnessProbe(
    string TargetName,
    MarketMinimumProbe? Nq,
    MarketMinimumProbe? Hq,
    long UploadTime = 0);

public enum MarketFreshnessGapKind
{
    AggregateAhead,
    Conflict,
}

public sealed record MarketFreshnessGap(
    string WorldName,
    long AggregateUploadTime,
    long DetailedUploadTime,
    MarketFreshnessGapKind Kind,
    string Detail);

public sealed record MarketFreshnessMatch(
    bool IsCurrent,
    string Detail,
    IReadOnlyList<MarketFreshnessGap> Gaps)
{
    public static MarketFreshnessMatch Current(string detail = "")
        => new(true, detail, []);

    public static MarketFreshnessMatch Stale(
        string detail,
        params MarketFreshnessGap[] gaps)
        => new(false, detail, gaps);
}

public static class MarketFreshnessMatcher
{
    public const long UploadRevisionToleranceMilliseconds = 1_000;

    public static MarketFreshnessMatch CompareScope(
        IReadOnlyCollection<MarketFreshnessProbe> worldProbes,
        UniversalisResponse detailed,
        bool hqOnly,
        int listingLimit)
    {
        var acceptedDetails = new List<string>();
        var gaps = new List<MarketFreshnessGap>();
        var isTruncated = detailed.ListingRequestLimit > 0
            ? detailed.ListingPageMayBeTruncated
            : listingLimit > 0 && detailed.RawListingCount >= listingLimit;
        var cutoffPrice = isTruncated
            ? detailed.RawListingCutoffPrice
            : null;

        foreach (var probe in worldProbes)
        {
            var nqMatch = hqOnly
                ? MarketFreshnessMatch.Current()
                : CompareWorldQuality(probe, probe.Nq, detailed, false, cutoffPrice);
            CollectMatch(nqMatch, acceptedDetails, gaps);

            var hqMatch = CompareWorldQuality(probe, probe.Hq, detailed, true, cutoffPrice);
            CollectMatch(hqMatch, acceptedDetails, gaps);
        }

        if (gaps.Count > 0)
        {
            var uniqueGaps = gaps
                .GroupBy(gap => (gap.WorldName, gap.Kind), StringTupleComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(gap => gap.AggregateUploadTime).First())
                .ToArray();
            return new MarketFreshnessMatch(
                false,
                string.Join(" ", uniqueGaps.Select(gap => gap.Detail)),
                uniqueGaps);
        }

        return MarketFreshnessMatch.Current(string.Join(" ", acceptedDetails));
    }

    public static MarketFreshnessMatch Compare(
        MarketFreshnessProbe probe,
        UniversalisResponse detailed,
        bool hqOnly)
    {
        if (hqOnly)
            return CompareQuality(probe.TargetName, probe.Hq, detailed, true);

        var expectedMinimum = new[] { probe.Nq, probe.Hq }
            .Where(minimum => minimum is not null)
            .MinBy(minimum => minimum!.PricePerUnit);
        if (expectedMinimum is not null)
        {
            var primaryMatch = CompareQuality(
                probe.TargetName,
                expectedMinimum,
                detailed,
                expectedMinimum.Hq);
            if (!primaryMatch.IsCurrent)
                return primaryMatch;

            var secondaryHq = !expectedMinimum.Hq;
            if (detailed.Listings.Any(listing => listing.Hq == secondaryHq))
            {
                var secondaryMatch = CompareQuality(
                    probe.TargetName,
                    secondaryHq ? probe.Hq : probe.Nq,
                    detailed,
                    secondaryHq);
                if (!secondaryMatch.IsCurrent)
                    return secondaryMatch;

                return MarketFreshnessMatch.Current(
                    string.Join(
                        " ",
                        new[] { primaryMatch.Detail, secondaryMatch.Detail }
                            .Where(detail => !string.IsNullOrWhiteSpace(detail))));
            }

            return primaryMatch;
        }

        return detailed.Listings.Count == 0
            ? MarketFreshnessMatch.Current()
            : MarketFreshnessMatch.Stale(
                $"Universalis now reports no listings for {probe.TargetName}, but detailed listings still contain market data.");
    }

    private static MarketFreshnessMatch CompareQuality(
        string targetName,
        MarketMinimumProbe? expected,
        UniversalisResponse detailed,
        bool hq)
    {
        var qualityLabel = hq ? "HQ" : "NQ";
        var actual = detailed.Listings
            .Where(listing => listing.Hq == hq)
            .OrderBy(listing => listing.PricePerUnit)
            .ThenBy(listing => listing.Quantity)
            .FirstOrDefault();

        if (expected is null)
        {
            return actual is null
                ? MarketFreshnessMatch.Current()
                : MarketFreshnessMatch.Stale(
                    $"Universalis now reports no {qualityLabel} minimum for {targetName}, but detailed listings still contain {actual.PricePerUnit:N0} gil.");
        }

        if (actual is null)
        {
            return MarketFreshnessMatch.Stale(
                $"Universalis reports a current {qualityLabel} minimum of {expected.PricePerUnit:N0} gil on {expected.WorldName}, but detailed listings do not contain it.");
        }

        var actualWorldMatches = actual.WorldID > 0
            ? actual.WorldID == expected.WorldId
            : string.Equals(
                string.IsNullOrWhiteSpace(actual.WorldName) ? targetName : actual.WorldName,
                expected.WorldName,
                StringComparison.OrdinalIgnoreCase);
        if (actual.PricePerUnit != expected.PricePerUnit || !actualWorldMatches)
        {
            var actualWorld = string.IsNullOrWhiteSpace(actual.WorldName) ? targetName : actual.WorldName;
            return MarketFreshnessMatch.Stale(
                $"Universalis reports a current {qualityLabel} minimum of {expected.PricePerUnit:N0} gil on {expected.WorldName}; detailed listings still begin at {actual.PricePerUnit:N0} gil on {actualWorld}.");
        }

        if (expected.UploadTime <= 0)
            return MarketFreshnessMatch.Current();

        var detailedUploadTime = detailed.WorldUploadTimes.TryGetValue(expected.WorldName, out var uploadTime)
            ? uploadTime
            : 0;
        return CompareUploadRevision(
            expected.UploadTime,
            detailedUploadTime,
            expected.WorldName,
            qualityLabel);
    }

    private static MarketFreshnessMatch CompareWorldQuality(
        MarketFreshnessProbe probe,
        MarketMinimumProbe? expected,
        UniversalisResponse detailed,
        bool hq,
        long? cutoffPrice)
    {
        var qualityLabel = hq ? "HQ" : "NQ";
        var aggregateUploadTime = expected?.UploadTime ?? probe.UploadTime;
        var detailedUploadTime = detailed.WorldUploadTimes.TryGetValue(probe.TargetName, out var uploadTime)
            ? uploadTime
            : 0;
        var actual = detailed.Listings
            .Where(listing => listing.Hq == hq && ListingMatchesWorld(listing, probe))
            .OrderBy(listing => listing.PricePerUnit)
            .ThenBy(listing => listing.Quantity)
            .FirstOrDefault();

        if (expected is null)
        {
            if (actual is null)
                return CompareUploadRevision(
                    aggregateUploadTime,
                    detailedUploadTime,
                    probe.TargetName,
                    qualityLabel);

            return ResolveContentDisagreement(
                probe.TargetName,
                qualityLabel,
                aggregateUploadTime,
                detailedUploadTime,
                $"Universalis now reports no {qualityLabel} listings on {probe.TargetName}, but detailed listings still contain {actual.PricePerUnit:N0} gil.");
        }

        if (actual is null)
        {
            if (cutoffPrice is not null && expected.PricePerUnit >= cutoffPrice.Value)
                return MarketFreshnessMatch.Current();

            return ResolveContentDisagreement(
                probe.TargetName,
                qualityLabel,
                aggregateUploadTime,
                detailedUploadTime,
                $"Universalis reports a current {qualityLabel} minimum of {expected.PricePerUnit:N0} gil on {probe.TargetName}, but the detailed scope is missing it.");
        }

        if (actual.PricePerUnit != expected.PricePerUnit)
        {
            return ResolveContentDisagreement(
                probe.TargetName,
                qualityLabel,
                aggregateUploadTime,
                detailedUploadTime,
                $"Universalis reports a current {qualityLabel} minimum of {expected.PricePerUnit:N0} gil on {probe.TargetName}; detailed listings still show {actual.PricePerUnit:N0} gil.");
        }

        return CompareUploadRevision(
            aggregateUploadTime,
            detailedUploadTime,
            probe.TargetName,
            qualityLabel);
    }

    private static MarketFreshnessMatch CompareUploadRevision(
        long expectedUploadTime,
        long detailedUploadTime,
        string worldName,
        string qualityLabel)
    {
        if (expectedUploadTime <= 0)
            return MarketFreshnessMatch.Current();

        var lagMilliseconds = expectedUploadTime - detailedUploadTime;
        if (detailedUploadTime <= 0 || lagMilliseconds > UploadRevisionToleranceMilliseconds)
        {
            var kind = detailedUploadTime > expectedUploadTime
                ? MarketFreshnessGapKind.Conflict
                : MarketFreshnessGapKind.AggregateAhead;
            var detail = detailedUploadTime <= 0
                ? $"Detailed listings for {worldName} did not include a listing revision."
                : $"Detailed listings for {worldName} are older than Universalis's current {qualityLabel} minimum.";
            return MarketFreshnessMatch.Stale(
                detail,
                new MarketFreshnessGap(
                    worldName,
                    expectedUploadTime,
                    detailedUploadTime,
                    kind,
                    detail));
        }

        return lagMilliseconds > 0
            ? MarketFreshnessMatch.Current(
                $"Accepted matching {qualityLabel} listings for {worldName} with {lagMilliseconds:N0}ms upload-revision skew.")
            : MarketFreshnessMatch.Current();
    }

    private static void AddAcceptedDetail(
        ICollection<string> acceptedDetails,
        MarketFreshnessMatch match)
    {
        if (!string.IsNullOrWhiteSpace(match.Detail))
            acceptedDetails.Add(match.Detail);
    }

    private static void CollectMatch(
        MarketFreshnessMatch match,
        ICollection<string> acceptedDetails,
        ICollection<MarketFreshnessGap> gaps)
    {
        if (match.IsCurrent)
            AddAcceptedDetail(acceptedDetails, match);
        else
            foreach (var gap in match.Gaps)
                gaps.Add(gap);
    }

    private static MarketFreshnessMatch ResolveContentDisagreement(
        string worldName,
        string qualityLabel,
        long aggregateUploadTime,
        long detailedUploadTime,
        string disagreement)
    {
        if (aggregateUploadTime > 0 && detailedUploadTime > aggregateUploadTime)
        {
            return MarketFreshnessMatch.Current(
                $"Accepted newer detailed {qualityLabel} listings for {worldName}; the aggregate projection is behind.");
        }

        var kind = aggregateUploadTime > detailedUploadTime
            ? MarketFreshnessGapKind.AggregateAhead
            : MarketFreshnessGapKind.Conflict;
        var detail = kind == MarketFreshnessGapKind.AggregateAhead
            ? $"{disagreement} The aggregate revision is newer, so only {worldName} requires repair."
            : $"{disagreement} Both projections report the same revision, so the world partition is inconsistent.";
        return MarketFreshnessMatch.Stale(
            detail,
            new MarketFreshnessGap(
                worldName,
                aggregateUploadTime,
                detailedUploadTime,
                kind,
                detail));
    }

    private static bool ListingMatchesWorld(
        MarketDataListing listing,
        MarketFreshnessProbe probe)
    {
        var expectedWorldId = probe.Nq?.WorldId ?? probe.Hq?.WorldId ?? 0;
        if (listing.WorldID > 0 && expectedWorldId > 0)
            return listing.WorldID == expectedWorldId;

        return string.Equals(
            string.IsNullOrWhiteSpace(listing.WorldName) ? probe.TargetName : listing.WorldName,
            probe.TargetName,
            StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class StringTupleComparer : IEqualityComparer<(string WorldName, MarketFreshnessGapKind Kind)>
{
    public static StringTupleComparer OrdinalIgnoreCase { get; } = new();

    public bool Equals(
        (string WorldName, MarketFreshnessGapKind Kind) x,
        (string WorldName, MarketFreshnessGapKind Kind) y)
        => x.Kind == y.Kind
           && string.Equals(x.WorldName, y.WorldName, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string WorldName, MarketFreshnessGapKind Kind) obj)
        => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.WorldName), obj.Kind);
}
