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
    MarketMinimumProbe? Hq);

public sealed record MarketFreshnessMatch(bool IsCurrent, string Detail)
{
    public static MarketFreshnessMatch Current(string detail = "")
        => new(true, detail);

    public static MarketFreshnessMatch Stale(string detail)
        => new(false, detail);
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
        var isTruncated = listingLimit > 0 && detailed.RawListingCount >= listingLimit;
        var cutoffPrice = isTruncated
            ? detailed.RawListingCutoffPrice
            : null;

        foreach (var probe in worldProbes)
        {
            var nqMatch = hqOnly
                ? MarketFreshnessMatch.Current()
                : CompareWorldQuality(probe, probe.Nq, detailed, false, cutoffPrice);
            if (!nqMatch.IsCurrent)
                return nqMatch;
            AddAcceptedDetail(acceptedDetails, nqMatch);

            var hqMatch = CompareWorldQuality(probe, probe.Hq, detailed, true, cutoffPrice);
            if (!hqMatch.IsCurrent)
                return hqMatch;
            AddAcceptedDetail(acceptedDetails, hqMatch);
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
        var actual = detailed.Listings
            .Where(listing => listing.Hq == hq && ListingMatchesWorld(listing, probe))
            .OrderBy(listing => listing.PricePerUnit)
            .ThenBy(listing => listing.Quantity)
            .FirstOrDefault();

        if (expected is null)
        {
            return actual is null
                ? MarketFreshnessMatch.Current()
                : MarketFreshnessMatch.Stale(
                    $"Universalis now reports no {qualityLabel} listings on {probe.TargetName}, but detailed listings still contain {actual.PricePerUnit:N0} gil.");
        }

        if (actual is null)
        {
            if (cutoffPrice is not null && expected.PricePerUnit >= cutoffPrice.Value)
                return MarketFreshnessMatch.Current();

            return MarketFreshnessMatch.Stale(
                $"Universalis reports a current {qualityLabel} minimum of {expected.PricePerUnit:N0} gil on {probe.TargetName}, but the detailed scope is missing it.");
        }

        if (actual.PricePerUnit != expected.PricePerUnit)
        {
            return MarketFreshnessMatch.Stale(
                $"Universalis reports a current {qualityLabel} minimum of {expected.PricePerUnit:N0} gil on {probe.TargetName}; detailed listings still show {actual.PricePerUnit:N0} gil.");
        }

        if (expected.UploadTime <= 0)
            return MarketFreshnessMatch.Current();

        var detailedUploadTime = detailed.WorldUploadTimes.TryGetValue(probe.TargetName, out var uploadTime)
            ? uploadTime
            : 0;
        return CompareUploadRevision(
            expected.UploadTime,
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
        var lagMilliseconds = expectedUploadTime - detailedUploadTime;
        if (detailedUploadTime <= 0 || lagMilliseconds > UploadRevisionToleranceMilliseconds)
        {
            return MarketFreshnessMatch.Stale(
                $"Detailed listings for {worldName} are older than Universalis's current {qualityLabel} minimum.");
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
