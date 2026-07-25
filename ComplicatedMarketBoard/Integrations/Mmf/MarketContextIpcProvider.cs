using System.Collections.Concurrent;
using System.Threading;
using ComplicatedMarketBoard.Integrations.Universalis;
using ComplicatedMarketBoard.Market;
using Dalamud.Plugin.Ipc;

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

    private readonly ConcurrentDictionary<(uint ItemId, bool Hq), (DateTimeOffset CachedAt, MarketContextResponse? Response)> cache = new();
    private readonly ICallGateProvider<uint, bool, MarketContextResponse?> provider;

    public MarketContextIpcProvider()
    {
        provider = Service.PluginInterface
            .GetIpcProvider<uint, bool, MarketContextResponse?>("ComplicatedMarketBoard.GetMarketContext");
        provider.RegisterFunc(GetMarketContext);
    }

    public void Dispose() => provider.UnregisterFunc();

    private MarketContextResponse? GetMarketContext(uint itemId, bool highQuality)
    {
        if (itemId == 0 || !P.IsInGame)
            return null;

        var key = (itemId, highQuality);
        if (cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheLifetime)
            return cached.Response;

        MarketContextResponse? response;
        try
        {
            response = Task.Run(() => BuildContextAsync(itemId, highQuality)).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Service.Log.Warning($"[CMB IPC] GetMarketContext failed for {itemId}: {exception.Message}");
            response = null;
        }

        cache[key] = (DateTimeOffset.UtcNow, response);
        return response;
    }

    private async Task<MarketContextResponse?> BuildContextAsync(uint itemId, bool highQuality)
    {
        var homeWorldName = P.MainWindow.GetCurrentWorldScopeName();
        if (string.IsNullOrWhiteSpace(homeWorldName))
            return null;

        var itemName = ResolveItemName(itemId);
        var home = await FetchAsync(itemId, itemName, homeWorldName, highQuality);
        if (highQuality && home is { Listings.Count: 0 })
        {
            highQuality = false;
            home = await FetchAsync(itemId, itemName, homeWorldName, false);
        }
        if (home is null || home.Status != UniversalisResponseStatus.Success)
            return null;

        var dataCenterName = ResolveDataCenterName();
        UniversalisResponse? datacenter = null;
        if (!string.IsNullOrWhiteSpace(dataCenterName))
            datacenter = await FetchAsync(itemId, itemName, dataCenterName, highQuality);

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

    private Task<UniversalisResponse> FetchAsync(uint itemId, string itemName, string targetRegion, bool highQuality)
    {
        var gameItem = new MarketItem
        {
            Id = itemId,
            Name = itemName,
            TargetRegion = targetRegion,
        };
        var previousHqOnly = P.Config.UniversalisHqOnly;
        P.Config.UniversalisHqOnly = highQuality;
        try
        {
            return P.Universalis.GetDataAsync(gameItem, CancellationToken.None);
        }
        finally
        {
            P.Config.UniversalisHqOnly = previousHqOnly;
        }
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
}
