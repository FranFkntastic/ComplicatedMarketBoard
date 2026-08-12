using System.Net.Http.Json;
using System.Net.Http;
using System.Net;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using ComplicatedMarketBoard.Assets;
using ComplicatedMarketBoard.Market;


namespace ComplicatedMarketBoard.Integrations.Universalis;

public sealed class UniversalisClient
{
    private const int MaxAttempts = 3;
    private const int AggregateMaxAttempts = 4;
    private const int MaxConcurrentRequests = 3;
    private static readonly JsonSerializerOptions UniversalisJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan FreshDetailTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FreshDetailRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FreshListingRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FreshListingInitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FreshListingMaximumRetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AggregateRequestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AggregateInitialRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AggregateMaximumRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumRequestStartInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim requestGate = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private readonly SemaphoreSlim requestStartGate = new(1, 1);
    private readonly object retryStateLock = new();
    private readonly AsyncLocal<UniversalisFetchDiagnosticSession?> activeDiagnosticSession = new();
    private DateTimeOffset nextRequestAllowedAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextRequestStartAt = DateTimeOffset.MinValue;

    public UniversalisClient()
    {
        httpClient = CreateHttpClient();
    }

    public void Dispose()
    {
        httpClient.Dispose();
        requestGate.Dispose();
        requestStartGate.Dispose();
    }


    // -------------------------------- http client --------------------------------
    private const string Host = "https://universalis.app";
    private HttpClient httpClient;

    public void ReloadHttpClient()
    {
        var previousClient = httpClient;
        httpClient = CreateHttpClient();
        previousClient.Dispose();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        client.DefaultRequestHeaders.Add("User-Agent", "ComplicatedMarketBoard/1.0 (Dalamud; FFXIV)");
        return client;
    }


    // -------------------------------- http methods --------------------------------
    public async Task<UniversalisResponse> GetDataAsync(
        MarketItem gameItem,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress = null,
        bool requireCurrentDetails = false,
        MarketRefreshVocabulary? vocabulary = null)
    {
        return await RunWithDiagnosticsAsync(
            gameItem,
            gameItem.TargetRegion,
            requireCurrentDetails,
            () => GetData(
                gameItem,
                cancellationToken,
                reportProgress,
                requireCurrentDetails,
                vocabulary ?? MarketRefreshVocabulary.Standard));
    }

    public async Task<UniversalisResponse> GetDataForTargetAsync(
        MarketItem gameItem,
        string targetName,
        bool highQualityOnly,
        CancellationToken cancellationToken) =>
        await RunWithDiagnosticsAsync(
            gameItem,
            targetName,
            requireCurrentDetails: false,
            () => GetDataForTarget(
                gameItem,
                targetName,
                cancellationToken,
                null,
                MarketRefreshVocabulary.Standard,
                highQualityOnly: highQualityOnly),
            highQualityOnly);

    private async Task<UniversalisResponse> RunWithDiagnosticsAsync(
        MarketItem gameItem,
        string requestedScope,
        bool requireCurrentDetails,
        Func<Task<UniversalisResponse>> request,
        bool? highQualityOnly = null)
    {
        if (activeDiagnosticSession.Value is not null)
            return await request();

        var session = new UniversalisFetchDiagnosticSession(
            gameItem.Id,
            gameItem.Name,
            requestedScope,
            requireCurrentDetails,
            P.Config.UniversalisListings,
            P.Config.UniversalisEntries,
            highQualityOnly ?? P.Config.UniversalisHqOnly);
        activeDiagnosticSession.Value = session;
        UniversalisResponse? outcome = null;
        Exception? failure = null;
        session.Record(
            "fetch-started",
            requestedScope,
            $"Started Universalis fetch for item {gameItem.Id}; current-details={requireCurrentDetails}.");

        try
        {
            outcome = await request();
            return outcome;
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            activeDiagnosticSession.Value = null;
            var document = session.Finish(outcome, failure);
            if (document is not null)
            {
                try
                {
                    var path = UniversalisFetchDiagnosticWriter.Write(
                        Service.PluginInterface.GetPluginConfigDirectory(),
                        document);
                    Service.Log.Warning(
                        $"[Universalis] Duplicate listing evidence {session.CorrelationId[..8]} written to {path}");
                }
                catch (Exception ex)
                {
                    Service.Log.Error(ex, "[Universalis] Failed to write duplicate-listing diagnostic evidence.");
                }
            }
        }
    }

    private void TraceFetch(
        string phase,
        string target,
        string message,
        string? requestUri = null,
        int? attempt = null,
        int? verificationPass = null,
        int? statusCode = null,
        double? durationMilliseconds = null) =>
        activeDiagnosticSession.Value?.Record(
            phase,
            target,
            message,
            requestUri,
            attempt,
            verificationPass,
            statusCode,
            durationMilliseconds);

    private async Task<UniversalisResponse> GetData(
        MarketItem gameItem,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress,
        bool requireCurrentDetails,
        MarketRefreshVocabulary vocabulary)
    {
        var worldProbeCache = new Dictionary<string, MarketFreshnessProbe>(StringComparer.OrdinalIgnoreCase);
        var customScope = P.Config.CustomMarketScopes.FirstOrDefault(scope => scope.Id == P.Config.selectedCustomScopeId);
        if (customScope is not null)
            return await GetCustomScopeData(
                gameItem,
                customScope,
                cancellationToken,
                reportProgress,
                requireCurrentDetails,
                worldProbeCache,
                vocabulary);

        return requireCurrentDetails
            ? await GetCurrentDataForTarget(
                gameItem,
                gameItem.TargetRegion,
                cancellationToken,
                reportProgress,
                worldProbeCache,
                vocabulary)
            : await GetDataForTarget(
                gameItem,
                gameItem.TargetRegion,
                cancellationToken,
                reportProgress,
                vocabulary);
    }

    private async Task<UniversalisResponse> GetCustomScopeData(
        MarketItem gameItem,
        CustomMarketScope customScope,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress,
        bool requireCurrentDetails,
        Dictionary<string, MarketFreshnessProbe> worldProbeCache,
        MarketRefreshVocabulary vocabulary)
    {
        var targets = P.MainWindow.ScopeCatalog.BuildQueryTargets(customScope.IncludedScopes, P.MainWindow.GetCurrentWorldScopeName());
        if (targets.Count == 0)
            return new UniversalisResponse { Status = UniversalisResponseStatus.InvalidData };

        var responses = new List<UniversalisResponse>();
        for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
            var target = targets[targetIndex];
            var targetProgress = new Action<UniversalisRequestProgress>(progress => reportProgress?.Invoke(progress with
            {
                StatusText = $"{customScope.Name}: {progress.StatusText} ({targetIndex + 1}/{targets.Count})",
            }));
            var response = requireCurrentDetails
                ? await GetCurrentDataForTarget(
                    gameItem,
                    target,
                    cancellationToken,
                    targetProgress,
                    worldProbeCache,
                    vocabulary)
                : await GetDataForTarget(
                    gameItem,
                    target,
                    cancellationToken,
                    targetProgress,
                    vocabulary);
            if (response.Status != UniversalisResponseStatus.Success)
            {
                Service.Log.Warning($"[Universalis] Custom scope '{customScope.Name}' failed while fetching '{target}'.");
                return response;
            }

            responses.Add(response);
        }

        return MergeCustomScopeResponses(gameItem, customScope, responses);
    }

    private async Task<UniversalisResponse> GetCurrentDataForTarget(
        MarketItem gameItem,
        string targetName,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress,
        Dictionary<string, MarketFreshnessProbe> worldProbeCache,
        MarketRefreshVocabulary vocabulary)
    {
        var worldNames = P.MainWindow.ScopeCatalog
            .ExpandToWorldNames([targetName], P.MainWindow.GetCurrentWorldScopeName());
        if (worldNames.Count == 0)
            worldNames.Add(targetName);

        var scanStartedAt = Stopwatch.GetTimestamp();
        TraceFetch(
            "aggregate-scan-started",
            targetName,
            $"Scanning {worldNames.Count} worlds with concurrency {MaxConcurrentRequests} and {MinimumRequestStartInterval.TotalMilliseconds:F0}ms request spacing.");
        Service.Log.Info(
            $"[Universalis] Aggregate scan started for {targetName}: {worldNames.Count} worlds, " +
            $"concurrency {MaxConcurrentRequests}, {MinimumRequestStartInterval.TotalMilliseconds:F0}ms request spacing.");

        var worldProbes = new MarketFreshnessProbe?[worldNames.Count];
        var completedWorldCount = 0;
        var pendingProbes = new List<Task<(int Index, string WorldName, MarketFreshnessProbe? Probe, UniversalisResponse? Failure)>>();
        for (var worldIndex = 0; worldIndex < worldNames.Count; worldIndex++)
        {
            var worldName = worldNames[worldIndex];
            if (worldProbeCache.TryGetValue(worldName, out var cachedProbe))
            {
                worldProbes[worldIndex] = cachedProbe;
                completedWorldCount++;
                continue;
            }

            var capturedIndex = worldIndex;
            pendingProbes.Add(FetchWorldProbeAsync(capturedIndex, worldName));
        }

        var fetchedProbes = await Task.WhenAll(pendingProbes);
        foreach (var result in fetchedProbes.OrderBy(result => result.Index))
        {
            if (result.Failure is not null)
                return result.Failure;
            if (result.Probe is null)
            {
                return new UniversalisResponse
                {
                    Status = UniversalisResponseStatus.InvalidData,
                    FailureDetail = $"Universalis returned no aggregate freshness data for {gameItem.Name} on {result.WorldName}.",
                };
            }

            worldProbeCache[result.WorldName] = result.Probe;
            worldProbes[result.Index] = result.Probe;
        }

        var resolvedWorldProbes = worldProbes
            .Select(probe => probe ?? throw new InvalidOperationException("A market freshness probe was not populated."))
            .ToArray();
        Service.Log.Info(
            $"[Universalis] Aggregate scan completed for {targetName}: {resolvedWorldProbes.Length} worlds in " +
            $"{Stopwatch.GetElapsedTime(scanStartedAt).TotalSeconds:F2}s.");
        TraceFetch(
            "aggregate-scan-completed",
            targetName,
            $"Resolved freshness probes for {resolvedWorldProbes.Length} worlds.",
            durationMilliseconds: Stopwatch.GetElapsedTime(scanStartedAt).TotalMilliseconds);

        var deadline = DateTimeOffset.UtcNow + FreshDetailTimeout;
        var detailedTask = GetListingDataForTarget(
            gameItem,
            targetName,
            cancellationToken,
            reportProgress,
            vocabulary,
            verificationPass: 1,
            requestProgress: GetFreshDetailProgress(FreshDetailTimeout));
        var historyTask = GetDataForTarget(
            gameItem,
            targetName,
            cancellationToken,
            reportProgress: null,
            vocabulary,
            listingLimitOverride: 0,
            entryLimitOverride: P.Config.UniversalisEntries);
        await Task.WhenAll(detailedTask, historyTask);
        var detailed = await detailedTask;
        var history = await historyTask;
        if (detailed.Status != UniversalisResponseStatus.Success)
            return detailed;
        if (history.Status != UniversalisResponseStatus.Success)
            return history;

        var lastMatch = MarketFreshnessMatcher.CompareScope(
            resolvedWorldProbes,
            detailed,
            P.Config.UniversalisHqOnly,
            P.Config.UniversalisListings);
        if (lastMatch.IsCurrent)
        {
            TraceFetch(
                "detail-verification-accepted",
                targetName,
                lastMatch.Detail,
                verificationPass: 1);
            return MarketListingReconciler.FinalizeVerifiedResponse(
                detailed,
                history,
                P.Config.UniversalisListings);
        }

        var conflict = lastMatch.Gaps.FirstOrDefault(gap => gap.Kind == MarketFreshnessGapKind.Conflict);
        if (conflict is not null)
            return CreateStaleResponse(vocabulary, lastMatch.Detail);

        var repairWorlds = lastMatch.Gaps
            .Where(gap => gap.Kind == MarketFreshnessGapKind.AggregateAhead)
            .Select(gap => gap.WorldName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var originalRepairWorldListingCount = detailed.Listings.Count(listing =>
            repairWorlds.Any(worldName => MarketListingReconciler.MatchesWorld(listing, worldName)));
        var previousGaps = lastMatch.Gaps;
        var repairedPartitions = new Dictionary<string, UniversalisResponse>(StringComparer.OrdinalIgnoreCase);
        var verificationPass = 1;
        while (repairWorlds.Length > 0 && DateTimeOffset.UtcNow < deadline)
        {
            verificationPass++;
            Service.Log.Info(
                $"[Universalis] Repairing {repairWorlds.Length} aggregate-ahead world partition(s) for {targetName}: " +
                string.Join(", ", repairWorlds));
            TraceFetch(
                "detail-targeted-repair-started",
                targetName,
                $"Repairing only: {string.Join(", ", repairWorlds)}.",
                verificationPass: verificationPass);

            var repairTasks = repairWorlds.Select(async worldName =>
            {
                var partitionTask = GetListingDataForTarget(
                    gameItem,
                    worldName,
                    cancellationToken,
                    reportProgress,
                    vocabulary,
                    verificationPass,
                    GetFreshDetailProgress(deadline - DateTimeOffset.UtcNow));
                var probeTask = GetFreshnessProbeForTarget(
                    gameItem,
                    worldName,
                    cancellationToken,
                    reportProgress: null,
                    vocabulary,
                    GetFreshDetailProgress(deadline - DateTimeOffset.UtcNow));
                await Task.WhenAll(partitionTask, probeTask);
                var probeResult = await probeTask;
                return (
                    WorldName: worldName,
                    Partition: await partitionTask,
                    Probe: probeResult.Probe,
                    ProbeFailure: probeResult.Failure);
            });
            var repairs = await Task.WhenAll(repairTasks);
            foreach (var repair in repairs)
            {
                if (repair.Partition.Status != UniversalisResponseStatus.Success)
                    return repair.Partition;
                if (repair.ProbeFailure is not null)
                    return repair.ProbeFailure;
                if (repair.Probe is null)
                {
                    return new UniversalisResponse
                    {
                        Status = UniversalisResponseStatus.InvalidData,
                        FailureDetail = $"Universalis returned no aggregate freshness data for {gameItem.Name} on {repair.WorldName}.",
                    };
                }

                repairedPartitions[repair.WorldName] = repair.Partition;
                var probeIndex = Array.FindIndex(
                    resolvedWorldProbes,
                    probe => string.Equals(
                        probe.TargetName,
                        repair.WorldName,
                        StringComparison.OrdinalIgnoreCase));
                if (probeIndex >= 0)
                    resolvedWorldProbes[probeIndex] = repair.Probe;
                MarketListingReconciler.ReplaceWorldPartition(detailed, repair.WorldName, repair.Partition);
            }

            lastMatch = MarketFreshnessMatcher.CompareScope(
                resolvedWorldProbes,
                detailed,
                P.Config.UniversalisHqOnly,
                P.Config.UniversalisListings);
            if (lastMatch.IsCurrent)
                break;

            conflict = lastMatch.Gaps.FirstOrDefault(gap => gap.Kind == MarketFreshnessGapKind.Conflict);
            if (conflict is not null)
                return CreateStaleResponse(vocabulary, lastMatch.Detail);

            if (!MarketFreshnessRetryPolicy.HasRevisionChange(previousGaps, lastMatch.Gaps))
            {
                TraceFetch(
                    "detail-targeted-repair-unchanged",
                    targetName,
                    "Targeted repair returned the same revision pair; stopping without another scope scan.",
                    verificationPass: verificationPass);
                return CreateStaleResponse(vocabulary, lastMatch.Detail);
            }

            previousGaps = lastMatch.Gaps;
            repairWorlds = lastMatch.Gaps
                .Where(gap => gap.Kind == MarketFreshnessGapKind.AggregateAhead)
                .Select(gap => gap.WorldName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (repairWorlds.Length > 0 && remaining > TimeSpan.Zero)
            {
                var retryDelay = remaining < FreshDetailRetryDelay ? remaining : FreshDetailRetryDelay;
                await Task.Delay(retryDelay, cancellationToken);
            }
        }

        if (!lastMatch.IsCurrent)
            return CreateStaleResponse(vocabulary, lastMatch.Detail);

        if (detailed.ListingPageMayBeTruncated
            && detailed.Listings.Count < P.Config.UniversalisListings
            && originalRepairWorldListingCount > 0)
        {
            var refillLimit = Math.Min(
                UniversalisListingFetchPolicy.MaximumListingRequestLimit,
                P.Config.UniversalisListings + originalRepairWorldListingCount);
            var refill = await GetListingDataForTarget(
                gameItem,
                targetName,
                cancellationToken,
                reportProgress,
                vocabulary,
                verificationPass + 1,
                GetFreshDetailProgress(deadline - DateTimeOffset.UtcNow),
                desiredUniqueListings: refillLimit,
                initialRequestLimit: refillLimit);
            if (refill.Status != UniversalisResponseStatus.Success)
                return refill;

            foreach (var repair in repairedPartitions)
                MarketListingReconciler.ReplaceWorldPartition(refill, repair.Key, repair.Value);
            detailed = refill;
            lastMatch = MarketFreshnessMatcher.CompareScope(
                resolvedWorldProbes,
                detailed,
                P.Config.UniversalisHqOnly,
                P.Config.UniversalisListings);
            if (!lastMatch.IsCurrent)
                return CreateStaleResponse(vocabulary, lastMatch.Detail);
        }

        if (detailed.ListingPageMayBeTruncated
            && detailed.Listings.Count < P.Config.UniversalisListings)
        {
            return CreateStaleResponse(
                vocabulary,
                "Universalis duplicate rows exhausted the bounded listing request before CMB could prove the configured unique-listing count.");
        }

        TraceFetch(
            "detail-verification-accepted",
            targetName,
            $"Accepted after targeted repair of {string.Join(", ", repairedPartitions.Keys)}.",
            verificationPass: verificationPass);
        return MarketListingReconciler.FinalizeVerifiedResponse(
            detailed,
            history,
            P.Config.UniversalisListings);

        async Task<(int Index, string WorldName, MarketFreshnessProbe? Probe, UniversalisResponse? Failure)> FetchWorldProbeAsync(
            int index,
            string worldName)
        {
            var probeProgress = 0.15f + (0.25f * Volatile.Read(ref completedWorldCount) / worldNames.Count);
            var result = await GetFreshnessProbeForTarget(
                gameItem,
                worldName,
                cancellationToken,
                reportProgress,
                vocabulary,
                probeProgress,
                index + 1,
                worldNames.Count);
            var completed = Interlocked.Increment(ref completedWorldCount);
            reportProgress?.Invoke(new UniversalisRequestProgress(
                vocabulary.WorldsVerified(gameItem.Name, completed, worldNames.Count),
                0.15f + (0.25f * completed / worldNames.Count)));
            return (index, worldName, result.Probe, result.Failure);
        }
    }

    private async Task<(MarketFreshnessProbe? Probe, UniversalisResponse? Failure)> GetFreshnessProbeForTarget(
        MarketItem gameItem,
        string targetName,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress,
        MarketRefreshVocabulary vocabulary,
        float requestProgress,
        int worldIndex = 0,
        int worldCount = 0)
    {
        try
        {
            var normalizedTarget = P.MainWindow.ScopeCatalog.NormalizeForUniversalis(targetName);
            var apiUrl = new UriBuilder($"{Host}/api/v2/aggregated/{normalizedTarget}/{gameItem.Id}").Uri.ToString();

            for (var attempt = 1; attempt <= AggregateMaxAttempts; attempt++)
            {
                reportProgress?.Invoke(new UniversalisRequestProgress(
                    worldCount > 0
                        ? vocabulary.VerifyingWorld(
                            gameItem.Name,
                            targetName,
                            worldIndex,
                            worldCount,
                            attempt,
                            AggregateMaxAttempts)
                        : vocabulary.CheckingLatestPrices(
                            gameItem.Name,
                            targetName,
                            attempt,
                            AggregateMaxAttempts),
                    requestProgress));

                try
                {
                    Service.Log.Info($"[Universalis] Aggregate probe attempt {attempt}/{AggregateMaxAttempts}: {apiUrl}");
                    TraceFetch(
                        "aggregate-request-started",
                        targetName,
                        $"Aggregate probe attempt {attempt}/{AggregateMaxAttempts} started.",
                        apiUrl,
                        attempt);
                    var requestStartedAt = Stopwatch.GetTimestamp();
                    using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                    using var response = await SendRequestAsync(
                        request,
                        AggregateRequestTimeout,
                        targetName,
                        requestProgress,
                        reportProgress,
                        cancellationToken);
                    TraceFetch(
                        "aggregate-response-received",
                        targetName,
                        $"Aggregate probe returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                        apiUrl,
                        attempt,
                        statusCode: (int)response.StatusCode,
                        durationMilliseconds: Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds);
                    if (!response.IsSuccessStatusCode)
                    {
                        var failure = CreateServerError(response.StatusCode);
                        if (!IsTransient(response.StatusCode) || attempt == AggregateMaxAttempts)
                        {
                            if (IsTransient(response.StatusCode))
                                StartFailureCooldown();

                            return (null, failure);
                        }

                        var retryDelay = RegisterTransientFailure(
                            GetRetryAfterDelay(response),
                            attempt,
                            AggregateInitialRetryDelay,
                            AggregateMaximumRetryDelay);
                        await WaitForRetryAsync(
                            targetName,
                            response.StatusCode.ToString(),
                            attempt + 1,
                            AggregateMaxAttempts,
                            retryDelay,
                            requestProgress,
                            reportProgress,
                            cancellationToken);
                        continue;
                    }

                    var data = await response.Content.ReadFromJsonAsync<UniversalisAggregateResponse>(
                        cancellationToken: cancellationToken);
                    if (data is null)
                    {
                        return (null, new UniversalisResponse
                        {
                            Status = UniversalisResponseStatus.InvalidData,
                            FailureDetail = "Universalis returned no aggregate market data.",
                        });
                    }

                    var aggregateItem = data.Results.SingleOrDefault(item => item.ItemId == gameItem.Id);
                    if (aggregateItem is null)
                    {
                        return (null, new UniversalisResponse
                        {
                            Status = UniversalisResponseStatus.InvalidData,
                            FailureDetail = $"Universalis aggregate data did not contain item {gameItem.Id}.",
                        });
                    }

                    var probe = BuildFreshnessProbe(targetName, aggregateItem);
                    var latestUploadTime = Math.Max(
                        probe.Nq?.UploadTime ?? 0,
                        probe.Hq?.UploadTime ?? 0);
                    TraceFetch(
                        "aggregate-probe-parsed",
                        targetName,
                        $"Parsed aggregate upload revision {latestUploadTime}.",
                        apiUrl,
                        attempt,
                        statusCode: (int)response.StatusCode);
                    return (probe, null);
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TraceFetch(
                        "aggregate-request-timeout",
                        targetName,
                        $"Aggregate probe timed out after {AggregateRequestTimeout.TotalSeconds:F0}s.",
                        apiUrl,
                        attempt);
                    Service.Log.Warning(
                        $"[Universalis] Aggregate probe for {targetName} timed out on attempt {attempt}/{AggregateMaxAttempts} " +
                        $"after {AggregateRequestTimeout.TotalSeconds:F0}s.");
                    if (attempt == AggregateMaxAttempts)
                    {
                        StartFailureCooldown();
                        return (null, new UniversalisResponse
                        {
                            Status = UniversalisResponseStatus.UserCancellation,
                            FailureDetail = $"Universalis aggregate probe timed out after {AggregateRequestTimeout.TotalSeconds:F0}s.",
                        });
                    }

                    var retryDelay = GetRetryDelay(
                        attempt,
                        AggregateInitialRetryDelay,
                        AggregateMaximumRetryDelay);
                    await WaitForRetryAsync(
                        targetName,
                        "aggregate probe timed out",
                        attempt + 1,
                        AggregateMaxAttempts,
                        retryDelay,
                        requestProgress,
                        reportProgress,
                        cancellationToken);
                }
                catch (HttpRequestException ex)
                {
                    TraceFetch(
                        "aggregate-request-failed",
                        targetName,
                        $"Aggregate probe connection failed: {ex.Message}",
                        apiUrl,
                        attempt);
                    if (attempt == AggregateMaxAttempts)
                    {
                        StartFailureCooldown();
                        Service.Log.Warning(ex, "[Universalis] Aggregate probe connection failed after all retry attempts.");
                        return (null, new UniversalisResponse
                        {
                            Status = UniversalisResponseStatus.ServerError,
                            FailureDetail = "Unable to connect to Universalis for the current minimum.",
                        });
                    }

                    var retryDelay = GetRetryDelay(
                        attempt,
                        AggregateInitialRetryDelay,
                        AggregateMaximumRetryDelay);
                    await WaitForRetryAsync(
                        targetName,
                        "aggregate probe connection failed",
                        attempt + 1,
                        AggregateMaxAttempts,
                        retryDelay,
                        requestProgress,
                        reportProgress,
                        cancellationToken);
                }
                catch (JsonException ex)
                {
                    TraceFetch(
                        "aggregate-response-invalid",
                        targetName,
                        $"Aggregate response JSON was invalid: {ex.Message}",
                        apiUrl,
                        attempt);
                    Service.Log.Warning(ex, "[Universalis] Aggregate probe JSON parse failed.");
                    return (null, new UniversalisResponse
                    {
                        Status = UniversalisResponseStatus.InvalidData,
                        FailureDetail = "Universalis returned invalid aggregate market data.",
                    });
                }
            }

            throw new InvalidOperationException("Universalis aggregate retry loop exited without a result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"[Universalis] Aggregate probe error: {ex.Message}");
            return (null, new UniversalisResponse
            {
                Status = UniversalisResponseStatus.UnknownError,
                FailureDetail = "Unexpected Universalis aggregate probe error.",
            });
        }
    }

    private static MarketFreshnessProbe BuildFreshnessProbe(
        string targetName,
        UniversalisAggregateItem aggregateItem)
    {
        if (!P.MainWindow.ScopeCatalog.TryGetScope(targetName, out var targetScope))
            throw new InvalidOperationException($"Unknown market scope '{targetName}'.");

        var nq = BuildMinimumProbe(false, targetScope, aggregateItem.Nq.MinListing, aggregateItem.WorldUploadTimes);
        var hq = BuildMinimumProbe(true, targetScope, aggregateItem.Hq.MinListing, aggregateItem.WorldUploadTimes);
        var worldUploadTime = targetScope.WorldId is { } worldId
            ? aggregateItem.WorldUploadTimes.FirstOrDefault(item => item.WorldId == worldId)?.Timestamp ?? 0
            : 0;
        return new MarketFreshnessProbe(
            targetName,
            nq,
            hq,
            Math.Max(worldUploadTime, Math.Max(nq?.UploadTime ?? 0, hq?.UploadTime ?? 0)));
    }

    private static MarketMinimumProbe? BuildMinimumProbe(
        bool hq,
        MarketScopeOption targetScope,
        UniversalisAggregateMinimums minimums,
        IReadOnlyCollection<UniversalisAggregateWorldUploadTime> uploadTimes)
    {
        var minimum = targetScope.Kind switch
        {
            MarketScopeKind.World => minimums.World,
            MarketScopeKind.DataCenter => minimums.DataCenter,
            MarketScopeKind.Region => minimums.Region,
            _ => null,
        };
        if (minimum is not { Price: > 0 })
            return null;

        var worldId = minimum.WorldId ?? targetScope.WorldId;
        var world = P.MainWindow.ScopeCatalog.AllScopes.FirstOrDefault(scope => scope.WorldId == worldId);
        if (world is null)
            throw new InvalidOperationException($"Universalis returned unknown world ID {worldId} for {targetScope.Name}.");

        var uploadTime = uploadTimes.FirstOrDefault(item => item.WorldId == worldId)?.Timestamp ?? 0;
        return new MarketMinimumProbe(hq, minimum.Price, worldId, world.Name, uploadTime);
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

    private async Task<UniversalisResponse> GetListingDataForTarget(
        MarketItem gameItem,
        string targetName,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress,
        MarketRefreshVocabulary vocabulary,
        int verificationPass,
        float requestProgress,
        int? desiredUniqueListings = null,
        int? initialRequestLimit = null)
    {
        var desired = Math.Clamp(
            desiredUniqueListings ?? P.Config.UniversalisListings,
            1,
            UniversalisListingFetchPolicy.MaximumListingRequestLimit);
        var requestLimit = Math.Clamp(
            initialRequestLimit ?? desired,
            1,
            UniversalisListingFetchPolicy.MaximumListingRequestLimit);
        var adaptivePass = 0;
        while (true)
        {
            adaptivePass++;
            var response = await GetDataForTarget(
                gameItem,
                targetName,
                cancellationToken,
                reportProgress,
                vocabulary,
                verificationPass + adaptivePass - 1,
                requestProgress,
                listingLimitOverride: requestLimit,
                entryLimitOverride: 0);
            if (response.Status != UniversalisResponseStatus.Success)
                return response;

            if (response.ConflictingListingIdentities.Count > 0)
            {
                return new UniversalisResponse
                {
                    Status = UniversalisResponseStatus.InvalidData,
                    FailureDetail =
                        $"Universalis returned conflicting values for {response.ConflictingListingIdentities.Count} listing identity or identities on {targetName}.",
                };
            }

            var nextLimit = UniversalisListingFetchPolicy.GetNextRequestLimit(
                desired,
                requestLimit,
                response.RawListingCount,
                response.Listings.Count);
            if (nextLimit is null)
                return response;

            TraceFetch(
                "detail-adaptive-overfetch",
                targetName,
                $"Expanded listing request from {requestLimit} to {nextLimit.Value} after {response.RawListingCount} rows produced {response.Listings.Count} unique identities.",
                verificationPass: verificationPass + adaptivePass);
            requestLimit = nextLimit.Value;
        }
    }

    private static UniversalisResponse CreateStaleResponse(
        MarketRefreshVocabulary vocabulary,
        string detail)
        => new()
        {
            Status = UniversalisResponseStatus.StaleData,
            FailureDetail = vocabulary.StaleFailure(FreshDetailTimeout, detail),
        };

    private async Task<UniversalisResponse> GetDataForTarget(
        MarketItem gameItem,
        string targetName,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress,
        MarketRefreshVocabulary vocabulary,
        int verificationPass = 0,
        float requestProgress = 0.35f,
        bool? highQualityOnly = null,
        int? listingLimitOverride = null,
        int? entryLimitOverride = null)
    {
        try
        {
            var listingLimit = listingLimitOverride ?? P.Config.UniversalisListings;
            var entryLimit = entryLimitOverride ?? P.Config.UniversalisEntries;
            var _hq = (highQualityOnly ?? P.Config.UniversalisHqOnly) ? "&hq=1" : "";
            var cacheBypass = verificationPass > 0
                ? $"&_cmbRefresh={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{verificationPass}"
                : "";
            var targetRegion = P.MainWindow.ScopeCatalog.NormalizeForUniversalis(targetName);
            var API_URL = new UriBuilder($"{Host}/api/v2/{targetRegion}/{gameItem.Id}?listings={listingLimit}&entries={entryLimit}{_hq}{cacheBypass}").Uri.ToString();
            var requestTimeout = verificationPass > 0
                ? FreshListingRequestTimeout
                : TimeSpan.FromSeconds(P.Config.RequestTimeout);
            var initialRetryDelay = verificationPass > 0
                ? FreshListingInitialRetryDelay
                : InitialRetryDelay;
            var maximumRetryDelay = verificationPass > 0
                ? FreshListingMaximumRetryDelay
                : MaximumRetryDelay;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                reportProgress?.Invoke(new UniversalisRequestProgress(
                    vocabulary.DownloadingListings(
                        gameItem.Name,
                        targetName,
                        attempt,
                        MaxAttempts,
                        verificationPass),
                    requestProgress));

                try
                {
                    Service.Log.Info($"[Universalis] Fetch attempt {attempt}/{MaxAttempts}: {API_URL}");
                    TraceFetch(
                        "detail-request-started",
                        targetName,
                        $"Detailed listing attempt {attempt}/{MaxAttempts} started.",
                        API_URL,
                        attempt,
                        verificationPass);
                    var requestStartedAtUtc = DateTimeOffset.UtcNow;
                    var requestStartedAt = Stopwatch.GetTimestamp();
                    using var request = new HttpRequestMessage(HttpMethod.Get, API_URL);
                    if (verificationPass > 0)
                    {
                        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                        {
                            NoCache = true,
                            NoStore = true,
                        };
                    }

                    using var response = await SendRequestAsync(
                        request,
                        requestTimeout,
                        targetName,
                        requestProgress,
                        reportProgress,
                        cancellationToken);
                    var requestDuration = Stopwatch.GetElapsedTime(requestStartedAt).TotalMilliseconds;
                    TraceFetch(
                        "detail-response-received",
                        targetName,
                        $"Detailed listing request returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                        API_URL,
                        attempt,
                        verificationPass,
                        (int)response.StatusCode,
                        requestDuration);
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

                        var retryDelay = RegisterTransientFailure(
                            GetRetryAfterDelay(response),
                            attempt,
                            initialRetryDelay,
                            maximumRetryDelay);
                        await WaitForRetryAsync(
                            targetName,
                            response.StatusCode.ToString(),
                            attempt + 1,
                            MaxAttempts,
                            retryDelay,
                            requestProgress,
                            reportProgress,
                            cancellationToken);
                        continue;
                    }

                    var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    using var rawPayload = JsonDocument.Parse(payload);
                    var data = rawPayload.RootElement.Deserialize<MarketDataCurrent>(UniversalisJsonOptions);
                    if (data is null)
                    {
                        Service.Log.Warning($"[Universalis] Parse JSON failed");
                        return new UniversalisResponse { Status = UniversalisResponseStatus.InvalidData, FailureDetail = "Universalis returned no market data." };
                    }

                    var interpretedResponse = BuildSuccessResponse(
                        data,
                        gameItem,
                        targetName,
                        listingLimit);
                    try
                    {
                        activeDiagnosticSession.Value?.CaptureDetailedResponse(
                            targetName,
                            API_URL,
                            attempt,
                            verificationPass,
                            requestStartedAtUtc,
                            requestDuration,
                            (int)response.StatusCode,
                            new UniversalisRequestHeaders(
                                request.Version,
                                request.Headers.ConnectionClose == true,
                                request.Headers.CacheControl?.ToString()),
                            CaptureResponseHeaders(response),
                            UniversalisFetchDiagnosticSession.ComputeSha256(payload),
                            rawPayload.RootElement.Clone(),
                            data,
                            interpretedResponse);
                    }
                    catch (Exception ex)
                    {
                        Service.Log.Error(ex, "[Universalis] Failed to capture duplicate-listing diagnostic evidence.");
                    }
                    if (interpretedResponse.RawListingCount != interpretedResponse.Listings.Count)
                    {
                        TraceFetch(
                            "duplicate-listings-detected",
                            targetName,
                            $"Universalis returned {interpretedResponse.RawListingCount} rows representing {interpretedResponse.Listings.Count} unique listing identities.",
                            API_URL,
                            attempt,
                            verificationPass,
                            (int)response.StatusCode,
                            requestDuration);
                    }

                    if (interpretedResponse.ConflictingListingIdentities.Count > 0)
                    {
                        return new UniversalisResponse
                        {
                            Status = UniversalisResponseStatus.InvalidData,
                            FailureDetail =
                                $"Universalis returned conflicting values for {interpretedResponse.ConflictingListingIdentities.Count} listing identity or identities on {targetName}.",
                        };
                    }

                    return interpretedResponse;
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TraceFetch(
                        "detail-request-timeout",
                        targetName,
                        $"Detailed listing request timed out after {requestTimeout.TotalSeconds:F0}s.",
                        API_URL,
                        attempt,
                        verificationPass);
                    if (attempt == MaxAttempts)
                    {
                        StartFailureCooldown();
                        Service.Log.Warning($"[Universalis] Request timed out after {requestTimeout.TotalSeconds:F0}s.");
                        return new UniversalisResponse
                        {
                            Status = UniversalisResponseStatus.UserCancellation,
                            FailureDetail = $"Universalis timed out after {requestTimeout.TotalSeconds:F0}s.",
                        };
                    }

                    var retryDelay = GetRetryDelay(
                        attempt,
                        initialRetryDelay,
                        maximumRetryDelay);
                    await WaitForRetryAsync(
                        targetName,
                        "timed out",
                        attempt + 1,
                        MaxAttempts,
                        retryDelay,
                        requestProgress,
                        reportProgress,
                        cancellationToken);
                }
                catch (HttpRequestException ex)
                {
                    TraceFetch(
                        "detail-request-failed",
                        targetName,
                        $"Detailed listing request connection failed: {ex.Message}",
                        API_URL,
                        attempt,
                        verificationPass);
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

                    var retryDelay = GetRetryDelay(
                        attempt,
                        initialRetryDelay,
                        maximumRetryDelay);
                    await WaitForRetryAsync(
                        targetName,
                        "connection failed",
                        attempt + 1,
                        MaxAttempts,
                        retryDelay,
                        requestProgress,
                        reportProgress,
                        cancellationToken);
                }
                catch (JsonException ex)
                {
                    TraceFetch(
                        "detail-response-invalid",
                        targetName,
                        $"Detailed response JSON was invalid: {ex.Message}",
                        API_URL,
                        attempt,
                        verificationPass);
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
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        string targetName,
        float progress,
        Action<UniversalisRequestProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            await WaitForSharedCooldownAsync(
                targetName,
                progress,
                reportProgress,
                cancellationToken);
            await WaitForRequestStartSlotAsync(cancellationToken);

            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(timeout);
            return await httpClient.SendAsync(request, requestTimeout.Token);
        }
        finally
        {
            requestGate.Release();
        }
    }

    private async Task WaitForRequestStartSlotAsync(CancellationToken cancellationToken)
    {
        await requestStartGate.WaitAsync(cancellationToken);
        try
        {
            var delay = nextRequestStartAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            nextRequestStartAt = DateTimeOffset.UtcNow + MinimumRequestStartInterval;
        }
        finally
        {
            requestStartGate.Release();
        }
    }

    private async Task WaitForSharedCooldownAsync(
        string targetName,
        float progress,
        Action<UniversalisRequestProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset allowedAt;
        lock (retryStateLock)
            allowedAt = nextRequestAllowedAt;

        var cooldownDelay = allowedAt - DateTimeOffset.UtcNow;
        if (cooldownDelay > TimeSpan.Zero)
        {
            TraceFetch(
                "shared-cooldown-wait",
                targetName,
                $"Waiting {FormatDelay(cooldownDelay)} for the shared Universalis recovery cooldown.");
        }

        await WaitWithProgressAsync(
            cooldownDelay,
            remaining => $"Universalis recovery cooldown for {targetName}: waiting {FormatDelay(remaining)}",
            progress,
            reportProgress,
            cancellationToken);
    }

    private async Task WaitForRetryAsync(
        string targetName,
        string reason,
        int nextAttempt,
        int maximumAttempts,
        TimeSpan retryDelay,
        float progress,
        Action<UniversalisRequestProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        TraceFetch(
            "retry-wait",
            targetName,
            $"{reason}; waiting {FormatDelay(retryDelay)} before attempt {nextAttempt}/{maximumAttempts}.",
            attempt: nextAttempt);
        await WaitWithProgressAsync(
            retryDelay,
            remaining => $"Universalis {reason} for {targetName}; retrying attempt {nextAttempt}/{maximumAttempts} in {FormatDelay(remaining)}",
            progress,
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
            var delayMilliseconds = Math.Max(1, Math.Min(250, (int)Math.Ceiling(remaining.TotalMilliseconds)));
            await Task.Delay(delayMilliseconds, cancellationToken);
        }
    }

    private TimeSpan RegisterTransientFailure(
        TimeSpan? retryAfter,
        int attempt,
        TimeSpan initialDelay,
        TimeSpan maximumDelay)
    {
        var retryDelay = GetRetryDelay(attempt, initialDelay, maximumDelay);
        if (retryAfter is { } requestedDelay && requestedDelay > retryDelay)
            retryDelay = requestedDelay;

        lock (retryStateLock)
        {
            var retryAt = DateTimeOffset.UtcNow + retryDelay;
            if (retryAt > nextRequestAllowedAt)
                nextRequestAllowedAt = retryAt;
        }

        return retryDelay;
    }

    private static TimeSpan GetRetryDelay(
        int attempt,
        TimeSpan initialDelay,
        TimeSpan maximumDelay)
        => TimeSpan.FromMilliseconds(Math.Min(
            maximumDelay.TotalMilliseconds,
            initialDelay.TotalMilliseconds * Math.Pow(3, Math.Max(0, attempt - 1))));

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

    private static UniversalisResponseHeaders CaptureResponseHeaders(HttpResponseMessage response)
    {
        static string? FirstHeader(HttpResponseMessage source, string name) =>
            source.Headers.TryGetValues(name, out var values)
                ? values.FirstOrDefault()
                : null;

        return new UniversalisResponseHeaders(
            response.Headers.Date,
            response.Headers.Age?.TotalSeconds,
            response.Headers.CacheControl?.ToString(),
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified,
            FirstHeader(response, "CF-Cache-Status"),
            FirstHeader(response, "CF-Ray"),
            response.Headers.Server.Count > 0
                ? string.Join(" ", response.Headers.Server)
                : null);
    }

    private static string FormatDelay(TimeSpan delay)
        => $"{Math.Max(0, delay.TotalSeconds):F1}s";

    private static float GetFreshDetailProgress(TimeSpan remaining)
    {
        var elapsedFraction = 1.0 - Math.Clamp(
            remaining.TotalMilliseconds / FreshDetailTimeout.TotalMilliseconds,
            0.0,
            1.0);
        return 0.45f + (float)(0.40 * elapsedFraction);
    }

    private static UniversalisResponse BuildSuccessResponse(
        MarketDataCurrent data,
        MarketItem gameItem,
        string targetName,
        int listingRequestLimit)
    {
        var fetchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rawListingCount = data.Listings.Count;
        var rawListingCutoffPrice = data.Listings.Count > 0
            ? data.Listings.Max(listing => listing.PricePerUnit)
            : (long?)null;
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

        var normalization = MarketListingNormalizer.Analyze(data.Listings);
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
            RawListingCount = rawListingCount,
            RawListingCutoffPrice = rawListingCutoffPrice,
            ListingRequestLimit = listingRequestLimit,
            ListingPageMayBeTruncated = listingRequestLimit > 0 && rawListingCount >= listingRequestLimit,
            ConflictingListingIdentities = normalization.Conflicts,
            Listings = normalization.Listings.ToList(),
            Entries = data.Entries,
            ScopeName = targetName,
        };
        Service.Log.Debug(
            $"[Universalis] Response {gameItem.Id} for {targetName}: " +
            $"{universalisResponse.Listings.Count} unique listings from {universalisResponse.RawListingCount} rows, " +
            $"{universalisResponse.Entries.Count} sales, " +
            $"latest upload {universalisResponse.LatestUploadTime}.");

        return universalisResponse;
    }
}

public sealed record UniversalisRequestProgress(string StatusText, float Progress);
