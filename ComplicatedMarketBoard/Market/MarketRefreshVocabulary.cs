using Franthropy.Dalamud.UI.Seasonal;

namespace ComplicatedMarketBoard.Market;

public sealed class MarketRefreshVocabulary
{
    public static MarketRefreshVocabulary Standard { get; } = new(false);

    private MarketRefreshVocabulary(bool festive)
    {
        Festive = festive;
    }

    public bool Festive { get; }

    public static MarketRefreshVocabulary Create(
        HolidaySpiritMode mode,
        DateOnly localDate)
        => HolidaySpirit.IsActive(mode, localDate)
            ? new MarketRefreshVocabulary(true)
            : Standard;

    public string Preparing(string itemName)
        => Festive
            ? $"Preparing the Starlight market list for {itemName}"
            : $"Preparing market refresh for {itemName}";

    public string CheckingLatestPrices(
        string itemName,
        string targetName,
        int attempt,
        int maxAttempts)
        => Festive
            ? $"Consulting the Starlight price ledger for {itemName} in {targetName} (attempt {attempt}/{maxAttempts})"
            : $"Checking latest market prices for {itemName} in {targetName} (attempt {attempt}/{maxAttempts})";

    public string VerifyingWorld(
        string itemName,
        string worldName,
        int worldIndex,
        int worldCount,
        int attempt,
        int maxAttempts)
        => Festive
            ? $"Checking {worldName}'s Starlight ledger for {itemName} ({worldIndex}/{worldCount}, attempt {attempt}/{maxAttempts})"
            : $"Verifying {itemName} listings for {worldName} ({worldIndex}/{worldCount}, attempt {attempt}/{maxAttempts})";

    public string WorldsVerified(string itemName, int completedWorldCount, int worldCount)
        => Festive
            ? $"Checked {completedWorldCount}/{worldCount} Starlight ledgers for {itemName}"
            : $"Verified {completedWorldCount}/{worldCount} worlds for {itemName}";

    public string DownloadingListings(
        string itemName,
        string targetName,
        int attempt,
        int maxAttempts,
        int verificationPass)
    {
        var pass = verificationPass > 0
            ? $", verification pass {verificationPass}"
            : "";
        return Festive
            ? $"Gathering detailed listings from the workshop for {itemName} in {targetName} (attempt {attempt}/{maxAttempts}{pass})"
            : $"Downloading detailed listings for {itemName} in {targetName} (attempt {attempt}/{maxAttempts}{pass})";
    }

    public string WaitingForListings(int nextPass, TimeSpan remaining)
        => Festive
            ? $"Listings are still on the naughty list; checking again (pass {nextPass}, {FormatDelay(remaining)} remaining)"
            : $"Listings are still updating; checking again (pass {nextPass}, {FormatDelay(remaining)} remaining)";

    public string Processing(string itemName)
        => Festive
            ? $"Wrapping up market data for {itemName}"
            : $"Processing verified market data for {itemName}";

    public string Confirmed(string itemName)
        => Festive
            ? $"Latest listings made the nice list for {itemName}"
            : $"Latest listings confirmed for {itemName}";

    public string Failure(string errorText)
        => Festive
            ? $"Starlight market check failed: {errorText}"
            : $"Market refresh failed: {errorText}";

    public string StaleFailure(TimeSpan timeout, string detail)
        => Festive
            ? $"The latest listings stayed on the naughty list for {timeout.TotalSeconds:F0}s. Keeping the previous results. {detail}"
            : $"The latest listings were not available after {timeout.TotalSeconds:F0}s. Keeping the previous results. {detail}";

    public static string GetModeLabel(HolidaySpiritMode mode)
        => mode switch
        {
            HolidaySpiritMode.Seasonal => "Seasonal",
            HolidaySpiritMode.Always => "Always festive",
            HolidaySpiritMode.Off => "Off",
            _ => "Off",
        };

    private static string FormatDelay(TimeSpan delay)
        => $"{Math.Max(0, delay.TotalSeconds):F1}s";
}
