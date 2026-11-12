using ComplicatedMarketBoard.Integrations.Universalis;
using Lumina.Excel.Sheets;

namespace ComplicatedMarketBoard.Market;

public sealed class MarketItem
{
    public MarketItem Snapshot() => (MarketItem)MemberwiseClone();
    public MarketObservationCache Observations { get; } = new();
    public ulong Id { get; set; }
    public string Name { get; set; } = "";
    public string TargetRegion { get; set; } = "";
    public uint VendorSelling { get; set; }
    public Item InGame { get; set; }
    public ulong FetchTimestamp { get; set; }
    public UniversalisResponse UniversalisResponse { get; set; } = new();
    public Dictionary<string, double> WorldOutOfDate { get; set; } = [];
    public double AvgPrice { get; set; }
    // Identity of the successful response, not of a later failed refresh attempt.
    public string CachedRequestIdentity { get; set; } = "";
}

public enum PriceDisplayMode : uint
{
    UniversalisAverage = 0,
    SellingLow = 1,
    SoldLow = 2,
}
