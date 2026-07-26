using Dalamud.Bindings.ImGui;
using ComplicatedMarketBoard.Integrations.Universalis;
using Franthropy.Dalamud.UI.Plots;

namespace ComplicatedMarketBoard.Windows;

public partial class MainWindow
{
    private const float ChartsHeight = 250f;

    private readonly DalamudPlotRenderer plotRenderer = new();

    private static readonly PlotColor NqSeriesColor = new(.45f, .62f, .92f);
    private static readonly PlotColor HqSeriesColor = new(.92f, .72f, .30f);
    private static readonly PlotColor VolumeSeriesColor = new(.55f, .75f, .55f);
    private static readonly PlotColor RuleColor = new(.85f, .85f, .85f, .6f);

    private static readonly PlotAxis TimeAxis = new("date", null, 6, value =>
        DateTimeOffset.FromUnixTimeSeconds((long)value).ToLocalTime().ToString("MMM d"));
    private static readonly PlotAxis GilAxis = new("gil", "p", 5, value => value.ToString("N0"));
    private static readonly PlotAxis UnitAxis = new("units", null, 4, value => value.ToString("N0"));

    private void DrawCharts()
    {
        var entries = CurrentItem.UniversalisResponse.Entries;
        if (entries.Count < 2)
        {
            if (ImGui.CollapsingHeader("Charts##cmb-charts"))
                ImGui.TextDisabled("Not enough sale history to chart.");
            return;
        }

        if (!ImGui.CollapsingHeader("Charts##cmb-charts"))
            return;

        var ordered = entries.OrderBy(entry => entry.Timestamp).ToArray();
        var xDomain = new PlotRange(ordered.First().Timestamp, ordered.Last().Timestamp);
        if (xDomain.Maximum <= xDomain.Minimum)
            xDomain = new(xDomain.Minimum, xDomain.Minimum + 1);

        DrawPriceChart(ordered, xDomain);
        DrawVolumeChart(ordered, xDomain);
    }

    private void DrawPriceChart(MarketDataEntry[] ordered, PlotRange xDomain)
    {
        var nq = ordered.Where(entry => !entry.Hq).Select(ToDatum).ToArray();
        var hq = ordered.Where(entry => entry.Hq).Select(ToDatum).ToArray();
        var maxPrice = ordered.Max(entry => (double)entry.PricePerUnit);
        if (maxPrice <= 0)
            return;

        var layers = new List<IPlotLayer>();
        if (nq.Length > 1)
            layers.Add(new PlotPolylineLayer("price-nq", nq, new PlotLineStyle(NqSeriesColor)));
        if (hq.Length > 1)
            layers.Add(new PlotPolylineLayer("price-hq", hq, new PlotLineStyle(HqSeriesColor)));
        var cheapest = CurrentItem.UniversalisResponse.Listings.Count > 0
            ? CurrentItem.UniversalisResponse.Listings.Min(listing => listing.PricePerUnit)
            : 0;
        if (cheapest > 0)
            layers.Add(new PlotRuleLayer("price-now", PlotRuleOrientation.Horizontal, cheapest, new PlotLineStyle(RuleColor), "now"));

        var spec = new PlotSpec(
            "cmb-price",
            xDomain,
            new PlotRange(0, maxPrice * 1.05),
            TimeAxis,
            GilAxis,
            layers);
        plotRenderer.Draw("PriceHistory", spec, new System.Numerics.Vector2(0, 140));
        ImGui.TextDisabled("Price per unit — NQ blue, HQ gold, rule = current cheapest");
    }

    private void DrawVolumeChart(MarketDataEntry[] ordered, PlotRange xDomain)
    {
        var maxVolume = ordered.Max(entry => (double)entry.Quantity);
        if (maxVolume <= 0)
            return;

        var spec = new PlotSpec(
            "cmb-volume",
            xDomain,
            new PlotRange(0, maxVolume * 1.05),
            TimeAxis,
            UnitAxis,
            [
                new PlotStepLayer("volume", ordered.Select(ToVolumeDatum).ToArray(), new PlotLineStyle(VolumeSeriesColor)),
            ]);
        plotRenderer.Draw("VolumeHistory", spec, new System.Numerics.Vector2(0, 90));
        ImGui.TextDisabled("Units moved per sale");
    }

    private static PlotDatum ToDatum(MarketDataEntry entry) =>
        new(
            $"{entry.Timestamp}-{entry.PricePerUnit}-{entry.Quantity}",
            entry.Timestamp,
            entry.PricePerUnit,
            []);

    private static PlotDatum ToVolumeDatum(MarketDataEntry entry) =>
        new(
            $"{entry.Timestamp}-{entry.PricePerUnit}-{entry.Quantity}",
            entry.Timestamp,
            entry.Quantity,
            []);
}
