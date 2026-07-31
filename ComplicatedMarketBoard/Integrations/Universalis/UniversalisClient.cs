using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using ComplicatedMarketBoard.Assets;
using ComplicatedMarketBoard.Market;


namespace ComplicatedMarketBoard.Integrations.Universalis;

public sealed class UniversalisClient
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly object retryStateLock = new();
    private DateTimeOffset nextRequestAllowedAt = DateTimeOffset.MinValue;
    private TimeSpan currentRetryDelay = TimeSpan.Zero;

    public UniversalisClient()
    {
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(P.Config.RequestTimeout),
        };
    }

    public void Dispose()
    {
        httpClient.Dispose();
        requestGate.Dispose();
    }


    // -------------------------------- http client --------------------------------
    private const string Host = "https://universalis.app";
    private HttpClient httpClient;

    public void ReloadHttpClient()
    {
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(P.Config.RequestTimeout),
        };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "ComplicatedMarketBoard/1.0 (Dalamud; FFXIV)");
    }


    // -------------------------------- http methods --------------------------------
    public async Task<UniversalisResponse> GetDataAsync(
        MarketItem gameItem,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress = null)
    {
        return await GetData(gameItem, cancellationToken, reportProgress);
    }

    public Task<UniversalisResponse> GetDataForTargetAsync(
        MarketItem gameItem,
        string targetName,
        bool highQualityOnly,
        CancellationToken cancellationToken) =>
        GetDataForTarget(gameItem, targetName, cancellationToken, null, highQualityOnly);

    private async Task<UniversalisResponse> GetData(
        MarketItem gameItem,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress)
    {
        var customScope = P.Config.CustomMarketScopes.FirstOrDefault(scope => scope.Id == P.Config.selectedCustomScopeId);
        if (customScope is not null)
            return await GetCustomScopeData(gameItem, customScope, cancellationToken, reportProgress);

        return await GetDataForTarget(gameItem, gameItem.TargetRegion, cancellationToken, reportProgress);
    }

    private async Task<UniversalisResponse> GetCustomScopeData(
        MarketItem gameItem,
        CustomMarketScope customScope,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress)
    {
        var targets = P.MainWindow.ScopeCatalog.BuildQueryTargets(customScope.IncludedScopes, P.MainWindow.GetCurrentWorldScopeName());
        if (targets.Count == 0)
            return new UniversalisResponse { Status = UniversalisResponseStatus.InvalidData };

        var responses = new List<UniversalisResponse>();
        for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
            var target = targets[targetIndex];
            var response = await GetDataForTarget(
                gameItem,
                target,
                cancellationToken,
                progress => reportProgress?.Invoke(progress with
                {
                    StatusText = $"{customScope.Name}: {progress.StatusText} ({targetIndex + 1}/{targets.Count})",
                }));
            if (response.Status != UniversalisResponseStatus.Success)
            {
                Service.Log.Warning($"[Universalis] Custom scope '{customScope.Name}' failed while fetching '{target}'.");
                return response;
            }

            responses.Add(response);
        }

        return MergeCustomScopeResponses(gameItem, customScope, responses);
    }

    private static UniversalisResponse MergeCustomScopeResponses(MarketItem gameItem, CustomMarketScope customScope, List<UniversalisResponse> responses)
    {
        var mergedWorldOutOfDate = new Dictionary<string, double>();
        var mergedWorldUploadTimes = new Dictionary<string, long>();

        foreach (var response in responses)
        {
            foreach (var freshness in response.WorldOutOfDate)
            {
                if (!mergedWorldOutOfDate.TryGetValue(freshness.Key, out var existing) || freshness.Value < existing)
                    mergedWorldOutOfDate[freshness.Key] = freshness.Value;
            }

            foreach (var uploadTime in response.WorldUploadTimes)
            {
                if (!mergedWorldUploadTimes.TryGetValue(uploadTime.Key, out var existing) || uploadTime.Value > existing)
                    mergedWorldUploadTimes[uploadTime.Key] = uploadTime.Value;
            }
        }

        return new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            ItemId = (ulong)gameItem.Id,
            IsCrossWorld = true,
            WorldOutOfDate = mergedWorldOutOfDate,
            FetchTime = responses.Min(response => response.FetchTime),
            LatestUploadTime = mergedWorldUploadTimes.Count > 0 ? mergedWorldUploadTimes.Values.Max() : responses.Max(response => response.LatestUploadTime),
            WorldUploadTimes = mergedWorldUploadTimes,
            UnitsForSale = responses.Sum(response => response.UnitsForSale),
            AveragePrice = AverageWeightedByListings(responses, response => response.AveragePrice),
            AveragePriceNq = AverageWeightedByListings(responses, response => response.AveragePriceNq),
            AveragePriceHq = AverageWeightedByListings(responses, response => response.AveragePriceHq),
            Velocity = responses.Sum(response => response.Velocity),
            VelocityNq = responses.Sum(response => response.VelocityNq),
            VelocityHq = responses.Sum(response => response.VelocityHq),
            Listings = MarketListingNormalizer.Normalize(
                    responses.SelectMany(response => response.Listings))
                .OrderBy(listing => listing.PricePerUnit)
                .ThenBy(listing => listing.Quantity)
                .Take(P.Config.UniversalisListings)
                .ToList(),
            Entries = responses
                .SelectMany(response => response.Entries)
                .OrderByDescending(entry => entry.Timestamp)
                .Take(P.Config.UniversalisEntries)
                .ToList(),
            ScopeName = customScope.Name,
        };
    }

    private static double AverageWeightedByListings(List<UniversalisResponse> responses, Func<UniversalisResponse, double> selector)
    {
        var totalListings = responses.Sum(response => response.Listings.Count);
        if (totalListings == 0)
            return responses.Average(selector);

        return responses.Sum(response => selector(response) * response.Listings.Count) / totalListings;
    }

    private async Task<UniversalisResponse> GetDataForTarget(
        MarketItem gameItem,
        string targetName,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress,
        bool? highQualityOnly = null)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            var _hq = (highQualityOnly ?? P.Config.UniversalisHqOnly) ? "&hq=1" : "";
            var targetRegion = P.MainWindow.ScopeCatalog.NormalizeForUniversalis(targetName);
            var API_URL = new UriBuilder($"{Host}/api/v2/{targetRegion}/{gameItem.Id}?listings={P.Config.UniversalisListings}&entries={P.Config.UniversalisEntries}{_hq}").Uri.ToString();

            await WaitForSharedCooldownAsync(targetName, reportProgress, cancellationToken);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                reportProgress?.Invoke(new UniversalisRequestProgress(
                    $"Fetching Universalis data for {gameItem.Name} ({targetName}, attempt {attempt}/{MaxAttempts})",
                    0.35f));

                try
                {
                    Service.Log.Info($"[Universalis] Fetch attempt {attempt}/{MaxAttempts}: {API_URL}");
                    using var response = await httpClient.GetAsync(API_URL, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        var failure = CreateServerError(response.StatusCode);
                        if (!IsTransient(response.StatusCode) || attempt == MaxAttempts)
                        {
                            if (IsTransient(response.StatusCode))
                                StartFailureCooldown();

                            Service.Log.Warning($"[Universalis] HTTP request not successful: {response.StatusCode}");
                            return failure;
                        }

                        var retryDelay = RegisterTransientFailure(GetRetryAfterDelay(response));
                        await WaitForRetryAsync(targetName, response.StatusCode.ToString(), attempt + 1, retryDelay, reportProgress, cancellationToken);
                        continue;
                    }

                    var data = await response.Content.ReadFromJsonAsync<MarketDataCurrent>(cancellationToken: cancellationToken);
                    if (data is null)
                    {
                        Service.Log.Warning($"[Universalis] Parse JSON failed");
                        return new UniversalisResponse { Status = UniversalisResponseStatus.InvalidData, FailureDetail = "Universalis returned no market data." };
                    }

                    ReportSuccess();
                    return BuildSuccessResponse(data, gameItem, targetName);
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (attempt == MaxAttempts)
                    {
                        StartFailureCooldown();
                        Service.Log.Warning($"[Universalis] Request timed out after {P.Config.RequestTimeout}s.");
                        return new UniversalisResponse
                        {
                            Status = UniversalisResponseStatus.UserCancellation,
                            FailureDetail = $"Universalis timed out after {P.Config.RequestTimeout}s.",
                        };
                    }

                    var retryDelay = RegisterTransientFailure(null);
                    await WaitForRetryAsync(targetName, "timed out", attempt + 1, retryDelay, reportProgress, cancellationToken);
                }
                catch (HttpRequestException ex)
                {
                    if (attempt == MaxAttempts)
                    {
                        StartFailureCooldown();
                        Service.Log.Warning(ex, "[Universalis] Connection failed after all retry attempts.");
                        return new UniversalisResponse
                        {
                            Status = UniversalisResponseStatus.ServerError,
                            FailureDetail = "Unable to connect to Universalis.",
                        };
                    }

                    var retryDelay = RegisterTransientFailure(null);
                    await WaitForRetryAsync(targetName, "connection failed", attempt + 1, retryDelay, reportProgress, cancellationToken);
                }
                catch (JsonException ex)
                {
                    Service.Log.Warning(ex, "[Universalis] Parse JSON failed.");
                    return new UniversalisResponse
                    {
                        Status = UniversalisResponseStatus.InvalidData,
                        FailureDetail = "Universalis returned invalid market data.",
                    };
                }
            }

            throw new InvalidOperationException("Universalis retry loop exited without a result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"[Universalis] Unknown error: {ex.Message}");
            return new UniversalisResponse { Status = UniversalisResponseStatus.UnknownError, FailureDetail = "Unexpected Universalis request error." };
        }
        finally
        {
            requestGate.Release();
        }
    }

    private async Task WaitForSharedCooldownAsync(
        string targetName,
        Action<UniversalisRequestProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset allowedAt;
        lock (retryStateLock)
            allowedAt = nextRequestAllowedAt;

        await WaitWithProgressAsync(
            allowedAt - DateTimeOffset.UtcNow,
            remaining => $"Universalis recovery cooldown for {targetName}: waiting {FormatDelay(remaining)}",
            0.20f,
            reportProgress,
            cancellationToken);
    }

    private async Task WaitForRetryAsync(
        string targetName,
        string reason,
        int nextAttempt,
        TimeSpan retryDelay,
        Action<UniversalisRequestProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        await WaitWithProgressAsync(
            retryDelay,
            remaining => $"Universalis {reason} for {targetName}; retrying attempt {nextAttempt}/{MaxAttempts} in {FormatDelay(remaining)}",
            0.35f,
            reportProgress,
            cancellationToken);
    }

    private static async Task WaitWithProgressAsync(
        TimeSpan delay,
        Func<TimeSpan, string> statusText,
        float progress,
        Action<UniversalisRequestProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
            return;

        var deadline = DateTimeOffset.UtcNow + delay;
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return;

            reportProgress?.Invoke(new UniversalisRequestProgress(statusText(remaining), progress));
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(250, remaining.TotalMilliseconds)), cancellationToken);
        }
    }

    private TimeSpan RegisterTransientFailure(TimeSpan? retryAfter)
    {
        lock (retryStateLock)
        {
            currentRetryDelay = currentRetryDelay <= TimeSpan.Zero
                ? InitialRetryDelay
                : TimeSpan.FromMilliseconds(Math.Min(MaximumRetryDelay.TotalMilliseconds, currentRetryDelay.TotalMilliseconds * 3));

            var retryDelay = retryAfter is { } requestedDelay
                ? requestedDelay > currentRetryDelay ? requestedDelay : currentRetryDelay
                : currentRetryDelay;
            nextRequestAllowedAt = DateTimeOffset.UtcNow + retryDelay;
            return retryDelay;
        }
    }

    private void ReportSuccess()
    {
        lock (retryStateLock)
        {
            currentRetryDelay = currentRetryDelay <= InitialRetryDelay
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(currentRetryDelay.TotalMilliseconds / 3);
        }
    }

    private void StartFailureCooldown()
    {
        lock (retryStateLock)
        {
            var cooldownUntil = DateTimeOffset.UtcNow + FailureCooldown;
            if (cooldownUntil > nextRequestAllowedAt)
                nextRequestAllowedAt = cooldownUntil;
        }
    }

    private static bool IsTransient(System.Net.HttpStatusCode statusCode)
        => statusCode is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.GatewayTimeout;

    private static UniversalisResponse CreateServerError(System.Net.HttpStatusCode statusCode)
        => new()
        {
            Status = UniversalisResponseStatus.ServerError,
            FailureDetail = $"Universalis returned HTTP {(int)statusCode} ({statusCode}).",
        };

    private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } retryDelay)
            return retryDelay;

        if (response.Headers.RetryAfter?.Date is { } retryAt)
            return retryAt - DateTimeOffset.UtcNow;

        return null;
    }

    private static string FormatDelay(TimeSpan delay)
        => $"{Math.Max(0, delay.TotalSeconds):F1}s";

    private static UniversalisResponse BuildSuccessResponse(MarketDataCurrent data, MarketItem gameItem, string targetName)
    {
        var fetchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var worldUpdatedData = new Dictionary<string, double>();
        var worldUploadTimes = new Dictionary<string, long>();
        if (data.WorldUploadTimes.Count > 0)
        {
            var orderedWorldUploadTimes = data.WorldUploadTimes.OrderByDescending(w => w.Value).ToList();
            foreach (var i in orderedWorldUploadTimes)
            {
                var worldRow = Data.WorldSheet.GetRow(uint.Parse(i.Key));
                var worldName = worldRow.Name.ToString();
                var hours = (fetchTime - i.Value) / 1000d / 3600d;
                worldUpdatedData.Add(worldName, hours);
                worldUploadTimes.Add(worldName, i.Value);
            }
        }
        else
        {
            worldUpdatedData.Add(targetName, (fetchTime - data.LastUploadTime) / 1000d / 3600d);
            worldUploadTimes.Add(targetName, data.LastUploadTime);
            foreach (var listing in data.Listings)
            {
                if (string.IsNullOrWhiteSpace(listing.WorldName))
                    listing.WorldName = targetName;
            }

            foreach (var entry in data.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.WorldName))
                    entry.WorldName = targetName;
            }
        }

        var universalisResponse = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            ItemId = data.ItemId,
            IsCrossWorld = data.WorldUploadTimes.Count > 0,
            WorldOutOfDate = worldUpdatedData,
            FetchTime = fetchTime,
            LatestUploadTime = worldUploadTimes.Count > 0 ? worldUploadTimes.Values.Max() : data.LastUploadTime,
            WorldUploadTimes = worldUploadTimes,
            UnitsForSale = data.UnitsForSale,
            AveragePrice = data.AveragePrice,
            AveragePriceNq = data.AveragePriceNq,
            AveragePriceHq = data.AveragePriceHq,
            Velocity = data.Velocity,
            VelocityNq = data.VelocityNq,
            VelocityHq = data.VelocityHq,
            Listings = MarketListingNormalizer.Normalize(data.Listings).ToList(),
            Entries = data.Entries,
            ScopeName = targetName,
        };
        Service.Log.Debug(
            $"[Universalis] Response {gameItem.Id} for {targetName}: " +
            $"{universalisResponse.Listings.Count} listings, {universalisResponse.Entries.Count} sales, " +
            $"latest upload {universalisResponse.LatestUploadTime}.");

        return universalisResponse;
    }
}

public sealed record UniversalisRequestProgress(string StatusText, float Progress);
