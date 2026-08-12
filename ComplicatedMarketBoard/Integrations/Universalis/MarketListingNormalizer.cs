using System.Text.Json;

namespace ComplicatedMarketBoard.Integrations.Universalis;

/// <summary>
/// Restores the listing-identity invariant when an upstream cache response repeats rows.
/// </summary>
public static class MarketListingNormalizer
{
    public static IReadOnlyList<MarketDataListing> Normalize(IEnumerable<MarketDataListing> listings)
    {
        ArgumentNullException.ThrowIfNull(listings);

        var normalized = new List<MarketDataListing>();
        var identityIndexes = new Dictionary<MarketListingIdentity, int>();
        foreach (var listing in listings)
        {
            if (listing is null)
                continue;

            if (!TryGetIdentity(listing, out var identity))
            {
                normalized.Add(listing);
                continue;
            }

            if (!identityIndexes.TryGetValue(identity, out var existingIndex))
            {
                identityIndexes.Add(identity, normalized.Count);
                normalized.Add(listing);
                continue;
            }

            if (listing.LastReviewTime > normalized[existingIndex].LastReviewTime)
                normalized[existingIndex] = listing;
        }

        return normalized;
    }

    public static MarketListingNormalizationResult Analyze(IEnumerable<MarketDataListing> listings)
    {
        ArgumentNullException.ThrowIfNull(listings);

        var unkeyed = new List<(int Index, MarketDataListing Listing)>();
        var keyed = new Dictionary<MarketListingIdentity, ListingGroup>();
        var index = 0;
        foreach (var listing in listings)
        {
            if (listing is null)
                continue;

            if (!TryGetIdentity(listing, out var identity))
            {
                unkeyed.Add((index++, listing));
                continue;
            }

            if (!keyed.TryGetValue(identity, out var group))
            {
                keyed.Add(identity, new ListingGroup(index++, listing));
                continue;
            }

            group.DuplicateCount++;
            if (!Equivalent(group.First, listing))
                group.HasConflict = true;
        }

        var conflicts = keyed
            .Where(pair => pair.Value.HasConflict)
            .Select(pair => pair.Key)
            .ToArray();
        var normalized = unkeyed
            .Concat(keyed
                .Where(pair => !pair.Value.HasConflict)
                .Select(pair => (pair.Value.Index, pair.Value.First)))
            .OrderBy(pair => pair.Index)
            .Select(pair => pair.Item2)
            .ToArray();
        return new MarketListingNormalizationResult(
            normalized,
            conflicts,
            keyed.Values.Sum(group => group.DuplicateCount));
    }

    public static bool TryGetIdentity(MarketDataListing listing, out MarketListingIdentity identity)
    {
        var listingId = listing.ListingId?.Trim();
        if (string.IsNullOrEmpty(listingId))
        {
            identity = default;
            return false;
        }

        var world = listing.WorldID != 0
            ? $"id:{listing.WorldID}"
            : $"name:{listing.WorldName?.Trim()}";
        identity = new MarketListingIdentity(world, listingId);
        return true;
    }

    private static bool Equivalent(MarketDataListing left, MarketDataListing right)
        => left.LastReviewTime == right.LastReviewTime
           && left.PricePerUnit == right.PricePerUnit
           && left.Quantity == right.Quantity
           && left.WorldID == right.WorldID
           && string.Equals(left.WorldName, right.WorldName, StringComparison.Ordinal)
           && left.Hq == right.Hq
           && left.IsCrafted == right.IsCrafted
           && left.OnMannequin == right.OnMannequin
           && string.Equals(left.RetainerName, right.RetainerName, StringComparison.Ordinal)
           && left.Tax == right.Tax
           && JsonSerializer.Serialize(left.Materia) == JsonSerializer.Serialize(right.Materia);

    private sealed class ListingGroup(int index, MarketDataListing first)
    {
        public int Index { get; } = index;
        public MarketDataListing First { get; } = first;
        public int DuplicateCount { get; set; }
        public bool HasConflict { get; set; }
    }
}

public readonly record struct MarketListingIdentity(string World, string ListingId);

public sealed record MarketListingNormalizationResult(
    IReadOnlyList<MarketDataListing> Listings,
    IReadOnlyList<MarketListingIdentity> Conflicts,
    int DuplicateCount);
