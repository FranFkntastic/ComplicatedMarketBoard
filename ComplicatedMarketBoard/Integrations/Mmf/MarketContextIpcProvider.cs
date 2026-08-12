using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using ComplicatedMarketBoard.Integrations.Universalis;
using ComplicatedMarketBoard.Market;
using Dalamud.Plugin.Ipc;
using Franthropy.Dalamud.UI.Performance;

namespace ComplicatedMarketBoard.Integrations.Mmf;

public sealed record MarketContextResponse(
    uint ItemId,
    bool HighQuality,
    uint? HomeWorldPrice,
    string? DatacenterBestWorld,
    uint? DatacenterBestPrice,
    double? VelocityPerDay,
    double? TrendAveragePrice,
    long FreshnessUtcMs,
    string Source);

public sealed class MarketContextIpcProvider : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);
    private const int MaximumCachedItems = 256;
    private const string GetMarketContextChannel = "ComplicatedMarketBoard.GetMarketContext";
    private const string GetMarketContextJsonChannel = "ComplicatedMarketBoard.GetMarketContext.v2";
    private const string MarketContextChangedChannel = "ComplicatedMarketBoard.MarketContextChanged";

    private readonly BoundedTtlCache<(uint ItemId, bool Hq), MarketContextResponse?> cache =
        new(MaximumCachedItems, CacheLifetime);
    private readonly ConcurrentDictionary<(uint ItemId, bool Hq), byte> refreshes = new();
    private readonly CancellationTokenSource disposalCancellation = new();
    private readonly ICallGateProvider<uint, bool, MarketContextResponse?> provider;
    private readonly ICallGateProvider<uint, bool, string?> jsonProvider;
    private readonly ICallGateProvider<uint, bool, object> changedProvider;

    public MarketContextIpcProvider()
    {
        provider = Service.PluginInterface
            .GetIpcProvider<uint, bool, MarketContextResponse?>(GetMarketContextChannel);
        jsonProvider = Service.PluginInterface
            .GetIpcProvider<uint, bool, string?>(GetMarketContextJsonChannel);
        changedProvider = Service.PluginInterface
            .GetIpcProvider<uint, bool, object>(MarketContextChangedChannel);
        provider.RegisterFunc(GetMarketContext);
        jsonProvider.RegisterFunc(GetMarketContextJson);
    }

    public void Dispose()
    {
        disposalCancellation.Cancel();
        provider.UnregisterFunc();
        jsonProvider.UnregisterFunc();
        disposalCancellation.Dispose();
    }

    private MarketContextResponse? GetMarketContext(uint itemId, bool highQuality)
    {
        if (itemId == 0 || !P.IsInGame)
            return null;

        var key = (itemId, highQuality);
        var cached = cache.Get(key);
        if (cached is { Found: true, IsFresh: true })
            return cached.Value;

        QueueRefresh(key);
        return cached.Found ? cached.Value : null;
    }

    private string? GetMarketContextJson(uint itemId, bool highQuality)
    {
        var response = GetMarketContext(itemId, highQuality);
        return response is null ? null : JsonSerializer.Serialize(response);
    }

    private void QueueRefresh((uint ItemId, bool Hq) key)
    {
        if (!refreshes.TryAdd(key, 0))
            return;

        _ = Task.Run(() => RefreshAsync(key), disposalCancellation.Token);
    }

    private async Task RefreshAsync((uint ItemId, bool Hq) key)
    {
        try
        {
            var response = await BuildContextAsync(key.ItemId, key.Hq, disposalCancellation.Token);
            cache.Set(key, response);
            PublishChanged(key);
        }
        catch (OperationCanceledException) when (disposalCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            cache.Set(key, null);
            Service.Log.Warning($"[CMB IPC] GetMarketContext refresh failed for {key.ItemId}: {exception.Message}");
            PublishChanged(key);
        }
        finally
        {
            refreshes.TryRemove(key, out _);
        }
    }

    private void PublishChanged((uint ItemId, bool Hq) key)
    {
        try
        {
            changedProvider.SendMessage(key.ItemId, key.Hq);
        }
        catch (Exception exception)
        {
            Service.Log.Verbose(
                $"[CMB IPC] MarketContextChanged had no available subscriber for {key.ItemId}: {exception.Message}");
        }
    }

    private async Task<MarketContextResponse?> BuildContextAsync(
        uint itemId,
        bool highQuality,
        CancellationToken cancellationToken)
    {
        var homeWorldName = ResolveCurrentWorldName();
        if (string.IsNullOrWhiteSpace(homeWorldName))
            return null;

        var itemName = ResolveItemName(itemId);
        var home = await FetchAsync(itemId, itemName, homeWorldName, highQuality, cancellationToken);
        if (highQuality && home is { Listings.Count: 0 })
        {
            highQuality = false;
            home = await FetchAsync(itemId, itemName, homeWorldName, false, cancellationToken);
        }
        if (home is null || home.Status != UniversalisResponseStatus.Success)
            return null;

        var dataCenterName = ResolveDataCenterName();
        UniversalisResponse? datacenter = null;
        if (!string.IsNullOrWhiteSpace(dataCenterName))
            datacenter = await FetchAsync(itemId, itemName, dataCenterName, highQuality, cancellationToken);

        var homePrice = home.Listings.Count > 0 ? (uint)home.Listings.Min(listing => listing.PricePerUnit) : (uint?)null;
        string? dcBestWorld = null;
        uint? dcBestPrice = null;
        if (datacenter is { Status: UniversalisResponseStatus.Success, Listings.Count: > 0 })
        {
            var best = datacenter.Listings.OrderBy(listing => listing.PricePerUnit).First();
            dcBestWorld = best.WorldName;
            dcBestPrice = (uint)best.PricePerUnit;
        }

        var velocity = highQuality ? home.VelocityHq : home.VelocityNq;
        var trend = highQuality ? home.AveragePriceHq : home.AveragePriceNq;

        return new MarketContextResponse(
            itemId,
            highQuality,
            homePrice,
            dcBestWorld,
            dcBestPrice,
            velocity > 0 ? velocity : null,
            trend > 0 ? trend : null,
            home.LatestUploadTime,
            "Universalis");
    }

    private Task<UniversalisResponse> FetchAsync(
        uint itemId,
        string itemName,
        string targetRegion,
        bool highQuality,
        CancellationToken cancellationToken)
    {
        var gameItem = new MarketItem
        {
            Id = itemId,
            Name = itemName,
            TargetRegion = targetRegion,
        };
        return P.Universalis.GetDataForTargetAsync(gameItem, targetRegion, highQuality, cancellationToken);
    }

    private static string ResolveItemName(uint itemId)
    {
        try
        {
            return Assets.Data.ItemSheet.GetRow(itemId).Name.ToString();
        }
        catch
        {
            return $"Item {itemId}";
        }
    }

    private static string? ResolveDataCenterName()
    {
        try
        {
            if (!Service.PlayerState.IsLoaded)
                return null;
            return Service.PlayerState.CurrentWorld.Value.DataCenter.Value.Name.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveCurrentWorldName()
    {
        try
        {
            if (P.Config.OverridePlayerHomeWorld && !string.IsNullOrWhiteSpace(P.Config.PlayerHomeWorld))
                return P.Config.PlayerHomeWorld;
            if (!Service.PlayerState.IsLoaded)
                return null;
            return Service.PlayerState.CurrentWorld.Value.Name.ToString();
        }
        catch
        {
            return null;
        }
    }
}
