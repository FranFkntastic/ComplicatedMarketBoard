using System.Globalization;
using ComplicatedMarketBoard.Integrations.Universalis;
using Franthropy.Dalamud.UI.Styling;
using Miosuke.UiHelper;

namespace ComplicatedMarketBoard.Windows;

public partial class MainWindow
{
    private static readonly DalamudUiTheme MarketStatusTheme =
        DalamudUiTheme.Dark(Ui.ColourCyan) with
        {
            Palette = DalamudUiPalette.Dark(Ui.ColourCyan) with
            {
                Error = Ui.ColourCrimson,
                Text = Ui.ColourWhite3,
            },
        };

    private void DrawMarketDataStatusBar(Vector2 spacing)
    {
        if (CurrentItem.Id == 0)
            return;

        ImGui.Spacing();
        var tone = GetMarketRefreshStatusTone();
        var statusHeight = Math.Max(
            ImGui.GetFrameHeight() + spacing.Y,
            ImGui.GetFrameHeight() + (MarketStatusTheme.Metrics.WindowPadding.Y * 2f));
        DalamudUiChrome.DrawStatusBand(
            "market data refresh status",
            MarketStatusTheme.Palette,
            tone,
            () =>
            {
                if (RefreshInProgress)
                {
                    var elapsed = FormatDuration(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - RefreshStartedAt);
                    ImGui.ProgressBar(
                        RefreshProgress,
                        new Vector2(-1, ImGui.GetFrameHeight()),
                        $"{RefreshStatusText}... {elapsed}");
                    return;
                }

                DalamudUiChrome.DrawBadge(GetMarketRefreshStatusLabel(), MarketStatusTheme.Palette, tone);
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(GetMarketRefreshStatusColour(), GetMarketRefreshStatusText());
            },
            statusHeight);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(GetMarketDataStatusTooltip());
    }

    private DalamudUiTone GetMarketRefreshStatusTone()
    {
        if (!string.IsNullOrWhiteSpace(RefreshErrorText) ||
            CurrentItem.UniversalisResponse.Status != UniversalisResponseStatus.Success)
            return DalamudUiTone.Error;
        return CurrentItem.UniversalisResponse.FetchTime == 0
            ? DalamudUiTone.Neutral
            : DalamudUiTone.Accent;
    }

    private string GetMarketRefreshStatusLabel()
    {
        if (!string.IsNullOrWhiteSpace(RefreshErrorText) ||
            CurrentItem.UniversalisResponse.Status != UniversalisResponseStatus.Success)
            return "ERROR";
        return CurrentItem.UniversalisResponse.FetchTime == 0 ? "IDLE" : "READY";
    }

    private Vector4 GetMarketRefreshStatusColour()
    {
        if (!string.IsNullOrWhiteSpace(RefreshErrorText))
            return Ui.ColourCrimson;

        var response = CurrentItem.UniversalisResponse;
        if (response.Status != UniversalisResponseStatus.Success)
            return Ui.ColourCrimson;

        if (response.FetchTime == 0)
            return Ui.ColourWhite3;

        return Ui.ColourCyan;
    }

    private string GetMarketRefreshStatusText()
    {
        if (!string.IsNullOrWhiteSpace(RefreshErrorText))
            return RefreshStatusText;

        var response = CurrentItem.UniversalisResponse;
        if (response.Status != UniversalisResponseStatus.Success)
            return $"Market refresh failed: {GetUniversalisStatusLabel(response.Status)}";

        if (response.FetchTime == 0)
            return "Market data not loaded";

        var fetchedAgo = FormatDuration(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - response.FetchTime);
        var summary = string.IsNullOrWhiteSpace(RefreshStatusText)
            ? "Market data refreshed"
            : RefreshStatusText;
        return $"{summary} · {fetchedAgo} ago";
    }

    private string GetMarketDataStatusTooltip()
    {
        var response = CurrentItem.UniversalisResponse;
        if (RefreshInProgress)
        {
            var elapsed = RefreshStartedAt > 0
                ? FormatDuration(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - RefreshStartedAt)
                : "unknown";

            if (response.WorldOutOfDate.Count == 0)
                return $"{RefreshStatusText}\nElapsed: {elapsed}";

            return $"{RefreshStatusText}\nElapsed: {elapsed}\n\n{GetMarketFreshnessTooltip()}";
        }

        if (!string.IsNullOrWhiteSpace(RefreshErrorText))
        {
            if (response.FetchTime == 0)
                return $"Last refresh failed: {RefreshErrorText}";

            return $"Last refresh failed: {RefreshErrorText}\nShowing data fetched {FormatDuration(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - response.FetchTime)} ago.\n\n{GetMarketFreshnessTooltip()}";
        }

        if (response.Status != UniversalisResponseStatus.Success)
            return $"Universalis status: {GetUniversalisStatusLabel(response.Status)}.";

        if (response.WorldOutOfDate.Count == 0)
            return "No freshness data was returned for this item.";

        return GetMarketFreshnessTooltip();
    }

    private string GetMarketFreshnessTooltip()
        => cachedMarketFreshnessTooltip;

    private string BuildMarketFreshnessTooltip()
    {
        var response = CurrentItem.UniversalisResponse;
        if (response.WorldOutOfDate.Count == 0)
            return "No freshness data was returned for this item.";

        var freshness = response.WorldOutOfDate.OrderByDescending(w => w.Value).ToList();
        var newest = freshness.MinBy(w => w.Value);
        var oldest = freshness.MaxBy(w => w.Value);
        var min = freshness.Min(w => w.Value);
        var avg = freshness.Average(w => w.Value);
        var max = freshness.Max(w => w.Value);
        var fetchedAt = response.FetchTime > 0
            ? FormatTimestamp(response.FetchTime)
            : "unknown";
        var newestUpload = response.LatestUploadTime > 0
            ? FormatTimestamp(response.LatestUploadTime)
            : "unknown";

        return
            $"Fetched: {fetchedAt}\n" +
            $"Newest upload: {newestUpload}\n" +
            $"Freshness: {min:F2} / {avg:F2} / {max:F2} hrs min/avg/max\n" +
            $"Freshest market: {newest.Key} ({newest.Value:F2} hrs)\n" +
            $"Stalest market: {oldest.Key} ({oldest.Value:F2} hrs)\n" +
            $"Worlds: {response.WorldOutOfDate.Count}\n" +
            $"Listings: {response.Listings.Count}\n" +
            $"Recent sales: {response.Entries.Count}";
    }

    private static string GetUniversalisStatusLabel(ulong status) => status switch
    {
        UniversalisResponseStatus.Success => "ok",
        UniversalisResponseStatus.ServerError => "server error",
        UniversalisResponseStatus.InvalidData => "invalid data",
        UniversalisResponseStatus.UserCancellation => "request timed out",
        UniversalisResponseStatus.StaleData => "current listing details unavailable",
        UniversalisResponseStatus.UnknownError => "unknown error",
        _ => $"status {status}",
    };

    private static string FormatDuration(long milliseconds)
    {
        if (milliseconds < 0)
            milliseconds = 0;

        var duration = TimeSpan.FromMilliseconds(milliseconds);
        if (duration.TotalMinutes < 1)
            return $"{duration.TotalSeconds:F1}s";
        if (duration.TotalHours < 1)
            return $"{duration.TotalMinutes:F1}m";
        if (duration.TotalDays < 1)
            return $"{duration.TotalHours:F2}h";
        return $"{duration.TotalDays:F2}d";
    }

    private static string FormatTimestamp(long unixMilliseconds)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
}
