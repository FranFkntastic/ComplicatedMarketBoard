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

    // -------------------------------- market refresh --------------------------------
    public void DoCheckAsync(ulong itemId)
    {
        Service.Log.Debug($"[MarketRefresh] Start item lookup: {itemId}");
        var requestedScope = P.MainWindow.GetSelectedMarketScopeLabel();
        var request = requestCoordinator.BeginSuperseding(
            new MarketRefreshRequestKey(itemId, requestedScope, RequireCurrentDetails: false));

        Task.Run(async () =>
        {
            try
            {
                Interlocked.Increment(ref P.MainWindow.LoadingQueue);
                await CheckItemAsync(request, itemId, requestedScope);
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Service.Log.Error($"[MarketRefresh] Item lookup failed, {ex.Message}");
                if (requestCoordinator.IsCurrent(request))
                {
                    P.MainWindow.CurrentItemLabel = "Error";
                    P.MainWindow.FailMarketDataRefresh(ex.Message);
                }
            }
            finally
            {
                Interlocked.Decrement(ref P.MainWindow.LoadingQueue);
                requestCoordinator.End(request);
            }
        });
    }

    private async Task CheckItemAsync(
        MarketRefreshRequestContext request,
        ulong itemId,
        string requestedScope)
    {
        var _cacheIds = GameItemCacheList.Select(i => i.Id).ToList();
        if (_cacheIds.Contains(itemId))
        {
            Service.Log.Debug($"[MarketRefresh] {itemId} found in cache.");
            var cached_gameItem = GameItemCacheList.Single(i => i.Id == itemId);
            if (requestCoordinator.IsCurrent(request))
            {
                P.MainWindow.ShowCachedMarketData(cached_gameItem.Name);
                P.MainWindow.CurrentItemUpdate(cached_gameItem);
            }
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
            Service.NotificationManager.AddNotification(new Notification
            {
                Content = $"{gameItem.Name} [{gameItem.Id}] is unmarketable.",
                Type = NotificationType.Warning,
            });
            return;
        }

        var valid_vendors = Service.Data.GetSubrowExcelSheet<GilShopItem>().Flatten().Where(i => i.Item.RowId == (uint)gameItem.Id).ToList();
        if (valid_vendors is { Count: > 0 })
        {
            gameItem.VendorSelling = gameItem.InGame.PriceMid;
        }

        await CheckGameItemAsync(request, gameItem, requestedScope, requireCurrentDetails: false);
    }

    public void DoCheckRefreshAsync(MarketItem gameItem)
    {
        var requestedScope = P.MainWindow.GetSelectedMarketScopeLabel();
        var requestKey = new MarketRefreshRequestKey(gameItem.Id, requestedScope, RequireCurrentDetails: true);
        var request = requestCoordinator.TryBeginCoalesced(requestKey);
        if (request is null)
        {
            Service.Log.Debug($"[MarketRefresh] Refresh already active: {gameItem.Id} on {requestedScope}.");
            return;
        }

        Service.Log.Debug($"[MarketRefresh] Start refresh: {gameItem.Id} on {requestedScope}");

        Task.Run(async () =>
        {
            try
            {
                Interlocked.Increment(ref P.MainWindow.LoadingQueue);
                await CheckGameItemAsync(request, gameItem, requestedScope, requireCurrentDetails: true);
            }
            catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Service.Log.Error($"[MarketRefresh] Refresh failed, {ex.Message}");
                if (requestCoordinator.IsCurrent(request))
                {
                    P.MainWindow.CurrentItemLabel = "Error";
                    P.MainWindow.FailMarketDataRefresh(ex.Message);
                }
            }
            finally
            {
                Interlocked.Decrement(ref P.MainWindow.LoadingQueue);
                requestCoordinator.End(request);
            }
        });
    }


    private async Task CheckGameItemAsync(
        MarketRefreshRequestContext request,
        MarketItem gameItem,
        string requestedScope,
        bool requireCurrentDetails)
    {
        request.Cancellation.Token.ThrowIfCancellationRequested();
        if (!requestCoordinator.IsCurrent(request))
            return;

        P.MainWindow.CurrentItemLabel = gameItem.Name;
        P.MainWindow.CurrentItemIcon = Service.Texture.GetFromGameIcon(new GameIconLookup(gameItem.InGame.Icon))!;
        gameItem.TargetRegion = requestedScope;
        gameItem.FetchTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var vocabulary = MarketRefreshVocabulary.Create(
            P.Config.HolidaySpirit,
            DateOnly.FromDateTime(DateTime.Now));
        P.MainWindow.BeginMarketDataRefresh(
            gameItem.Name,
            vocabulary.Preparing(gameItem.Name));
        var universalisResponse = await P.Universalis.GetDataAsync(
            gameItem,
            request.Cancellation.Token,
            progress =>
            {
                if (requestCoordinator.IsCurrent(request))
                    P.MainWindow.UpdateMarketDataRefresh(progress.StatusText, progress.Progress);
            },
            requireCurrentDetails,
            vocabulary);
        request.Cancellation.Token.ThrowIfCancellationRequested();
        if (!requestCoordinator.IsCurrent(request))
            return;

        P.MainWindow.UpdateMarketDataRefresh(
            vocabulary.Processing(gameItem.Name),
            0.90f);

        if (universalisResponse.Status != UniversalisResponseStatus.Success)
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
            return;
        }

        gameItem.UniversalisResponse = universalisResponse;
        gameItem.WorldOutOfDate = universalisResponse.WorldOutOfDate;
        gameItem.AvgPrice = universalisResponse.AveragePrice;

        if (P.Config.EnableChatLog) SendChatMessage(gameItem);
        if (P.Config.EnableToastLog) SendToast(gameItem);

        P.MainWindow.CurrentItemUpdate(gameItem);
        SearchHistoryUpdate(gameItem);
        var duplicateLimited = universalisResponse.ListingCoverage == MarketListingCoverageStatus.DuplicateLimited;
        var completionText = universalisResponse.DeferredWorlds.Count > 0 || duplicateLimited
            ? vocabulary.PartiallyConfirmed(
                gameItem.Name,
                universalisResponse.DeferredWorlds.Keys.ToArray(),
                universalisResponse.Listings.Count,
                universalisResponse.RequestedListingCount,
                duplicateLimited)
            : vocabulary.Confirmed(gameItem.Name);
        P.MainWindow.CompleteMarketDataRefresh(
            gameItem.Name,
            completionText);
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
        GameItemCacheList.Insert(0, gameItem);
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
