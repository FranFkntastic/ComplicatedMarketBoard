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
    public static MarketFreshnessMatch Current()
        => new(true, "");

    public static MarketFreshnessMatch Stale(string detail)
        => new(false, detail);
}

public static class MarketFreshnessMatcher
{
    public static MarketFreshnessMatch CompareScope(
        IReadOnlyCollection<MarketFreshnessProbe> worldProbes,
        UniversalisResponse detailed,
        bool hqOnly,
        int listingLimit)
    {
        var isTruncated = listingLimit > 0 && detailed.Listings.Count >= listingLimit;
        var cutoffPrice = isTruncated && detailed.Listings.Count > 0
            ? detailed.Listings.Max(listing => listing.PricePerUnit)
            : (long?)null;

        foreach (var probe in worldProbes)
        {
            var nqMatch = hqOnly
                ? MarketFreshnessMatch.Current()
                : CompareWorldQuality(probe, probe.Nq, detailed, false, cutoffPrice);
            if (!nqMatch.IsCurrent)
                return nqMatch;

            var hqMatch = CompareWorldQuality(probe, probe.Hq, detailed, true, cutoffPrice);
            if (!hqMatch.IsCurrent)
                return hqMatch;
        }

        return MarketFreshnessMatch.Current();
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
                return CompareQuality(
                    probe.TargetName,
                    secondaryHq ? probe.Hq : probe.Nq,
                    detailed,
                    secondaryHq);
            }

            return MarketFreshnessMatch.Current();
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
        return detailedUploadTime >= expected.UploadTime
            ? MarketFreshnessMatch.Current()
            : MarketFreshnessMatch.Stale(
                $"Detailed listings for {expected.WorldName} are older than Universalis's current {qualityLabel} minimum.");
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
        return detailedUploadTime >= expected.UploadTime
            ? MarketFreshnessMatch.Current()
            : MarketFreshnessMatch.Stale(
                $"Detailed listings for {probe.TargetName} are older than Universalis's current {qualityLabel} minimum.");
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
