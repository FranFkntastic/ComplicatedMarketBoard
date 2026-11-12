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
    private readonly AsyncLocal<Action<UniversalisResponse>?> publishDetails = new();
    private readonly AsyncLocal<System.Collections.Concurrent.ConcurrentDictionary<MarketListingIdentity, byte>?> refusedIdentities = new();
    private readonly AsyncLocal<bool> backgroundReconciliation = new();
    private readonly AsyncLocal<Task?> detailAdmission = new();

    private UniversalisResponse BrowsingSnapshot(UniversalisResponse response)
    {
        var snapshot = response.Snapshot();
        if (refusedIdentities.Value is { } refused)
        {
            foreach (var row in snapshot.Listings)
                if (MarketListingNormalizer.TryGetIdentity(row, out var id) && refused.ContainsKey(id))
                    snapshot.PendingWorlds.Add(row.WorldName);
            foreach (var identity in refused.Keys)
            {
                var worldName = identity.World.StartsWith("name:", StringComparison.Ordinal)
                    ? identity.World[5..]
                    : uint.TryParse(identity.World.AsSpan(3), out var worldId)
                        ? Data.WorldSheet.FirstOrDefault(world => world.RowId == worldId).Name.ToString() : "";
                if (!string.IsNullOrEmpty(worldName)) snapshot.PendingWorlds.Add(worldName);
            }
            snapshot.ConflictingListingIdentities = snapshot.ConflictingListingIdentities.Concat(refused.Keys).Distinct().ToArray();
            snapshot.Listings = snapshot.Listings.Where(row => !MarketListingNormalizer.TryGetIdentity(row, out var id)
                || !refused.ContainsKey(id)).ToList();
            snapshot.UnitsForSale = snapshot.Listings.Sum(row => row.Quantity);
        }
        return snapshot;
    }
    private readonly AsyncLocal<(int Listings, int Entries, bool Hq)?> fetchSettings = new();
    private int ListingLimit => fetchSettings.Value?.Listings ?? P.Config.UniversalisListings;
    private int EntryLimit => fetchSettings.Value?.Entries ?? P.Config.UniversalisEntries;
    private bool HqOnly => fetchSettings.Value?.Hq ?? P.Config.UniversalisHqOnly;
    private const int MaxAttempts = 3;
    private const int AggregateMaxAttempts = 4;
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
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);
    private readonly UniversalisRequestBudget requestBudget;
    private readonly UniversalisRequestPacer requestPacer;
    private readonly object retryStateLock = new();
    private readonly AsyncLocal<UniversalisFetchDiagnosticSession?> activeDiagnosticSession = new();
    private DateTimeOffset nextRequestAllowedAt = DateTimeOffset.MinValue;

    public UniversalisClient()
    {
        requestPacer = new UniversalisRequestPacer(
            () => P.Config.UniversalisRequestSpacingMilliseconds,
            () => { lock (retryStateLock) return nextRequestAllowedAt; });
        requestBudget = new UniversalisRequestBudget(
            () => P.Config.UniversalisMaxConcurrentRequests, requestPacer.Dispose);
        httpClient = CreateHttpClient();
    }

    public void Dispose()
    {
        httpClient.Dispose();
        requestBudget.Dispose();
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
        MarketRefreshVocabulary? vocabulary = null,
        bool reconcileProvisionalOnly = false,
        Action<UniversalisResponse>? onDetails = null)
    {
        publishDetails.Value = onDetails;
        backgroundReconciliation.Value = reconcileProvisionalOnly;
        refusedIdentities.Value = new();
        fetchSettings.Value = (P.Config.UniversalisListings, P.Config.UniversalisEntries, P.Config.UniversalisHqOnly);
        detailAdmission.Value = null;
        return await RunWithDiagnosticsAsync(
            gameItem,
            gameItem.TargetRegion,
            requireCurrentDetails,
            () => GetData(
                gameItem,
                cancellationToken,
                reportProgress,
                requireCurrentDetails,
                vocabulary ?? MarketRefreshVocabulary.Standard,
                reconcileProvisionalOnly));
    }

    public async Task<UniversalisResponse> GetDataForTargetAsync(
        MarketItem gameItem,
        string targetName,
        bool highQualityOnly,
        CancellationToken cancellationToken)
    {
        var response = await RunWithDiagnosticsAsync(
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

        return MarketListingCoveragePolicy.Classify(
            response,
            ListingLimit);
    }

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
            ListingLimit,
            EntryLimit,
            highQualityOnly ?? HqOnly);
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
        MarketRefreshVocabulary vocabulary,
        bool reconcileProvisionalOnly)
    {
        if (reconcileProvisionalOnly && !gameItem.UniversalisResponse.NeedsListingReconciliation)
            return await RefreshHistoryAsync(gameItem, cancellationToken, vocabulary);
        var worldProbeCache = reconcileProvisionalOnly
            ? MarketProvisionalListingPolicy.ReconciliationFences(gameItem.UniversalisResponse, gameItem.TargetRegion)
            : new Dictionary<string, MarketFreshnessProbe>(StringComparer.OrdinalIgnoreCase);
        var selectedCustom = P.Config.CustomMarketScopes.FirstOrDefault(scope => scope.Id == P.Config.selectedCustomScopeId);
        var customScope = selectedCustom is null ? null : new CustomMarketScope
        {
            Id = selectedCustom.Id, Name = selectedCustom.Name, IncludedScopes = selectedCustom.IncludedScopes.ToList(),
        };
        UniversalisResponse response;
        if (customScope is not null)
        {
            var previousResponse = MarketWorldPartitionPolicy.SelectPreviousVerifiedResponse(
                gameItem.UniversalisResponse,
                customScope.Name);
            response = await GetCustomScopeData(
                gameItem,
                customScope,
                cancellationToken,
                reportProgress,
                requireCurrentDetails,
                worldProbeCache,
                vocabulary,
                previousResponse);
        }
        else
        {
            var previousTargetResponse = MarketWorldPartitionPolicy.SelectPreviousVerifiedResponse(
                gameItem.UniversalisResponse,
                gameItem.TargetRegion);

            response = requireCurrentDetails
                ? await GetCurrentDataForTarget(
                    gameItem,
                    gameItem.TargetRegion,
                    cancellationToken,
                    reportProgress,
                    worldProbeCache,
                    vocabulary,
                    previousTargetResponse)
                : await GetDataForTarget(
                    gameItem,
                    gameItem.TargetRegion,
                    cancellationToken,
                    reportProgress,
                    vocabulary);
        }

        if (reconcileProvisionalOnly && response.Status == UniversalisResponseStatus.Success)
            response = AssembleBackgroundPartitions(response, gameItem.UniversalisResponse);
        else if (reconcileProvisionalOnly && refusedIdentities.Value is { IsEmpty: false })
        {
            // A transport/verification failure cannot keep a newly refused old row
            // displayable. This is a safety correction, not a successful fetch.
            var retained = BrowsingSnapshot(gameItem.UniversalisResponse);
            if (retained.PendingWorlds.Except(gameItem.UniversalisResponse.PendingWorlds,
                    StringComparer.OrdinalIgnoreCase).Any()
                || retained.Listings.Count != gameItem.UniversalisResponse.Listings.Count)
                response = retained;
        }
        return MarketListingCoveragePolicy.Classify(
            response,
            ListingLimit,
            response.ListingCoverage == MarketListingCoverageStatus.DuplicateLimited);
    }

    private static void PreserveBackgroundHistory(UniversalisResponse current, UniversalisResponse? previous)
    {
        if (previous is null || previous.HistoryPending
            || (!current.HistoryPending && current.HistoryFailure.Length == 0)) return;
        // Keep the failure/pending flags from this attempt, not the old success claim.
        current.Entries = previous.Entries.ToList();
        current.AveragePrice = previous.AveragePrice;
        current.AveragePriceNq = previous.AveragePriceNq;
        current.AveragePriceHq = previous.AveragePriceHq;
        current.Velocity = previous.Velocity;
        current.VelocityNq = previous.VelocityNq;
        current.VelocityHq = previous.VelocityHq;
        current.HistoryListingWeights = new(previous.HistoryListingWeights, StringComparer.OrdinalIgnoreCase);
        if (current.HistoryFailure.Length > 0) current.HistoryPending = false;
    }

    private UniversalisResponse AssembleBackgroundPartitions(UniversalisResponse response, UniversalisResponse previous)
    {
        var current = BrowsingSnapshot(response);
        var worlds = current.VerificationProbes.Select(probe => probe.TargetName)
            .Concat(current.WorldUploadTimes.Keys).Concat(current.PendingWorlds)
            .Concat(current.DeferredWorlds.Keys).Concat(current.ProvisionalWorlds.Keys)
            .Concat(current.WorldsWithoutListingData).Concat(current.Listings.Select(row => row.WorldName))
            .Concat(previous.WorldUploadTimes.Keys).Concat(previous.PendingWorlds)
            .Concat(previous.DeferredWorlds.Keys).Concat(previous.ProvisionalWorlds.Keys)
            .Concat(previous.WorldsWithoutListingData).Concat(previous.Listings.Select(row => row.WorldName))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retained = false;
        var retryRetained = false;
        foreach (var world in worlds)
        {
            var wasPending = previous.PendingWorlds.Contains(world) || previous.DeferredWorlds.ContainsKey(world)
                || previous.WorldsWithoutListingData.Contains(world);
            var wasProvisional = previous.ProvisionalWorlds.TryGetValue(world, out var oldProvisional);
            var isProvisional = current.ProvisionalWorlds.TryGetValue(world, out var newProvisional);
            var hasCurrentEvidence = current.VerificationProbes.Any(probe => string.Equals(
                probe.TargetName, world, StringComparison.OrdinalIgnoreCase));
            var regresses = previous.WorldUploadTimes.TryGetValue(world, out var oldUpload)
                && (!current.WorldUploadTimes.TryGetValue(world, out var newUpload) || newUpload < oldUpload);
            if (hasCurrentEvidence && !current.PendingWorlds.Contains(world)
                && !current.DeferredWorlds.ContainsKey(world)
                && (!current.WorldsWithoutListingData.Contains(world)
                    || (!wasProvisional && !previous.Listings.Any(row => MarketListingReconciler.MatchesWorld(row, world))))
                && !regresses
                && (!isProvisional || wasPending || (wasProvisional
                    && newProvisional!.AggregateUploadTime > oldProvisional!.AggregateUploadTime)))
            {
                accepted.Add(world);
                continue;
            }

            // Retention is display continuity, not verification. Preserve the exact
            // old qualification (including expired eligibility) and its timestamps.
            retained = true;
            retryRetained |= current.PendingWorlds.Contains(world) || previous.PendingWorlds.Contains(world)
                || wasProvisional || regresses
                || (current.DeferredWorlds.ContainsKey(world) && !previous.DeferredWorlds.ContainsKey(world));
            MarketListingReconciler.RemoveWorldPartition(current, world);
            current.PendingWorlds.Remove(world);
            current.DeferredWorlds.Remove(world);
            var partition = new UniversalisResponse
            {
                Listings = previous.Listings.Where(row => MarketListingReconciler.MatchesWorld(row, world)).ToList(),
            };
            if (previous.WorldUploadTimes.TryGetValue(world, out var upload)) partition.WorldUploadTimes[world] = upload;
            if (previous.WorldOutOfDate.TryGetValue(world, out var age)) partition.WorldOutOfDate[world] = age;
            MarketListingReconciler.ReplaceWorldPartition(current, world, partition);
            if (previous.PendingWorlds.Contains(world)) current.PendingWorlds.Add(world);
            if (wasProvisional) current.ProvisionalWorlds[world] = oldProvisional!;
            if (previous.DeferredWorlds.TryGetValue(world, out var deferred)) current.DeferredWorlds[world] = deferred;
            if (previous.WorldsWithoutListingData.Contains(world)) current.WorldsWithoutListingData.Add(world);
            if (!previous.WorldUploadTimes.ContainsKey(world) && !wasProvisional
                && !previous.DeferredWorlds.ContainsKey(world)) current.PendingWorlds.Add(world);
        }
        current.VerificationProbes = current.VerificationProbes.Where(probe => accepted.Contains(probe.TargetName))
            .Concat(previous.VerificationProbes.Where(probe => !accepted.Contains(probe.TargetName))).ToArray();
        if (retained)
        {
            current.FetchTime = previous.FetchTime;
            // Even when all old rows were verified, a rejected replacement still
            // needs a listing retry, not just a history-only retry.
            current.ListingRefillPending |= retryRetained;
        }
        PreserveBackgroundHistory(current, previous);
        // Refused identities apply even to older retained rows and make their worlds
        // uncertain, so neither continuity nor the next attempt can verify through them.
        current = BrowsingSnapshot(current);
        var safetyProgress = current.PendingWorlds.Except(previous.PendingWorlds, StringComparer.OrdinalIgnoreCase).Any()
            || previous.Listings.Any(row => MarketListingNormalizer.TryGetIdentity(row, out var id)
                && current.ConflictingListingIdentities.Contains(id));
        var historyProgress = (previous.HistoryPending || previous.HistoryFailure.Length > 0)
            && !current.HistoryPending && current.HistoryFailure.Length == 0;
        if (accepted.Count == 0 && !safetyProgress && !historyProgress)
            return new UniversalisResponse { Status = UniversalisResponseStatus.StaleData,
                FailureDetail = "Corroboration did not improve the retained qualified snapshot." };
        current.Listings = current.Listings.OrderBy(row => row.PricePerUnit).ThenBy(row => row.Quantity)
            .Take(ListingLimit).ToList();
        current.UnitsForSale = current.Listings.Sum(row => row.Quantity);
        return current.Snapshot();
    }

    private async Task<UniversalisResponse> RefreshHistoryAsync(
        MarketItem gameItem, CancellationToken cancellationToken, MarketRefreshVocabulary vocabulary)
    {
        var custom = P.Config.CustomMarketScopes.FirstOrDefault(scope => scope.Id == P.Config.selectedCustomScopeId);
        var targets = custom is null ? new List<string> { gameItem.TargetRegion }
            : P.MainWindow.ScopeCatalog.BuildQueryTargets(custom.IncludedScopes, P.MainWindow.GetCurrentWorldScopeName());
        var histories = await Task.WhenAll(targets.Select(target => GetDataForTarget(
            gameItem, target, cancellationToken, null, vocabulary, listingLimitOverride: 0, entryLimitOverride: EntryLimit)));
        var failure = histories.FirstOrDefault(history => history.Status != UniversalisResponseStatus.Success);
        if (failure is not null) return failure;
        if (histories.Length == 0) return new UniversalisResponse { Status = UniversalisResponseStatus.InvalidData };
        var history = custom is null ? histories[0] : MergeCustomScopeResponses(gameItem, custom, histories.ToList(),
            gameItem.UniversalisResponse.HistoryListingWeights);
        return MarketListingReconciler.FinalizeVerifiedResponse(gameItem.UniversalisResponse.Snapshot(), history, ListingLimit);
    }

    public TimeSpan ReconciliationAttemptTimeout(MarketItem item)
    {
        var custom = P.Config.CustomMarketScopes.FirstOrDefault(scope => scope.Id == P.Config.selectedCustomScopeId);
        var scopes = custom is null ? new List<string> { item.TargetRegion } : custom.IncludedScopes;
        var worlds = P.MainWindow.ScopeCatalog.ExpandToWorldNames(scopes, P.MainWindow.GetCurrentWorldScopeName()).Count;
        // Include queue admission, aggregate retries, detail/history retries, repair and cooldown.
        // Cadence is intentionally independent of this upper bound (including 1000ms pacing).
        return TimeSpan.FromMilliseconds(Math.Max(120_000,
            worlds * AggregateMaxAttempts * (requestPacer.SpacingMilliseconds + AggregateRequestTimeout.TotalMilliseconds)
            + scopes.Count * 2 * MaxAttempts * (requestPacer.SpacingMilliseconds
                + Math.Max(FreshListingRequestTimeout.TotalMilliseconds, P.Config.RequestTimeout * 1000.0)
                + MaximumRetryDelay.TotalMilliseconds)
            + 2 * FailureCooldown.TotalMilliseconds + FreshDetailTimeout.TotalMilliseconds));
    }

    private async Task<UniversalisResponse> GetCustomScopeData(
        MarketItem gameItem,
        CustomMarketScope customScope,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress,
        bool requireCurrentDetails,
        Dictionary<string, MarketFreshnessProbe> worldProbeCache,
        MarketRefreshVocabulary vocabulary,
        UniversalisResponse? previousVerifiedResponse)
    {
        var targets = P.MainWindow.ScopeCatalog.BuildQueryTargets(customScope.IncludedScopes, P.MainWindow.GetCurrentWorldScopeName());
        if (targets.Count == 0)
            return new UniversalisResponse { Status = UniversalisResponseStatus.InvalidData };

        var responses = new Dictionary<string, UniversalisResponse>(StringComparer.OrdinalIgnoreCase);
        var publish = publishDetails.Value;
        var sync = new object();
        // Every target starts its detail/history downloads before any target admits probes.
        var admitted = 0;
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        detailAdmission.Value = admission.Task;
        void PublishCustom()
        {
            if (responses.Count == 0) return;
            var merged = MergeCustomScopeResponses(gameItem, customScope, responses.Values.ToList());
            foreach (var missing in targets.Where(target => !responses.ContainsKey(target)))
                merged.PendingWorlds.UnionWith(P.MainWindow.ScopeCatalog.ExpandToWorldNames(
                    [missing], P.MainWindow.GetCurrentWorldScopeName()));
            merged.HistoryPending |= responses.Count < targets.Count;
            publish?.Invoke(BrowsingSnapshot(merged));
        }
        var tasks = targets.Select(async target =>
        {
            publishDetails.Value = partial =>
            {
                lock (sync)
                {
                    responses[target] = partial;
                    PublishCustom();
                }
            };
            var response = requireCurrentDetails
                ? await GetCurrentDataForTarget(gameItem, target, cancellationToken, reportProgress,
                    new Dictionary<string, MarketFreshnessProbe>(worldProbeCache, StringComparer.OrdinalIgnoreCase),
                    vocabulary, previousVerifiedResponse, () =>
                    {
                        if (Interlocked.Increment(ref admitted) == targets.Count) admission.TrySetResult();
                    })
                : await GetDataForTarget(gameItem, target, cancellationToken, reportProgress, vocabulary);
            lock (sync)
            {
                if (response.Status == UniversalisResponseStatus.Success)
                    responses[target] = response;
                PublishCustom();
            }
            return response;
        }).ToArray();
        var completed = await Task.WhenAll(tasks);
        detailAdmission.Value = null;
        lock (sync)
        {
            // Early detail is useful for initial browsing, but a failed target's partial
            // publication is not a successful background replacement partition.
            if (backgroundReconciliation.Value)
                for (var i = 0; i < targets.Count; i++)
                    if (completed[i].Status != UniversalisResponseStatus.Success)
                        responses.Remove(targets[i]);
            if (responses.Count == 0) return completed[0];
            var merged = MergeCustomScopeResponses(gameItem, customScope, responses.Values.ToList());
            foreach (var missing in targets.Where(target => !responses.ContainsKey(target)))
                merged.PendingWorlds.UnionWith(P.MainWindow.ScopeCatalog.ExpandToWorldNames(
                    [missing], P.MainWindow.GetCurrentWorldScopeName()));
            merged.HistoryPending |= responses.Count < targets.Count;
            if (completed.Any(result => result.Status != UniversalisResponseStatus.Success))
            {
                merged.HistoryFailure = "Sales history incomplete: one or more custom targets failed.";
                if (backgroundReconciliation.Value) merged.ListingRefillPending = true;
            }
            if (backgroundReconciliation.Value)
                PreserveBackgroundHistory(merged, previousVerifiedResponse);
            return BrowsingSnapshot(merged);
        }
    }

    private async Task<UniversalisResponse> GetCurrentDataForTarget(
        MarketItem gameItem,
        string targetName,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress,
        Dictionary<string, MarketFreshnessProbe> worldProbeCache,
        MarketRefreshVocabulary vocabulary,
        UniversalisResponse? previousVerifiedResponse,
        Action? detailsStarted = null)
    {
        var worldNames = P.MainWindow.ScopeCatalog
            .ExpandToWorldNames([targetName], P.MainWindow.GetCurrentWorldScopeName());
        if (worldNames.Count == 0)
            worldNames.Add(targetName);

        var scanStartedAt = Stopwatch.GetTimestamp();
        var concurrency = requestBudget.Concurrency;
        var spacingMilliseconds = requestPacer.SpacingMilliseconds;
        TraceFetch(
            "aggregate-scan-started",
            targetName,
            $"Scanning {worldNames.Count} worlds with concurrency {concurrency} and {spacingMilliseconds}ms request spacing.");
        Service.Log.Info(
            $"[Universalis] Aggregate scan started for {targetName}: {worldNames.Count} worlds, " +
            $"concurrency {concurrency}, {spacingMilliseconds}ms request spacing.");

        var worldProbes = new MarketFreshnessProbe?[worldNames.Count];
        var deferredWorlds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        UniversalisResponse? firstProbeFailure = null;
        var completedWorldCount = 0;
        var freshDetailDeadline = DateTimeOffset.MaxValue;
        UniversalisResponse? firstPage = null;
        Task<UniversalisResponse>? listingTask = null;
        Task<UniversalisResponse>? historyTask = null;
        try
        {
        listingTask = GetListingDataForTarget(
            gameItem, targetName, cancellationToken, reportProgress: null, vocabulary,
            verificationPass: 1, requestProgress: GetFreshDetailProgress(FreshDetailTimeout),
            onPage: page =>
            {
                var pending = BrowsingSnapshot(page);
                pending.PendingWorlds.UnionWith(worldNames);
                pending.HistoryPending = true;
                firstPage = pending;
                publishDetails.Value?.Invoke(pending);
            });
        historyTask = GetDataForTarget(
            gameItem, targetName, cancellationToken, reportProgress: null, vocabulary,
            listingLimitOverride: 0, entryLimitOverride: EntryLimit);
        }
        catch
        {
            try { await Task.WhenAll(new[] { listingTask, historyTask }.OfType<Task<UniversalisResponse>>()); }
            catch { /* Observe every branch before preserving the factory failure. */ }
            throw;
        }
        finally { detailsStarted?.Invoke(); }
        var probeTask = StartWorldProbes();
        UniversalisResponse detailed;
        try { detailed = await listingTask; }
        catch
        {
            try { await Task.WhenAll(historyTask, probeTask); } catch { }
            throw;
        }
        if (detailed.Status != UniversalisResponseStatus.Success)
        {
            await Task.WhenAll(historyTask, probeTask);
            if (firstPage is not null)
                publishDetails.Value?.Invoke(BrowsingSnapshot(firstPage));
            return detailed;
        }
        var early = detailed.Snapshot();
        early.PendingWorlds.UnionWith(worldNames);
        early.HistoryPending = true;
        try { publishDetails.Value?.Invoke(early.Snapshot()); }
        catch
        {
            try { await Task.WhenAll(historyTask, probeTask); } catch { }
            throw;
        }
        var publicationLock = new object();
        var latest = early;
        UniversalisResponse? verificationFailure = null;
        var history = new UniversalisResponse { HistoryPending = true };
        var verificationTask = VerifyAndPublishAsync();
        var joinHistoryTask = JoinHistoryAsync();
        await Task.WhenAll(verificationTask, joinHistoryTask);
        if (backgroundReconciliation.Value && verificationFailure is not null)
            return verificationFailure;
        return BrowsingSnapshot(latest);

        async Task JoinHistoryAsync()
        {
            var fetchedHistory = await historyTask;
            lock (publicationLock)
            {
                history = fetchedHistory.Status == UniversalisResponseStatus.Success
                    ? fetchedHistory
                    : new UniversalisResponse { HistoryFailure = GetFailureDetail(fetchedHistory, "Sales history unavailable.") };
                // A custom scope's previous history is already merged: retain it only
                // at that scope boundary, never once per target.
                if (backgroundReconciliation.Value && detailAdmission.Value is null && previousVerifiedResponse is not null
                    && string.Equals(previousVerifiedResponse.ScopeName, targetName, StringComparison.OrdinalIgnoreCase))
                    PreserveBackgroundHistory(history, previousVerifiedResponse);
                latest = MarketListingReconciler.FinalizeVerifiedResponse(latest.Snapshot(), history, ListingLimit);
                publishDetails.Value?.Invoke(BrowsingSnapshot(latest));
            }
        }

        async Task VerifyAndPublishAsync()
        {
            var verified = await VerifyAsync();
            lock (publicationLock)
            {
                if (verified.Status != UniversalisResponseStatus.Success)
                {
                    verificationFailure = verified;
                    latest = BrowsingSnapshot(latest);
                    publishDetails.Value?.Invoke(BrowsingSnapshot(latest));
                    return;
                }
                latest = MarketListingReconciler.FinalizeVerifiedResponse(verified.Snapshot(), history, ListingLimit);
                publishDetails.Value?.Invoke(BrowsingSnapshot(latest));
            }
        }

        async Task<(int Index, string WorldName, MarketFreshnessProbe? Probe, UniversalisResponse? Failure)[]> StartWorldProbes()
        {
            if (detailAdmission.Value is { } admissionBarrier)
                await admissionBarrier.WaitAsync(cancellationToken);
            var pendingProbes = new List<Task<(int Index, string WorldName, MarketFreshnessProbe? Probe, UniversalisResponse? Failure)>>();
            for (var worldIndex = 0; worldIndex < worldNames.Count; worldIndex++)
            {
                var worldName = worldNames[worldIndex];
                if (worldProbeCache.TryGetValue(worldName, out var cachedProbe))
                {
                    worldProbes[worldIndex] = cachedProbe;
                    Interlocked.Increment(ref completedWorldCount);
                    continue;
                }

                var capturedIndex = worldIndex;
                pendingProbes.Add(FetchWorldProbeAsync(capturedIndex, worldName));
            }

            var results = await Task.WhenAll(pendingProbes);
            // Keep the original repair deadline anchored to aggregate completion.
            freshDetailDeadline = DateTimeOffset.UtcNow + FreshDetailTimeout;
            return results;
        }
        async Task<UniversalisResponse> VerifyAsync()
        {
            var fetchedProbes = await probeTask;
            foreach (var result in fetchedProbes.OrderBy(result => result.Index))
            {
                if (result.Failure is not null)
                {
                    firstProbeFailure ??= result.Failure;
                    deferredWorlds[result.WorldName] = GetFailureDetail(
                        result.Failure,
                        $"Universalis freshness data was unavailable for {result.WorldName}.");
                    continue;
                }
                if (result.Probe is null)
                {
                    deferredWorlds[result.WorldName] =
                        $"Universalis returned no aggregate freshness data for {gameItem.Name} on {result.WorldName}.";
                    continue;
                }

                worldProbeCache[result.WorldName] = result.Probe;
                worldProbes[result.Index] = result.Probe;
            }

            var resolvedWorldProbes = worldProbes
                .OfType<MarketFreshnessProbe>()
                .ToArray();
            if (resolvedWorldProbes.Length == 0)
            {
                return firstProbeFailure ?? new UniversalisResponse
                {
                    Status = UniversalisResponseStatus.InvalidData,
                    FailureDetail = $"Universalis returned no aggregate freshness data for {gameItem.Name} in {targetName}.",
                };
            }
            Service.Log.Info(
                $"[Universalis] Initial detail/history/aggregate fetch completed for {targetName}: {resolvedWorldProbes.Length} worlds in " +
                $"{Stopwatch.GetElapsedTime(scanStartedAt).TotalSeconds:F2}s.");
            TraceFetch(
                "initial-fetch-completed",
                targetName,
                $"Resolved freshness probes for {resolvedWorldProbes.Length} worlds; deferred {deferredWorlds.Count}.",
                durationMilliseconds: Stopwatch.GetElapsedTime(scanStartedAt).TotalMilliseconds);

            var deadline = freshDetailDeadline;

            detailed.VerificationProbes = resolvedWorldProbes;

            var lastMatch = MarketWorldPartitionPolicy.CompareEligibleScope(
                resolvedWorldProbes,
                detailed,
                HqOnly,
                ListingLimit,
                deferredWorlds);
            if (detailed.ProvisionalWorlds.Count > 0)
            {
                TraceFetch("recent-upload-provisional", targetName,
                    $"Returning exact rows while recent uploads reconcile on: {string.Join(", ", detailed.ProvisionalWorlds.Keys)}.");
            }
            if (lastMatch.IsCurrent)
            {
                TraceFetch(
                    detailed.ProvisionalWorlds.Count > 0
                        ? "detail-verification-provisional"
                        : deferredWorlds.Count == 0
                        ? "detail-verification-accepted"
                        : "detail-verification-partial",
                    targetName,
                    deferredWorlds.Count == 0
                        ? lastMatch.Detail
                        : $"Accepted verified worlds while deferring: {string.Join(", ", deferredWorlds.Keys)}.",
                    verificationPass: 1);
                return FinalizeVerifiedScope(
                    detailed,
                    history,
                    ListingLimit,
                    previousVerifiedResponse,
                    deferredWorlds);
            }

            MarketWorldPartitionPolicy.DeferGaps(
                lastMatch.Gaps.Where(gap => gap.Kind == MarketFreshnessGapKind.Conflict),
                deferredWorlds);
            lastMatch = MarketWorldPartitionPolicy.CompareEligibleScope(
                resolvedWorldProbes,
                detailed,
                HqOnly,
                ListingLimit,
                deferredWorlds);
            if (lastMatch.IsCurrent)
            {
                TraceFetch(
                    "detail-verification-partial",
                    targetName,
                    $"Accepted verified worlds while deferring: {string.Join(", ", deferredWorlds.Keys)}.",
                    verificationPass: 1);
                return FinalizeVerifiedScope(
                    detailed,
                    history,
                    ListingLimit,
                    previousVerifiedResponse,
                    deferredWorlds);
            }

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
            var targetedRepairPass = 0;
            var usedScopeRepair = false;
            if (MarketFreshnessRetryPolicy.ShouldUseScopeRepair(repairWorlds.Length, worldNames.Count))
            {
                usedScopeRepair = true;
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    MarketWorldPartitionPolicy.DeferGaps(lastMatch.Gaps, deferredWorlds);
                    lastMatch = MarketWorldPartitionPolicy.CompareEligibleScope(
                        resolvedWorldProbes,
                        detailed,
                        HqOnly,
                        ListingLimit,
                        deferredWorlds);
                    repairWorlds = [];
                }
                else
                {
                    var retryDelay = MarketFreshnessRetryPolicy.GetBackoff(
                        1,
                        FreshDetailRetryDelay,
                        remaining);
                    await Task.Delay(retryDelay, cancellationToken);
                    verificationPass++;
                    Service.Log.Info(
                        $"[Universalis] Retrying the complete {targetName} detail scope once because " +
                        $"{repairWorlds.Length}/{worldNames.Count} world partitions disagree with aggregate freshness.");
                    TraceFetch(
                        "detail-scope-repair-started",
                        targetName,
                        $"Retrying the complete detail scope once and refreshing probes for: {string.Join(", ", repairWorlds)}.",
                        verificationPass: verificationPass);

                    var scopeDetailTask = GetListingDataForTarget(
                        gameItem,
                        targetName,
                        cancellationToken,
                        reportProgress,
                        vocabulary,
                        verificationPass,
                        GetFreshDetailProgress(deadline - DateTimeOffset.UtcNow));
                    var refreshedProbeTasks = repairWorlds.Select(async worldName =>
                    {
                        var result = await GetFreshnessProbeForTarget(
                            gameItem,
                            worldName,
                            cancellationToken,
                            reportProgress: null,
                            vocabulary,
                            GetFreshDetailProgress(deadline - DateTimeOffset.UtcNow));
                        return (WorldName: worldName, result.Probe, result.Failure);
                    });
                    var refreshedProbes = await Task.WhenAll(refreshedProbeTasks);
                    var retriedDetailed = await scopeDetailTask;
                    if (retriedDetailed.Status == UniversalisResponseStatus.Success)
                    {
                        detailed = retriedDetailed;
                        detailed.VerificationProbes = resolvedWorldProbes;
                    }
                    else
                    {
                        Service.Log.Warning(
                            $"[Universalis] Complete {targetName} detail retry failed; preserving the first successful detail response and continuing with per-world repair.");
                    }

                    foreach (var refreshed in refreshedProbes)
                    {
                        if (refreshed.Failure is not null)
                        {
                            deferredWorlds[refreshed.WorldName] = GetFailureDetail(
                                refreshed.Failure,
                                $"Universalis freshness data remained unavailable for {refreshed.WorldName}.");
                            continue;
                        }
                        if (refreshed.Probe is null)
                        {
                            deferredWorlds[refreshed.WorldName] =
                                $"Universalis returned no aggregate freshness data for {gameItem.Name} on {refreshed.WorldName}.";
                            continue;
                        }

                        var probeIndex = Array.FindIndex(
                            resolvedWorldProbes,
                            probe => string.Equals(
                                probe.TargetName,
                                refreshed.WorldName,
                                StringComparison.OrdinalIgnoreCase));
                        if (probeIndex >= 0)
                            resolvedWorldProbes[probeIndex] = refreshed.Probe;
                    }

                    lastMatch = MarketWorldPartitionPolicy.CompareEligibleScope(
                        resolvedWorldProbes,
                        detailed,
                        HqOnly,
                        ListingLimit,
                        deferredWorlds);
                    if (!lastMatch.IsCurrent)
                    {
                        MarketWorldPartitionPolicy.DeferGaps(lastMatch.Gaps, deferredWorlds);
                        lastMatch = MarketWorldPartitionPolicy.CompareEligibleScope(
                            resolvedWorldProbes,
                            detailed,
                            HqOnly,
                            ListingLimit,
                            deferredWorlds);
                    }

                    repairWorlds = [];
                }
            }

            while (repairWorlds.Length > 0
                   && targetedRepairPass < MarketFreshnessRetryPolicy.MaxTargetedRepairPasses
                   && DateTimeOffset.UtcNow < deadline)
            {
                targetedRepairPass++;
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
                    {
                        deferredWorlds[repair.WorldName] = GetFailureDetail(
                            repair.Partition,
                            $"Detailed listings remained unavailable for {repair.WorldName}.");
                        continue;
                    }
                    if (repair.ProbeFailure is not null)
                    {
                        deferredWorlds[repair.WorldName] = GetFailureDetail(
                            repair.ProbeFailure,
                            $"Universalis freshness data remained unavailable for {repair.WorldName}.");
                        continue;
                    }
                    if (repair.Probe is null)
                    {
                        deferredWorlds[repair.WorldName] =
                            $"Universalis returned no aggregate freshness data for {gameItem.Name} on {repair.WorldName}.";
                        continue;
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

                lastMatch = MarketWorldPartitionPolicy.CompareEligibleScope(
                    resolvedWorldProbes,
                    detailed,
                    HqOnly,
                    ListingLimit,
                    deferredWorlds);
                if (lastMatch.IsCurrent)
                    break;

                MarketWorldPartitionPolicy.DeferGaps(
                    lastMatch.Gaps.Where(gap => gap.Kind == MarketFreshnessGapKind.Conflict),
                    deferredWorlds);
                lastMatch = MarketWorldPartitionPolicy.CompareEligibleScope(
                    resolvedWorldProbes,
                    detailed,
                    HqOnly,
                    ListingLimit,
                    deferredWorlds);
                if (lastMatch.IsCurrent)
                    break;

                var revisionsChanged = MarketFreshnessRetryPolicy.HasRevisionChange(previousGaps, lastMatch.Gaps);
                if (!revisionsChanged)
                {
                    TraceFetch(
                        "detail-targeted-repair-unchanged",
                        targetName,
                        $"Targeted repair returned the same revision pair on pass {targetedRepairPass}/{MarketFreshnessRetryPolicy.MaxTargetedRepairPasses}; backing off without another scope scan.",
                        verificationPass: verificationPass);
                }

                previousGaps = lastMatch.Gaps;
                repairWorlds = lastMatch.Gaps
                    .Where(gap => gap.Kind == MarketFreshnessGapKind.AggregateAhead)
                    .Select(gap => gap.WorldName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (repairWorlds.Length > 0
                    && targetedRepairPass < MarketFreshnessRetryPolicy.MaxTargetedRepairPasses
                    && remaining > TimeSpan.Zero)
                {
                    var retryDelay = MarketFreshnessRetryPolicy.GetBackoff(
                        targetedRepairPass,
                        FreshDetailRetryDelay,
                        remaining);
                    await Task.Delay(retryDelay, cancellationToken);
                }
            }

            if (!lastMatch.IsCurrent)
            {
                MarketWorldPartitionPolicy.DeferGaps(lastMatch.Gaps, deferredWorlds);
                lastMatch = MarketWorldPartitionPolicy.CompareEligibleScope(
                    resolvedWorldProbes,
                    detailed,
                    HqOnly,
                    ListingLimit,
                    deferredWorlds);
            }

            foreach (var deferredWorld in deferredWorlds.Keys)
                repairedPartitions.Remove(deferredWorld);

            var verifiedWorldCount = MarketWorldPartitionPolicy.CountVerifiedWorlds(
                resolvedWorldProbes,
                deferredWorlds);
            if ((verifiedWorldCount == 0 && detailed.ProvisionalWorlds.Count == 0) || !lastMatch.IsCurrent)
            {
                return CreateStaleResponse(
                    vocabulary,
                    "Universalis did not return a corroborated world partition for this scope.");
            }

            if (detailed.ListingPageMayBeTruncated
                && detailed.Listings.Count < ListingLimit
                && repairedPartitions.Count > 0
                && originalRepairWorldListingCount > 0)
            {
                var refillLimit = Math.Min(
                    UniversalisListingFetchPolicy.MaximumListingRequestLimit,
                    ListingLimit + originalRepairWorldListingCount);
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
                {
                    detailed.ListingRefillPending = true;
                    return FinalizeVerifiedScope(detailed, history, ListingLimit, previousVerifiedResponse, deferredWorlds);
                }

                foreach (var repair in repairedPartitions)
                    MarketListingReconciler.ReplaceWorldPartition(refill, repair.Key, repair.Value);
                detailed = refill;
                detailed.VerificationProbes = resolvedWorldProbes;
                lastMatch = MarketWorldPartitionPolicy.CompareEligibleScope(
                    resolvedWorldProbes,
                    detailed,
                    HqOnly,
                    ListingLimit,
                    deferredWorlds);
                if (!lastMatch.IsCurrent)
                {
                    MarketWorldPartitionPolicy.DeferGaps(lastMatch.Gaps, deferredWorlds);
                    lastMatch = MarketWorldPartitionPolicy.CompareEligibleScope(
                        resolvedWorldProbes,
                        detailed,
                        HqOnly,
                        ListingLimit,
                        deferredWorlds);
                }
            }

            TraceFetch(
                deferredWorlds.Count == 0
                    ? "detail-verification-accepted"
                    : "detail-verification-partial",
                targetName,
                deferredWorlds.Count > 0
                    ? $"Accepted {verifiedWorldCount}/{worldNames.Count} verified worlds; deferred {string.Join(", ", deferredWorlds.Keys)}."
                    : usedScopeRepair
                    ? "Accepted after one complete-scope detail repair."
                    : $"Accepted after targeted repair of {string.Join(", ", repairedPartitions.Keys)}.",
                verificationPass: verificationPass);
            return FinalizeVerifiedScope(
                detailed,
                history,
                ListingLimit,
                previousVerifiedResponse,
                deferredWorlds);

        }

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

    private UniversalisResponse MergeCustomScopeResponses(MarketItem gameItem, CustomMarketScope customScope,
        List<UniversalisResponse> responses, IReadOnlyDictionary<string, int>? historyWeights = null)
    {
        var mergedWorldOutOfDate = new Dictionary<string, double>();
        var mergedWorldUploadTimes = new Dictionary<string, long>();
        var mergedDeferredWorlds = new Dictionary<string, DeferredWorldPartition>(StringComparer.OrdinalIgnoreCase);
        var mergedProvisionalWorlds = new Dictionary<string, ProvisionalWorldPartition>(StringComparer.OrdinalIgnoreCase);
        var worldsWithoutListingData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            foreach (var deferred in response.DeferredWorlds)
                mergedDeferredWorlds[deferred.Key] = deferred.Value;
            foreach (var provisional in response.ProvisionalWorlds)
                mergedProvisionalWorlds[provisional.Key] = provisional.Value;
            worldsWithoutListingData.UnionWith(response.WorldsWithoutListingData);
        }

        var duplicateLimited = responses.Any(response =>
            response.ListingCoverage == MarketListingCoverageStatus.DuplicateLimited
            || MarketListingCoveragePolicy.IsDuplicateLimited(response, ListingLimit));
        var normalization = MarketListingNormalizer.Analyze(responses.SelectMany(response => response.Listings));
        foreach (var conflict in normalization.Conflicts)
            refusedIdentities.Value?.TryAdd(conflict, 0);
        var mergedListings = normalization.Listings
            .OrderBy(listing => listing.PricePerUnit)
            .ThenBy(listing => listing.Quantity)
            .Take(ListingLimit)
            .ToList();
        var merged = new UniversalisResponse
        {
            Status = UniversalisResponseStatus.Success,
            ItemId = (ulong)gameItem.Id,
            IsCrossWorld = true,
            WorldOutOfDate = mergedWorldOutOfDate,
            FetchTime = responses.Min(response => response.FetchTime),
            LatestUploadTime = mergedWorldUploadTimes.Count > 0 ? mergedWorldUploadTimes.Values.Max() : responses.Max(response => response.LatestUploadTime),
            WorldUploadTimes = mergedWorldUploadTimes,
            DeferredWorlds = mergedDeferredWorlds,
            ProvisionalWorlds = mergedProvisionalWorlds,
            ConflictingListingIdentities = normalization.Conflicts,
            PendingWorlds = new(responses.SelectMany(response => response.PendingWorlds), StringComparer.OrdinalIgnoreCase),
            HistoryPending = responses.Any(response => response.HistoryPending),
            ListingRefillPending = responses.Any(response => response.ListingRefillPending),
            HistoryFailure = string.Join("; ", responses.Select(response => response.HistoryFailure).Where(reason => reason.Length > 0)),
            HistoryListingWeights = responses.ToDictionary(response => response.ScopeName,
                response => historyWeights is not null && historyWeights.TryGetValue(response.ScopeName, out var weight)
                    ? weight : response.Listings.Count,
                StringComparer.OrdinalIgnoreCase),
            WorldsWithoutListingData = worldsWithoutListingData,
            VerificationProbes = responses.SelectMany(response => response.VerificationProbes).ToArray(),
            RawListingCount = responses.Sum(response => response.RawListingCount),
            ListingRequestLimit = responses.Sum(response => response.ListingRequestLimit),
            ListingPageMayBeTruncated = responses.Any(response => response.ListingPageMayBeTruncated),
            UnitsForSale = mergedListings.Sum(listing => listing.Quantity),
            AveragePrice = AverageWeightedByListings(responses, response => response.AveragePrice, historyWeights),
            AveragePriceNq = AverageWeightedByListings(responses, response => response.AveragePriceNq, historyWeights),
            AveragePriceHq = AverageWeightedByListings(responses, response => response.AveragePriceHq, historyWeights),
            Velocity = responses.Sum(response => response.Velocity),
            VelocityNq = responses.Sum(response => response.VelocityNq),
            VelocityHq = responses.Sum(response => response.VelocityHq),
            Listings = mergedListings,
            Entries = responses
                .SelectMany(response => response.Entries)
                .OrderByDescending(entry => entry.Timestamp)
                .Take(EntryLimit)
                .ToList(),
            ScopeName = customScope.Name,
        };

        foreach (var row in responses.SelectMany(response => response.Listings))
            if (MarketListingNormalizer.TryGetIdentity(row, out var id) && normalization.Conflicts.Contains(id))
                merged.PendingWorlds.Add(row.WorldName);

        return MarketListingCoveragePolicy.Classify(
            merged,
            ListingLimit,
            duplicateLimited);
    }

    private static double AverageWeightedByListings(List<UniversalisResponse> responses,
        Func<UniversalisResponse, double> selector, IReadOnlyDictionary<string, int>? historyWeights = null)
    {
        int Weight(UniversalisResponse response) => historyWeights is not null
            && historyWeights.TryGetValue(response.ScopeName, out var weight) ? weight : response.Listings.Count;
        var totalListings = responses.Sum(Weight);
        if (totalListings == 0)
            return responses.Average(selector);

        return responses.Sum(response => selector(response) * Weight(response)) / totalListings;
    }

    private UniversalisResponse FinalizeVerifiedScope(
        UniversalisResponse detailed,
        UniversalisResponse history,
        int listingLimit,
        UniversalisResponse? previousVerifiedResponse,
        IReadOnlyDictionary<string, string> deferredWorlds)
    {
        var duplicateLimited = MarketListingCoveragePolicy.IsDuplicateLimited(
            detailed,
            listingLimit);
        if (duplicateLimited)
        {
            TraceFetch(
                "detail-verification-duplicate-limited",
                detailed.ScopeName,
                $"Accepted {detailed.Listings.Count} verified unique listings from {detailed.RawListingCount} Universalis rows after bounded expansion; requested {listingLimit} unique listings.");
            Service.Log.Warning(
                $"[Universalis] Accepted duplicate-limited current response for {detailed.ScopeName}: " +
                $"{detailed.Listings.Count}/{listingLimit} unique listings from {detailed.RawListingCount} rows.");
        }

        if (deferredWorlds.Count > 0)
        {
            var observed = detailed.Snapshot();
            MarketListingReconciler.ApplyDeferredWorldPartitions(
                detailed,
                previousVerifiedResponse,
                deferredWorlds);
            foreach (var deferred in detailed.DeferredWorlds.Where(pair => !pair.Value.RetainedPreviousPartition))
            {
                var rows = observed.Listings.Where(row => MarketListingReconciler.MatchesWorld(row, deferred.Key)).ToArray();
                if (rows.Length == 0) continue;
                detailed.Listings = detailed.Listings.Concat(rows).ToList();
                detailed.PendingWorlds.Add(deferred.Key);
            }
        }

        return MarketListingCoveragePolicy.Classify(
            MarketListingReconciler.FinalizeVerifiedResponse(
                detailed,
                history,
                listingLimit),
            listingLimit,
            duplicateLimited);
    }

    private static string GetFailureDetail(
        UniversalisResponse failure,
        string fallback)
        => string.IsNullOrWhiteSpace(failure.FailureDetail)
            ? fallback
            : failure.FailureDetail;

    private async Task<UniversalisResponse> GetListingDataForTarget(
        MarketItem gameItem,
        string targetName,
        CancellationToken cancellationToken,
        Action<UniversalisRequestProgress>? reportProgress,
        MarketRefreshVocabulary vocabulary,
        int verificationPass,
        float requestProgress,
        int? desiredUniqueListings = null,
        int? initialRequestLimit = null,
        Action<UniversalisResponse>? onPage = null)
    {
        var desired = Math.Clamp(
            desiredUniqueListings ?? ListingLimit,
            1,
            UniversalisListingFetchPolicy.MaximumListingRequestLimit);
        var requestLimit = Math.Clamp(
            initialRequestLimit ?? desired,
            1,
            UniversalisListingFetchPolicy.MaximumListingRequestLimit);
        var adaptivePass = 0;
        int? previousUniqueListingCount = null;
        UniversalisResponse? bestResponse = null;
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

            if (bestResponse is null || response.Listings.Count >= bestResponse.Listings.Count)
                bestResponse = response;

            onPage?.Invoke(MarketListingCoveragePolicy.Classify(response.Snapshot(), desired));

            var nextLimit = UniversalisListingFetchPolicy.GetNextRequestLimit(
                desired,
                requestLimit,
                response.RawListingCount,
                response.Listings.Count,
                previousUniqueListingCount);
            if (nextLimit is null)
            {
                if (previousUniqueListingCount.HasValue
                    && response.RawListingCount >= requestLimit
                    && response.Listings.Count <= previousUniqueListingCount.Value)
                {
                    TraceFetch(
                        "detail-adaptive-overfetch-stalled",
                        targetName,
                        $"Stopped listing expansion at {requestLimit} rows because the response produced " +
                        $"{response.Listings.Count} unique identities after the prior pass produced {previousUniqueListingCount.Value}.",
                        verificationPass: verificationPass + adaptivePass - 1);
                }

                return bestResponse;
            }

            TraceFetch(
                "detail-adaptive-overfetch",
                targetName,
                $"Expanded listing request from {requestLimit} to {nextLimit.Value} after {response.RawListingCount} rows produced {response.Listings.Count} unique identities.",
                verificationPass: verificationPass + adaptivePass);
            previousUniqueListingCount = response.Listings.Count;
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
            var listingLimit = listingLimitOverride ?? ListingLimit;
            var entryLimit = entryLimitOverride ?? EntryLimit;
            var _hq = (highQualityOnly ?? HqOnly) ? "&hq=1" : "";
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

                    if (MarketListingDataPolicy.HasContradictoryDataFlag(data))
                        return new UniversalisResponse
                        {
                            Status = UniversalisResponseStatus.InvalidData,
                            FailureDetail = "Universalis marked listing data unavailable but returned listing rows.",
                        };

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
                        foreach (var conflict in interpretedResponse.ConflictingListingIdentities)
                            refusedIdentities.Value?.TryAdd(conflict, 0);
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
        using var lease = await requestBudget.EnterAsync(cancellationToken);
        await WaitForSharedCooldownAsync(
            targetName,
            progress,
            reportProgress,
            cancellationToken);
        await requestPacer.WaitAsync(cancellationToken);

        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(timeout);
        return await httpClient.SendAsync(request, requestTimeout.Token);
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
            if (data.LastUploadTime > 0)
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
            HasData = data.HasData,
            WorldsWithoutListingData = data.HasData == false && data.Listings.Count == 0
                ? new(StringComparer.OrdinalIgnoreCase) { targetName }
                : new(StringComparer.OrdinalIgnoreCase),
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
