using ComplicatedMarketBoard.Integrations.Universalis;
using System.Net;
using System.Text.Json;

namespace ComplicatedMarketBoard.Tests;

public sealed class UniversalisFetchDiagnosticSessionTests
{
    [Fact]
    public void Finish_CapturesRawAndInterpretedRowsForDuplicateIdentities()
    {
        var first = Listing("listing-a", 91, 38, 3_000, 100, "Barba");
        var newest = Listing("listing-a", 91, 39, 3_000, 200, "Barba");
        var unique = Listing("listing-b", 91, 40, 150, 150, "Faratam");
        var raw = new MarketDataCurrent
        {
            ItemId = 8,
            LastUploadTime = 1_777_777_777_000,
            UnitsForSale = 6_150,
            WorldName = "Balmung",
            Listings = [first, newest, unique],
        };
        var interpreted = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            Listings = MarketListingNormalizer.Normalize(raw.Listings).ToList(),
        };
        var session = CreateSession();
        session.Record("detail-request-started", "Balmung", "Request started.");

        session.CaptureDetailedResponse(
            "Balmung",
            "https://universalis.app/api/v2/Balmung/8?listings=50&entries=50",
            attempt: 1,
            verificationPass: 1,
            DateTimeOffset.UtcNow,
            durationMilliseconds: 125,
            statusCode: 200,
            new UniversalisRequestHeaders(HttpVersion.Version11, true, "no-cache, no-store"),
            new UniversalisResponseHeaders(null, 12, "public, max-age=60", null, null, "HIT", "ray", "cloudflare"),
            new string('A', 64),
            JsonDocument.Parse("""{"itemID":8,"listings":[{"listingID":"listing-a"},{"listingID":"listing-a"}]}""").RootElement.Clone(),
            raw,
            interpreted);

        var document = session.Finish(interpreted, null);

        Assert.NotNull(document);
        var response = Assert.Single(document.AffectedResponses);
        Assert.Equal(3, response.RawListingCount);
        Assert.Equal(2, response.NormalizedListingCount);
        Assert.Equal(1, response.DuplicateRowCount);
        Assert.Equal([0, 1], Assert.Single(response.DuplicateIdentities).RawIndexes);
        Assert.Equal(1, Assert.Single(response.DuplicateIdentities).KeptRawIndex);
        Assert.Equal([38L, 39L, 40L], response.RawListings.Select(listing => listing.PricePerUnit));
        Assert.Equal([39L, 40L], response.InterpretedListings.Select(listing => listing.PricePerUnit));
        Assert.Equal(JsonValueKind.Object, response.RawPayload.ValueKind);
        Assert.True(response.RequestHeaders.ConnectionClose);
        Assert.Contains(document.Events, entry => entry.Phase == "detail-request-started");
        Assert.Contains(document.Events, entry => entry.Phase == "fetch-finished");

        var directory = Path.Combine(Path.GetTempPath(), $"cmb-universalis-diagnostic-{Guid.NewGuid():N}");
        try
        {
            var path = UniversalisFetchDiagnosticWriter.Write(directory, document);
            using var written = JsonDocument.Parse(File.ReadAllText(path));
            var affected = written.RootElement.GetProperty("affectedResponses")[0];
            Assert.Equal(3, affected.GetProperty("rawListingCount").GetInt32());
            Assert.Equal(2, affected.GetProperty("rawPayload").GetProperty("listings").GetArrayLength());
            Assert.Equal("no-cache, no-store", affected.GetProperty("requestHeaders").GetProperty("cacheControl").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Finish_DoesNotCreateEvidenceWhenEveryListingIdentityIsUnique()
    {
        var raw = new MarketDataCurrent
        {
            ItemId = 8,
            Listings =
            [
                Listing("listing-a", 91, 38, 3_000, 100, "Barba"),
                Listing("listing-b", 91, 40, 150, 150, "Faratam"),
            ],
        };
        var interpreted = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            Listings = MarketListingNormalizer.Normalize(raw.Listings).ToList(),
        };
        var session = CreateSession();

        session.CaptureDetailedResponse(
            "Balmung",
            "https://universalis.app/api/v2/Balmung/8?listings=50&entries=50",
            attempt: 1,
            verificationPass: 0,
            DateTimeOffset.UtcNow,
            durationMilliseconds: 125,
            statusCode: 200,
            new UniversalisRequestHeaders(HttpVersion.Version11, false, null),
            new UniversalisResponseHeaders(null, null, null, null, null, null, null, null),
            new string('B', 64),
            JsonDocument.Parse("""{"itemID":8,"listings":[]}""").RootElement.Clone(),
            raw,
            interpreted);

        Assert.Null(session.Finish(interpreted, null));
    }

    private static UniversalisFetchDiagnosticSession CreateSession() =>
        new(
            8,
            "Fire Crystal",
            "Balmung",
            requireCurrentDetails: true,
            listingLimit: 50,
            entryLimit: 50,
            highQualityOnly: false);

    private static MarketDataListing Listing(
        string listingId,
        ulong worldId,
        long price,
        long quantity,
        long lastReviewTime,
        string retainerName) =>
        new()
        {
            ListingId = listingId,
            WorldID = worldId,
            WorldName = "Balmung",
            PricePerUnit = price,
            Quantity = quantity,
            LastReviewTime = lastReviewTime,
            RetainerName = retainerName,
        };
}
