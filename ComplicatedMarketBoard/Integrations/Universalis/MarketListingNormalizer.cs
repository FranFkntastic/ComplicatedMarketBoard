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
}

public readonly record struct MarketListingIdentity(string World, string ListingId);
