using Dalamud.Bindings.ImGui;
using ComplicatedMarketBoard.Integrations.Universalis;
using Franthropy.Dalamud.UI.Plots;
using Miosuke.Configuration;

namespace ComplicatedMarketBoard.Windows;

public partial class MainWindow
{
    private readonly DalamudPlotRenderer plotRenderer = new();

    private static readonly PlotColor NqSeriesColor = new(.45f, .62f, .92f);
    private static readonly PlotColor HqSeriesColor = new(.92f, .72f, .30f);
    private static readonly PlotColor VolumeSeriesColor = new(.55f, .75f, .55f);
    private static readonly PlotColor RuleColor = new(.85f, .85f, .85f, .6f);

    private static readonly PlotAxis TimeAxis = new("date", null, 6, value =>
        DateTimeOffset.FromUnixTimeSeconds((long)value).ToLocalTime().ToString("MMM d"));
    private static readonly PlotAxis GilAxis = new("gil", "p", 5, value => value.ToString("N0"));
    private static readonly PlotAxis UnitAxis = new("units", null, 4, value => value.ToString("N0"));

    private bool chartsTabActive;
    private MarketChartSnapshot? chartSnapshot;

    private void DrawChartsToggleButton()
    {
        if (P.Config.ChartsDetached)
            return;
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{(char)FontAwesomeIcon.ChartLine}", new Vector2(P.Config.ButtonSizeOffset[0], ImGui.GetItemRectSize().Y)))
            chartsTabActive = !chartsTabActive;
        ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(chartsTabActive ? "Show market data" : "Show charts");
    }

    private void DrawChartsTab()
    {
        if (ImGui.SmallButton("Detach charts"))
        {
            P.Config.ChartsDetached = true;
            P.Config.Save();
            P.ChartsWindow.IsOpen = true;
            return;
        }
        ImGui.Separator();
        DrawCharts();
    }

    public void DrawCharts()
    {
        if (chartSnapshot is null)
        {
            ImGui.TextDisabled("Not enough sale history to chart.");
            return;
        }

        if (chartSnapshot.Price is { } price)
        {
            plotRenderer.Draw("PriceHistory", price, new Vector2(0, 0.55f * ImGui.GetContentRegionAvail().Y));
            ImGui.TextDisabled("Price per unit - NQ blue, HQ gold, rule = current cheapest");
        }
        if (chartSnapshot.Volume is { } volume)
        {
            plotRenderer.Draw("VolumeHistory", volume, new Vector2(0, 0.5f * ImGui.GetContentRegionAvail().Y));
            ImGui.TextDisabled("Units moved per sale");
        }
    }

    private void RebuildChartSnapshot()
    {
        var entries = CurrentItem.UniversalisResponse.Entries;
        if (entries.Count < 2)
        {
            chartSnapshot = null;
            return;
        }

        var ordered = entries.OrderBy(entry => entry.Timestamp).ToArray();
        var xDomain = new PlotRange(ordered.First().Timestamp, ordered.Last().Timestamp);
        if (xDomain.Maximum <= xDomain.Minimum)
            xDomain = new(xDomain.Minimum, xDomain.Minimum + 1);

        chartSnapshot = new(
            BuildPriceChart(ordered, xDomain),
            BuildVolumeChart(ordered, xDomain));
    }

    private PlotSpec? BuildPriceChart(MarketDataEntry[] ordered, PlotRange xDomain)
    {
        var nq = ordered.Where(entry => !entry.Hq).Select(ToDatum).ToArray();
        var hq = ordered.Where(entry => entry.Hq).Select(ToDatum).ToArray();
        var maxPrice = ordered.Max(entry => (double)entry.PricePerUnit);
        if (maxPrice <= 0)
            return null;

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

        return new PlotSpec(
            "cmb-price",
            xDomain,
            new PlotRange(0, maxPrice * 1.05),
            TimeAxis,
            GilAxis,
            layers);
    }

    private static PlotSpec? BuildVolumeChart(MarketDataEntry[] ordered, PlotRange xDomain)
    {
        var maxVolume = ordered.Max(entry => (double)entry.Quantity);
        if (maxVolume <= 0)
            return null;

        return new PlotSpec(
            "cmb-volume",
            xDomain,
            new PlotRange(0, maxVolume * 1.05),
            TimeAxis,
            UnitAxis,
            [
                new PlotStepLayer("volume", ordered.Select(ToVolumeDatum).ToArray(), new PlotLineStyle(VolumeSeriesColor)),
            ]);
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

    private sealed record MarketChartSnapshot(PlotSpec? Price, PlotSpec? Volume);
}
