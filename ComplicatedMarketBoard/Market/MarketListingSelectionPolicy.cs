using ComplicatedMarketBoard.Integrations.Universalis;

namespace ComplicatedMarketBoard.Market;

public static class MarketListingSelectionPolicy
{
    public static int FindIndex(IReadOnlyList<MarketDataListing> listings, MarketDataListing? previous)
    {
        if (previous is null)
            return -1;
        var hasIdentity = MarketListingNormalizer.TryGetIdentity(previous, out var identity);
        for (var index = 0; index < listings.Count; index++)
            if (ReferenceEquals(listings[index], previous)
                || (hasIdentity && MarketListingNormalizer.TryGetIdentity(listings[index], out var candidate)
                                && candidate == identity))
                return index;
        return -1; // Removal must not select a different bargain at the same index.
    }
}
