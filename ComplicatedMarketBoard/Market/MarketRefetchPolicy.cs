namespace ComplicatedMarketBoard.Market;

public enum ListingRefetchMode
{
    Always,
    AfterAge,
    Manual,
}

/// <summary>Navigation may reuse an observation; it never renews its fetch timestamp.</summary>
public static class MarketRefetchPolicy
{
    public const int DefaultAgeSeconds = 300;
    public const int MaximumAgeSeconds = 86400;

    public static int NormalizeAge(int seconds) => Math.Clamp(seconds, 1, MaximumAgeSeconds);

    public static string RequestIdentity(string scope, string? customId, IEnumerable<string> worlds,
        bool hqOnly, int listings, int entries)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            Scope = scope,
            CustomId = customId,
            Worlds = worlds.OrderBy(world => world, StringComparer.OrdinalIgnoreCase).ToArray(),
            HqOnly = hqOnly,
            Listings = listings,
            Entries = entries,
        });

    public static bool CanReuse(ListingRefetchMode mode, int ageSeconds, long fetchedAt, long now,
        bool successful, bool matchingIdentity, bool forceRefresh)
    {
        if (forceRefresh || !successful || !matchingIdentity || fetchedAt <= 0 || now < fetchedAt)
            return false;

        return mode switch
        {
            ListingRefetchMode.AfterAge => now - fetchedAt < (long)NormalizeAge(ageSeconds) * 1000,
            ListingRefetchMode.Manual => true,
            _ => false,
        };
    }
}
