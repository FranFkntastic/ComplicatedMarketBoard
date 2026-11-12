using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Text;
using Lumina.Excel.Sheets;
using Dalamud.Interface.ImGuiNotification;
using Miosuke.Messages;
using Dalamud.Interface.Textures;
using ComplicatedMarketBoard.Assets;
using ComplicatedMarketBoard.Integrations.Universalis;
using System.Threading;


namespace ComplicatedMarketBoard.Market;

public sealed class MarketRefreshService
{
    private readonly MarketRefreshRequestCoordinator requestCoordinator = new();

    public MarketRefreshService()
    {
    }

    public void Dispose()
    {
        requestCoordinator.Dispose();
    }


    // -------------------------------- game item --------------------------------
    public List<MarketItem> GameItemCacheList = [];

    // Request starts and all UI/cache publications are serialized by Dalamud's owning thread.
    // No projection rebuild runs under the coordinator lock, and a superseding start cannot
    // interleave between the identity check and the publication.
    private void Publish(MarketRefreshRequestContext request, System.Action publish)
        => Service.Framework.RunOnFrameworkThread(() =>
        {
            if (requestCoordinator.IsCurrent(request)
                && request.Key.TargetScope == GetRequestIdentity(P.MainWindow.GetSelectedMarketScopeLabel()))
                publish();
        }).GetAwaiter().GetResult();

    private void ReportProgress(MarketRefreshRequestContext request, UniversalisRequestProgress progress)
    {
        if (request.HasPublished) return;
        Interlocked.Exchange(ref request.LatestProgress, progress);
        if (Interlocked.Exchange(ref request.ProgressQueued, 1) != 0) return;
        _ = DispatchAsync();

        async Task DispatchAsync()
        {
            try
            {
                await Service.Framework.RunOnFrameworkThread(() =>
                {
                    Interlocked.Exchange(ref request.ProgressQueued, 0);
                    var latest = Interlocked.Exchange(ref request.LatestProgress, null);
                    if (latest is not null && !request.HasPublished && requestCoordinator.IsCurrent(request)
                        && request.Key.TargetScope == GetRequestIdentity(P.MainWindow.GetSelectedMarketScopeLabel()))
                        P.MainWindow.UpdateMarketDataRefresh(latest.StatusText, latest.Progress);
                }).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref request.ProgressQueued, 0);
                Service.Log.Warning(exception, "[MarketRefresh] Progress publication failed.");
            }
        }
    }

    private void FinishRequest(MarketRefreshRequestContext request)
        => Service.Framework.RunOnFrameworkThread(() =>
        {
            // A settings-only change must resume the same intent instead of leaving Loading stuck.
            if (requestCoordinator.IsCurrent(request)
                && request.Key.TargetScope != GetRequestIdentity(P.MainWindow.GetSelectedMarketScopeLabel()))
                BeginCheck(request.Key.ItemId);
            requestCoordinator.End(request);
        }).GetAwaiter().GetResult();

    private void PublishFirst(MarketRefreshRequestContext request, MarketItem item)
    {
        if (!request.HasPublished)
        {
            SearchHistoryUpdate(item);
            request.HasPublished = true;
        }
        else
        {
            // Replace the cached snapshot without changing recency or mutating enumerated lists.
            var index = GameItemCacheList.FindIndex(cached => cached.Id == item.Id);
            if (index >= 0) GameItemCacheList[index] = item.Snapshot();
        }
        PublishPriceNotifications(request, item);
        ScheduleEligibilityExpiry(request, item);
    }

    private void ScheduleEligibilityExpiry(MarketRefreshRequestContext request, MarketItem item)
    {
        request.EligibilityTimer?.Cancel();
        request.EligibilityTimer?.Dispose();
        request.EligibilityTimer = null;
        var boundaries = item.UniversalisResponse.ProvisionalWorlds.Values
            .Where(partition => !partition.EligibilityExpired)
            .Select(partition => partition.AggregateUploadTime
                + (long)MarketProvisionalListingPolicy.RecentUploadWindow.TotalMilliseconds + 1).ToArray();
        if (boundaries.Length == 0) return;
        var timer = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation.Token);
        request.EligibilityTimer = timer;
        _ = ExpireAsync(timer.Token);

        async Task ExpireAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1,
                    boundaries.Min() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())), token);
                Publish(request, () =>
                {
                    var snapshot = item.UniversalisResponse.Snapshot();
                    if (!MarketProvisionalListingPolicy.ExpireDisplayedQualifications(snapshot,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return;
                    item.UniversalisResponse = snapshot;
                    item.Observations.Remember(item.CachedRequestIdentity, snapshot);
                    P.MainWindow.CurrentItemUpdate(item);
                    P.MainWindow.CompleteMarketDataRefresh(item.Name, MarketProvisionalListingPolicy.Summary(snapshot));
                    PublishFirst(request, item);
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { Service.Log.Warning(exception, "[MarketRefresh] Qualification expiry publication failed."); }
        }
    }

    // -------------------------------- market refresh --------------------------------
    public void DoCheckAsync(ulong itemId)
        => _ = Service.Framework.RunOnFrameworkThread(() => BeginCheck(itemId));

    private void BeginCheck(ulong itemId)
    {
        Service.Log.Debug($"[MarketRefresh] Start item lookup: {itemId}");
        var requestedScope = P.MainWindow.GetSelectedMarketScopeLabel();
        var request = requestCoordinator.TryBeginCoalesced(
            new MarketRefreshRequestKey(itemId, GetRequestIdentity(requestedScope), RequireCurrentDetails: true));
        if (request is null) return;
        var cachedGameItem = GameItemCacheList.FirstOrDefault(i => i.Id == itemId)?.Snapshot();
        if (cachedGameItem is not null && cachedGameItem.CachedRequestIdentity == request.Key.TargetScope)
        {
            cachedGameItem.UniversalisResponse = cachedGameItem.UniversalisResponse.Snapshot();
            MarketProvisionalListingPolicy.ExpireDisplayedQualifications(cachedGameItem.UniversalisResponse,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            P.MainWindow.CurrentItemUpdate(cachedGameItem);
        }
        P.MainWindow.BeginMarketDataRefresh("item", "Loading item");

        Task.Run(async () =>
        {
            try
            {
                Interlocked.Increment(ref P.MainWindow.LoadingQueue);
                await CheckItemAsync(request, itemId, requestedScope, cachedGameItem);
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Service.Log.Error($"[MarketRefresh] Item lookup failed, {ex.Message}");
                Publish(request, () =>
                {
                    P.MainWindow.CurrentItemLabel = "Error";
                    P.MainWindow.FailMarketDataRefresh(ex.Message);
                });
            }
            finally
            {
                Interlocked.Decrement(ref P.MainWindow.LoadingQueue);
                FinishRequest(request);
            }
        });
    }

    private async Task CheckItemAsync(
        MarketRefreshRequestContext request,
        ulong itemId,
        string requestedScope,
        MarketItem? cached_gameItem)
    {
        if (cached_gameItem is not null)
        {
            Service.Log.Debug($"[MarketRefresh] {itemId} found in cache.");
            await CheckGameItemAsync(request, cached_gameItem, requestedScope, requireCurrentDetails: true);
            return;
        }

        var gameItem = new MarketItem()
        {
            Id = itemId,
            InGame = Data.ItemSheet.Single(i => i.RowId == (uint)itemId),
            VendorSelling = 0,
        };
        gameItem.Name = gameItem.InGame.Name.ToString();

        if (gameItem.InGame.ItemSearchCategory.RowId == 0)
        {
            Publish(request, () =>
            {
            Service.NotificationManager.AddNotification(new Notification
            {
                Content = $"{gameItem.Name} [{gameItem.Id}] is unmarketable.",
                Type = NotificationType.Warning,
            });
            P.MainWindow.FailMarketDataRefresh("Item is unmarketable.");
            });
            return;
        }

        var valid_vendors = Service.Data.GetSubrowExcelSheet<GilShopItem>().Flatten().Where(i => i.Item.RowId == (uint)gameItem.Id).ToList();
        if (valid_vendors is { Count: > 0 })
        {
            gameItem.VendorSelling = gameItem.InGame.PriceMid;
        }

        await CheckGameItemAsync(request, gameItem, requestedScope, requireCurrentDetails: true);
    }

    public void DoCheckRefreshAsync(MarketItem gameItem)
        => _ = Service.Framework.RunOnFrameworkThread(() => BeginRefresh(gameItem));

    private void BeginRefresh(MarketItem gameItem)
    {
        var requestedScope = P.MainWindow.GetSelectedMarketScopeLabel();
        var requestKey = new MarketRefreshRequestKey(gameItem.Id, GetRequestIdentity(requestedScope), RequireCurrentDetails: true);
        var request = requestCoordinator.TryBeginCoalesced(requestKey);
        if (request is null)
        {
            Service.Log.Debug($"[MarketRefresh] Refresh already active: {gameItem.Id} on {requestedScope}.");
            return;
        }

        Service.Log.Debug($"[MarketRefresh] Start refresh: {gameItem.Id} on {requestedScope}");
        P.MainWindow.BeginMarketDataRefresh(gameItem.Name);

        Task.Run(async () =>
        {
            try
            {
                Interlocked.Increment(ref P.MainWindow.LoadingQueue);
                await CheckGameItemAsync(request, gameItem, requestedScope, requireCurrentDetails: true, forceRefresh: true);
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Service.Log.Error($"[MarketRefresh] Refresh failed, {ex.Message}");
                Publish(request, () =>
                {
                    P.MainWindow.CurrentItemLabel = "Error";
                    P.MainWindow.FailMarketDataRefresh(ex.Message);
                });
            }
            finally
            {
                Interlocked.Decrement(ref P.MainWindow.LoadingQueue);
                FinishRequest(request);
            }
        });
    }


    private async Task CheckGameItemAsync(
        MarketRefreshRequestContext request,
        MarketItem gameItem,
        string requestedScope,
        bool requireCurrentDetails,
        bool forceRefresh = false)
    {
        request.Cancellation.Token.ThrowIfCancellationRequested();
        if (!requestCoordinator.IsCurrent(request))
            return;

        gameItem = gameItem.Snapshot();

        var requestIdentity = request.Key.TargetScope;
        if (requestIdentity != GetRequestIdentity(requestedScope)) return;
        UniversalisResponse? retained = null;
        Publish(request, () =>
        {
            gameItem.Observations.Remember(gameItem.CachedRequestIdentity, gameItem.UniversalisResponse);
            retained = gameItem.Observations.Find(requestIdentity);
        });
        if (retained != null)
        {
            gameItem.UniversalisResponse = retained;
            gameItem.CachedRequestIdentity = requestIdentity;
            gameItem.WorldOutOfDate = retained.WorldOutOfDate;
            gameItem.AvgPrice = retained.AveragePrice;
        }
        if (gameItem.CachedRequestIdentity != requestIdentity)
        {
            // Preserve other scope observations in their cache, never under this request's label.
            gameItem.UniversalisResponse = new();
            gameItem.WorldOutOfDate = [];
            gameItem.AvgPrice = 0;
        }
        Publish(request, () =>
        {
            gameItem.UniversalisResponse = gameItem.UniversalisResponse.Snapshot();
            MarketProvisionalListingPolicy.ExpireDisplayedQualifications(gameItem.UniversalisResponse,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            P.MainWindow.CurrentItemUpdate(gameItem);
        });
        if (MarketRefetchPolicy.CanReuse(P.Config.ListingRefetch, P.Config.ListingRefetchAgeSeconds,
                gameItem.UniversalisResponse.FetchTime, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                gameItem.UniversalisResponse.Status == UniversalisResponseStatus.Success,
                gameItem.CachedRequestIdentity == requestIdentity, forceRefresh))
        {
            gameItem.TargetRegion = requestedScope;
            Publish(request, () =>
            {
                gameItem.UniversalisResponse = gameItem.UniversalisResponse.Snapshot();
                MarketProvisionalListingPolicy.ExpireDisplayedQualifications(gameItem.UniversalisResponse,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                P.MainWindow.ShowCachedMarketData(gameItem.Name);
                P.MainWindow.CurrentItemUpdate(gameItem);
                PublishFirst(request, gameItem);
            });
            await ReconcileProvisionalAsync(request, gameItem, MarketRefreshVocabulary.Create(
                P.Config.HolidaySpirit, DateOnly.FromDateTime(DateTime.Now)));
            return;
        }
        gameItem.TargetRegion = requestedScope;
        gameItem.FetchTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var vocabulary = MarketRefreshVocabulary.Create(
            P.Config.HolidaySpirit,
            DateOnly.FromDateTime(DateTime.Now));
        Publish(request, () => P.MainWindow.BeginMarketDataRefresh(
            gameItem.Name,
            vocabulary.Preparing(gameItem.Name)));
        var universalisResponse = await P.Universalis.GetDataAsync(
            gameItem,
            request.Cancellation.Token,
            progress => ReportProgress(request, progress),
            requireCurrentDetails,
            vocabulary,
            onDetails: PublishDetails);
        request.Cancellation.Token.ThrowIfCancellationRequested();
        if (!requestCoordinator.IsCurrent(request) || requestIdentity != GetRequestIdentity(requestedScope))
            return;

        Publish(request, () => P.MainWindow.UpdateMarketDataRefresh(
            vocabulary.Processing(gameItem.Name), 0.90f));

        if (universalisResponse.Status != UniversalisResponseStatus.Success)
        {
            Publish(request, () =>
            {
            var failureText = GetUniversalisFailureText(universalisResponse);
            var hasPreviousData = gameItem.UniversalisResponse.Status == UniversalisResponseStatus.Success
                                  && gameItem.UniversalisResponse.FetchTime > 0;

            Service.NotificationManager.AddNotification(new Notification
            {
                Content = $"Market refresh failed for {gameItem.Name}: {failureText}",
                Type = NotificationType.Warning,
            });

            if (!hasPreviousData)
            {
                gameItem.UniversalisResponse = universalisResponse;
                P.MainWindow.CurrentItemUpdate(gameItem);
            }

            P.MainWindow.FailMarketDataRefresh(
                failureText,
                vocabulary.Failure(failureText));
            });
            await ReconcileProvisionalAsync(request, gameItem, vocabulary);
            return;
        }

        Publish(request, () =>
        {
            if (requestIdentity != GetRequestIdentity(requestedScope)) return;
            gameItem.UniversalisResponse = universalisResponse;
            // A setting changed during the await must not label a mixed request as reusable.
            gameItem.CachedRequestIdentity = requestIdentity == GetRequestIdentity(requestedScope) ? requestIdentity : "";
            gameItem.Observations.Remember(gameItem.CachedRequestIdentity, universalisResponse);
            gameItem.WorldOutOfDate = universalisResponse.WorldOutOfDate;
            gameItem.AvgPrice = universalisResponse.AveragePrice;

            P.MainWindow.CurrentItemUpdate(gameItem);
            PublishFirst(request, gameItem);
            var duplicateLimited = universalisResponse.ListingCoverage == MarketListingCoverageStatus.DuplicateLimited;
            var completionText = universalisResponse.NeedsQualification
                ? MarketProvisionalListingPolicy.Summary(universalisResponse)
                : MarketListingDataPolicy.CompletionSummary(universalisResponse, gameItem.Name)
                  ?? (universalisResponse.DeferredWorlds.Count > 0 || duplicateLimited
                ? vocabulary.PartiallyConfirmed(
                    gameItem.Name,
                    universalisResponse.DeferredWorlds.Keys.ToArray(),
                    universalisResponse.Listings.Count,
                    universalisResponse.RequestedListingCount,
                    duplicateLimited)
                : vocabulary.Confirmed(gameItem.Name));
            P.MainWindow.CompleteMarketDataRefresh(
                gameItem.Name,
                completionText);
        });
        await ReconcileProvisionalAsync(request, gameItem, vocabulary);

        void PublishDetails(UniversalisResponse response)
        {
            Publish(request, () =>
            {
                if (requestIdentity != GetRequestIdentity(requestedScope)) return;
                gameItem.UniversalisResponse = response;
                gameItem.CachedRequestIdentity = requestIdentity;
                gameItem.WorldOutOfDate = response.WorldOutOfDate;
                gameItem.AvgPrice = response.AveragePrice;
                gameItem.Observations.Remember(requestIdentity, response);
                P.MainWindow.CurrentItemUpdate(gameItem);
                PublishFirst(request, gameItem);
                P.MainWindow.CompleteMarketDataRefresh(gameItem.Name, response.NeedsQualification
                    ? MarketProvisionalListingPolicy.Summary(response) : vocabulary.Confirmed(gameItem.Name));
            });
        }
    }

    private async Task ReconcileProvisionalAsync(
        MarketRefreshRequestContext request,
        MarketItem gameItem,
        MarketRefreshVocabulary vocabulary)
    {
        request.IsReconciling = true;
        var retry = 0;
        while (gameItem.UniversalisResponse.NeedsListingReconciliation
               || gameItem.UniversalisResponse.HistoryPending
               || gameItem.UniversalisResponse.HistoryFailure.Length > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(retry++, 4)))),
                request.Cancellation.Token);
            if (!requestCoordinator.IsCurrent(request)
                || request.Key.TargetScope != GetRequestIdentity(gameItem.TargetRegion))
                return;
            ExpireDisplayedQualifications();

            UniversalisResponse response;
            using var recheck = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation.Token);
            recheck.CancelAfter(P.Universalis.ReconciliationAttemptTimeout(gameItem));
            try
            {
                var reconciliationIdentity = GetRequestIdentity(gameItem.TargetRegion);
                if (gameItem.CachedRequestIdentity != reconciliationIdentity)
                    gameItem.CachedRequestIdentity = "";
                var attempt = P.Universalis.GetDataAsync(
                    gameItem.Snapshot(), recheck.Token, requireCurrentDetails: true,
                    vocabulary: vocabulary, reconcileProvisionalOnly: true);
                response = await attempt;
                if (reconciliationIdentity != GetRequestIdentity(gameItem.TargetRegion))
                    gameItem.CachedRequestIdentity = "";
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                ExpireDisplayedQualifications();
                continue;
            }
            catch (Exception exception)
            {
                Service.Log.Warning(exception, "[MarketRefresh] Background corroboration failed; preserving qualified results and retrying.");
                continue;
            }
            request.Cancellation.Token.ThrowIfCancellationRequested();
            if (!requestCoordinator.IsCurrent(request)
                || request.Key.TargetScope != GetRequestIdentity(gameItem.TargetRegion))
                return;
            ExpireDisplayedQualifications();
            if (response.Status != UniversalisResponseStatus.Success)
            {
                Service.Log.Warning($"[MarketRefresh] Background corroboration unavailable: {GetUniversalisFailureText(response)}; retrying automatically.");
                continue;
            }

            Publish(request, () =>
            {
                if (request.Key.TargetScope != GetRequestIdentity(gameItem.TargetRegion)) return;
                MarketProvisionalListingPolicy.ExpireDisplayedQualifications(response, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                gameItem.UniversalisResponse = response;
                gameItem.Observations.Remember(gameItem.CachedRequestIdentity, response);
                gameItem.WorldOutOfDate = response.WorldOutOfDate;
                gameItem.AvgPrice = response.AveragePrice;
                P.MainWindow.CurrentItemUpdate(gameItem);
                PublishFirst(request, gameItem);
                P.MainWindow.CompleteMarketDataRefresh(gameItem.Name,
                    response.NeedsQualification
                        ? MarketProvisionalListingPolicy.Summary(response)
                        : MarketListingDataPolicy.CompletionSummary(response, gameItem.Name)
                          ?? vocabulary.PartiallyConfirmed(gameItem.Name, response.DeferredWorlds.Keys.ToArray(),
                            response.Listings.Count, response.RequestedListingCount,
                            response.ListingCoverage == MarketListingCoverageStatus.DuplicateLimited));
                Service.Log.Info($"[MarketRefresh] Background corroboration: {response.ProvisionalWorlds.Count} provisional, {response.DeferredWorlds.Count} deferred world(s).");
            });
        }

        void ExpireDisplayedQualifications()
        {
            Publish(request, () =>
            {
                if (request.Key.TargetScope != GetRequestIdentity(gameItem.TargetRegion)) return;
                var snapshot = gameItem.UniversalisResponse.Snapshot();
                if (!requestCoordinator.IsCurrent(request)
                    || !MarketProvisionalListingPolicy.ExpireDisplayedQualifications(
                        snapshot, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                    return;
                gameItem.UniversalisResponse = snapshot;
                gameItem.Observations.Remember(gameItem.CachedRequestIdentity, snapshot);
                P.MainWindow.CurrentItemUpdate(gameItem);
                PublishFirst(request, gameItem);
                P.MainWindow.CompleteMarketDataRefresh(gameItem.Name,
                    MarketProvisionalListingPolicy.Summary(gameItem.UniversalisResponse));
            });
        }
    }

    private static string GetRequestIdentity(string requestedScope)
    {
        var custom = P.Config.CustomMarketScopes.FirstOrDefault(scope => scope.Id == P.Config.selectedCustomScopeId);
        var targets = custom is null ? new[] { requestedScope } : custom.IncludedScopes.ToArray();
        var worlds = P.MainWindow.ScopeCatalog.ExpandToWorldNames(targets, P.MainWindow.GetCurrentWorldScopeName());
        return MarketRefetchPolicy.RequestIdentity(requestedScope, custom?.Id, worlds,
            P.Config.UniversalisHqOnly, P.Config.UniversalisListings, P.Config.UniversalisEntries);
    }

    private static string GetUniversalisFailureText(UniversalisResponse response)
        => string.IsNullOrWhiteSpace(response.FailureDetail)
            ? GetUniversalisStatusLabel(response.Status)
            : response.FailureDetail;

    private static string GetUniversalisStatusLabel(ulong status) => status switch
    {
        UniversalisResponseStatus.ServerError => "server error",
        UniversalisResponseStatus.InvalidData => "invalid data",
        UniversalisResponseStatus.UserCancellation => "request timed out",
        UniversalisResponseStatus.StaleData => "current listing details unavailable",
        UniversalisResponseStatus.UnknownError => "unknown error",
        _ => $"status {status}",
    };

    // -------------------------------- search history --------------------------------
    public void SearchHistoryUpdate(MarketItem gameItem)
    {
        SearchHistoryClean();
        GameItemCacheList.RemoveAll(i => i.Id == gameItem.Id);
        GameItemCacheList.Insert(0, gameItem.Snapshot());
    }

    public void SearchHistoryClean()
    {
        Service.Log.Debug($"[Cache] Items in cache {GameItemCacheList.Count}");

        if (GameItemCacheList.Count < P.Config.MaxCacheItems) return;

        if (P.Config.CleanCacheASAP || !P.Config.CleanCacheASAP && !P.MainWindow.IsOpen)
        {
            GameItemCacheList.RemoveRange(
                P.Config.MaxCacheItems - 1,
                GameItemCacheList.Count - P.Config.MaxCacheItems + 1
            );
            Service.Log.Debug($"[Cache] Cache cleaned. Current items in cache {GameItemCacheList.Count}");
        }
    }


    // -------------------------------- notification --------------------------------
    private void PublishPriceNotifications(MarketRefreshRequestContext request, MarketItem gameItem)
    {
        var response = gameItem.UniversalisResponse;
        var historyReady = !response.HistoryPending && response.HistoryFailure.Length == 0;
        if (response.Status == UniversalisResponseStatus.Success
            && response.PendingWorlds.Count == 0 && response.ProvisionalWorlds.Count == 0
            && !response.ListingRefillPending
            && response.WorldsWithoutListingData.Count == 0
            && response.Listings.Count > 0)
        {
            if (P.Config.EnableChatLog && !request.ChatNotificationPublished
                && (P.Config.priceToPrint == PriceDisplayMode.SellingLow || historyReady)
                && (P.Config.priceToPrint != PriceDisplayMode.SoldLow || response.Entries.Count > 0))
            {
                request.ChatNotificationPublished = true;
                SendChatMessage(gameItem);
            }
            // Toasts retain their existing average-price policy, independently of chat mode.
            if (P.Config.EnableToastLog && !request.ToastNotificationPublished && historyReady
                && (P.Config.priceToPrint != PriceDisplayMode.SoldLow || response.Entries.Count > 0))
            {
                request.ToastNotificationPublished = true;
                SendToast(gameItem);
            }
        }
    }

    public void SendChatMessage(MarketItem gameItem)
    {
        double price;
        if (P.Config.priceToPrint == PriceDisplayMode.SellingLow)
        {
            price = gameItem.UniversalisResponse.Listings[0].PricePerUnit;
        }
        else if (P.Config.priceToPrint == PriceDisplayMode.SoldLow)
        {
            price = gameItem.UniversalisResponse.Entries.OrderBy(entry => entry.PricePerUnit).First().PricePerUnit;
        }
        else
        {
            price = gameItem.AvgPrice;
        }

        Chat.PluginMessage(
            P.Config.ChatLogChannel,
            $"[{NameShort}]",
            [
                new TextPayload($" [{gameItem.TargetRegion}]"),
                new UIForegroundPayload(39),
                new ItemPayload((uint)gameItem.Id),
                new TextPayload($"{(char)SeIconChar.LinkMarker} {gameItem.InGame.Name}"),
                RawPayload.LinkTerminator,
                new TextPayload($": {price:N0} {(char)SeIconChar.Gil}"),
                new UIForegroundPayload(0)
            ],
            P.PluginPayload);
    }

    public void SendToast(MarketItem gameItem)
    {
        Toast.Normal(
            $"[{gameItem.TargetRegion}] {gameItem.InGame.Name}: {gameItem.AvgPrice:N0} {(char)SeIconChar.Gil}",
            Dalamud.Game.Gui.Toast.ToastPosition.Bottom);
    }
}
