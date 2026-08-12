using ComplicatedMarketBoard.Integrations.Universalis;
using System.Text.Json;

namespace ComplicatedMarketBoard.Tests;

public sealed class MarketListingNormalizerTests
{
    [Fact]
    public void UniversalisListing_DeserializesAuthoritativeListingIdentity()
    {
        var listing = JsonSerializer.Deserialize<MarketDataListing>(
            """{"listingID":"listing-a","worldID":73,"pricePerUnit":575000,"quantity":1}""");

        Assert.NotNull(listing);
        Assert.Equal("listing-a", listing.ListingId);
        Assert.Equal((ulong)73, listing.WorldID);
    }

    [Fact]
    public void Analyze_CollapsesByteEquivalentRepeatedIdentity()
    {
        var first = Listing("listing-a", 73, 575_000, 100);
        var repeated = Listing("listing-a", 73, 575_000, 100);

        var result = MarketListingNormalizer.Analyze([first, repeated]);

        Assert.Same(first, Assert.Single(result.Listings));
        Assert.Empty(result.Conflicts);
        Assert.Equal(1, result.DuplicateCount);
    }

    [Fact]
    public void Analyze_QuarantinesConflictingRepeatedIdentity()
    {
        var first = Listing("listing-a", 73, 575_000, 100);
        var conflicting = Listing("listing-a", 73, 600_000, 200);

        var result = MarketListingNormalizer.Analyze([first, conflicting]);

        Assert.Empty(result.Listings);
        Assert.Equal(new MarketListingIdentity("id:73", "listing-a"), Assert.Single(result.Conflicts));
    }

    [Fact]
    public void Normalize_PreservesLegacyNewestObservationSelection()
    {
        var stale = Listing("listing-a", 73, 575_000, 100);
        var newest = Listing("listing-a", 73, 600_000, 200);

        var normalized = MarketListingNormalizer.Normalize([stale, newest]);

        Assert.Same(newest, Assert.Single(normalized));
    }

    [Fact]
    public void Normalize_PreservesSameListingIdFromDifferentWorlds()
    {
        var siren = Listing("listing-a", 73, 575_000, 100);
        var faerie = Listing("listing-a", 54, 575_000, 100);

        var normalized = MarketListingNormalizer.Normalize([siren, faerie]);

        Assert.Equal([siren, faerie], normalized);
    }

    [Fact]
    public void Normalize_PreservesRowsWithoutAuthoritativeIdentity()
    {
        var first = Listing(null, 73, 575_000, 100);
        var second = Listing(null, 73, 575_000, 100);

        var normalized = MarketListingNormalizer.Normalize([first, second]);

        Assert.Equal([first, second], normalized);
    }

    private static MarketDataListing Listing(
        string? listingId,
        ulong worldId,
        long price,
        long lastReviewTime) =>
        new()
        {
            ListingId = listingId,
            WorldID = worldId,
            WorldName = worldId.ToString(),
            PricePerUnit = price,
            Quantity = 1,
            LastReviewTime = lastReviewTime,
        };
}
