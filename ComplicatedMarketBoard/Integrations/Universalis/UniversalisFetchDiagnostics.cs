using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Franthropy.Dalamud.Persistence;

namespace ComplicatedMarketBoard.Integrations.Universalis;

public sealed class UniversalisFetchDiagnosticSession
{
    private readonly object sync = new();
    private readonly long startedTimestamp = Stopwatch.GetTimestamp();
    private readonly List<UniversalisFetchDiagnosticEvent> events = [];
    private readonly List<UniversalisAffectedListingsResponse> affectedResponses = [];
    private int nextSequence;

    public UniversalisFetchDiagnosticSession(
        ulong itemId,
        string itemName,
        string requestedScope,
        bool requireCurrentDetails,
        int listingLimit,
        int entryLimit,
        bool highQualityOnly)
    {
        CorrelationId = Guid.NewGuid().ToString("N");
        StartedAtUtc = DateTimeOffset.UtcNow;
        ItemId = itemId;
        ItemName = itemName;
        RequestedScope = requestedScope;
        RequireCurrentDetails = requireCurrentDetails;
        ListingLimit = listingLimit;
        EntryLimit = entryLimit;
        HighQualityOnly = highQualityOnly;
    }

    public string CorrelationId { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public ulong ItemId { get; }
    public string ItemName { get; }
    public string RequestedScope { get; }
    public bool RequireCurrentDetails { get; }
    public int ListingLimit { get; }
    public int EntryLimit { get; }
    public bool HighQualityOnly { get; }

    public void Record(
        string phase,
        string target,
        string message,
        string? requestUri = null,
        int? attempt = null,
        int? verificationPass = null,
        int? statusCode = null,
        double? durationMilliseconds = null)
    {
        lock (sync)
        {
            events.Add(new UniversalisFetchDiagnosticEvent(
                ++nextSequence,
                DateTimeOffset.UtcNow,
                Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
                phase,
                target,
                message,
                requestUri,
                attempt,
                verificationPass,
                statusCode,
                durationMilliseconds));
        }
    }

    public void CaptureDetailedResponse(
        string target,
        string requestUri,
        int attempt,
        int verificationPass,
        DateTimeOffset requestStartedAtUtc,
        double durationMilliseconds,
        int statusCode,
        UniversalisRequestHeaders requestHeaders,
        UniversalisResponseHeaders headers,
        string payloadSha256,
        JsonElement rawPayload,
        MarketDataCurrent rawResponse,
        UniversalisResponse interpretedResponse)
    {
        var rawListings = rawResponse.Listings.ToArray();
        var normalizedListings = interpretedResponse.Listings.ToArray();
        if (rawListings.Length == normalizedListings.Length)
            return;

        var duplicateIdentities = rawListings
            .Select((listing, index) => new
            {
                Listing = listing,
                Index = index,
                HasIdentity = MarketListingNormalizer.TryGetIdentity(listing, out var identity),
                Identity = identity,
            })
            .Where(row => row.HasIdentity)
            .GroupBy(row => row.Identity)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var keptListing = normalizedListings.FirstOrDefault(listing =>
                    MarketListingNormalizer.TryGetIdentity(listing, out var identity) &&
                    identity == group.Key);
                var keptRawIndex = keptListing is null
                    ? (int?)null
                    : Array.FindIndex(rawListings, listing => ReferenceEquals(listing, keptListing));
                return new UniversalisDuplicateListingIdentity(
                    group.Key.World,
                    group.Key.ListingId,
                    group.Select(row => row.Index).ToArray(),
                    keptRawIndex >= 0 ? keptRawIndex : null);
            })
            .ToArray();

        var affected = new UniversalisAffectedListingsResponse(
            target,
            requestUri,
            attempt,
            verificationPass,
            requestStartedAtUtc,
            DateTimeOffset.UtcNow,
            durationMilliseconds,
            statusCode,
            requestHeaders,
            headers,
            payloadSha256,
            rawPayload,
            rawResponse.ItemId,
            rawResponse.LastUploadTime,
            rawResponse.WorldUploadTimes,
            rawResponse.UnitsForSale,
            rawListings.Length,
            normalizedListings.Length,
            rawListings.Length - normalizedListings.Length,
            duplicateIdentities,
            rawListings.Select(ToSnapshot).ToArray(),
            normalizedListings.Select(ToSnapshot).ToArray());

        lock (sync)
            affectedResponses.Add(affected);
    }

    public UniversalisFetchDiagnosticDocument? Finish(UniversalisResponse? outcome, Exception? exception)
    {
        Record(
            "fetch-finished",
            RequestedScope,
            exception is null
                ? $"Completed with status {outcome?.Status.ToString() ?? "none"}."
                : $"Ended with {exception.GetType().Name}: {exception.Message}");

        lock (sync)
        {
            if (affectedResponses.Count == 0)
                return null;

            return new UniversalisFetchDiagnosticDocument(
                SchemaVersion: 1,
                CorrelationId,
                StartedAtUtc,
                CapturedAtUtc: DateTimeOffset.UtcNow,
                PluginVersion: GetPluginVersion(),
                ItemId,
                ItemName,
                RequestedScope,
                RequireCurrentDetails,
                ListingLimit,
                EntryLimit,
                HighQualityOnly,
                OutcomeStatus: outcome?.Status,
                OutcomeDetail: exception?.Message ?? outcome?.FailureDetail,
                Events: events.OrderBy(entry => entry.Sequence).ToArray(),
                AffectedResponses: affectedResponses.ToArray());
        }
    }

    public static string ComputeSha256(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload));

    private static UniversalisListingSnapshot ToSnapshot(MarketDataListing listing, int index) =>
        new(
            index,
            listing.ListingId,
            listing.WorldID,
            listing.WorldName,
            listing.RetainerName,
            listing.PricePerUnit,
            listing.Quantity,
            listing.PricePerUnit * listing.Quantity,
            listing.Hq,
            listing.LastReviewTime,
            listing.Tax,
            listing.OnMannequin);

    private static string GetPluginVersion()
    {
        var assembly = typeof(UniversalisFetchDiagnosticSession).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }
}

public static class UniversalisFetchDiagnosticWriter
{
    private const int MaximumRetainedDiagnostics = 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string Write(string pluginConfigDirectory, UniversalisFetchDiagnosticDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginConfigDirectory);
        ArgumentNullException.ThrowIfNull(document);

        var directory = Path.Combine(pluginConfigDirectory, "diagnostics", "universalis");
        var fileName =
            $"duplicate-listings-{document.CapturedAtUtc:yyyyMMddTHHmmssfffZ}-item-{document.ItemId}-{document.CorrelationId[..8]}.json";
        var path = Path.Combine(directory, fileName);
        AtomicJsonFile.Write(path, document, JsonOptions);
        PruneOldDiagnostics(directory, path);
        return path;
    }

    private static void PruneOldDiagnostics(string directory, string preservedPath)
    {
        foreach (var oldPath in Directory
                     .EnumerateFiles(directory, "duplicate-listings-*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(MaximumRetainedDiagnostics))
        {
            if (string.Equals(oldPath, preservedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                File.Delete(oldPath);
            }
            catch (IOException)
            {
                // Retention cleanup must never invalidate the freshly captured evidence.
            }
            catch (UnauthorizedAccessException)
            {
                // A locked historical dump can wait for a later capture to prune it.
            }
        }
    }
}

public sealed record UniversalisFetchDiagnosticDocument(
    int SchemaVersion,
    string CorrelationId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CapturedAtUtc,
    string PluginVersion,
    ulong ItemId,
    string ItemName,
    string RequestedScope,
    bool RequireCurrentDetails,
    int ListingLimit,
    int EntryLimit,
    bool HighQualityOnly,
    ulong? OutcomeStatus,
    string? OutcomeDetail,
    IReadOnlyList<UniversalisFetchDiagnosticEvent> Events,
    IReadOnlyList<UniversalisAffectedListingsResponse> AffectedResponses);

public sealed record UniversalisFetchDiagnosticEvent(
    int Sequence,
    DateTimeOffset TimestampUtc,
    double ElapsedMilliseconds,
    string Phase,
    string Target,
    string Message,
    string? RequestUri,
    int? Attempt,
    int? VerificationPass,
    int? StatusCode,
    double? DurationMilliseconds);

public sealed record UniversalisAffectedListingsResponse(
    string Target,
    string RequestUri,
    int Attempt,
    int VerificationPass,
    DateTimeOffset RequestStartedAtUtc,
    DateTimeOffset ResponseCapturedAtUtc,
    double DurationMilliseconds,
    int StatusCode,
    UniversalisRequestHeaders RequestHeaders,
    UniversalisResponseHeaders ResponseHeaders,
    string PayloadSha256,
    JsonElement RawPayload,
    ulong ItemId,
    long LastUploadTime,
    IReadOnlyDictionary<string, long> WorldUploadTimes,
    long UnitsForSale,
    int RawListingCount,
    int NormalizedListingCount,
    int DuplicateRowCount,
    IReadOnlyList<UniversalisDuplicateListingIdentity> DuplicateIdentities,
    IReadOnlyList<UniversalisListingSnapshot> RawListings,
    IReadOnlyList<UniversalisListingSnapshot> InterpretedListings);

public sealed record UniversalisDuplicateListingIdentity(
    string World,
    string ListingId,
    IReadOnlyList<int> RawIndexes,
    int? KeptRawIndex);

public sealed record UniversalisListingSnapshot(
    int Index,
    string? ListingId,
    ulong WorldId,
    string WorldName,
    string RetainerName,
    long PricePerUnit,
    long Quantity,
    long Total,
    bool Hq,
    long LastReviewTime,
    long Tax,
    bool OnMannequin);

public sealed record UniversalisResponseHeaders(
    DateTimeOffset? Date,
    double? AgeSeconds,
    string? CacheControl,
    string? ETag,
    DateTimeOffset? LastModified,
    string? CfCacheStatus,
    string? CfRay,
    string? Server);

public sealed record UniversalisRequestHeaders(
    Version HttpVersion,
    bool ConnectionClose,
    string? CacheControl);
