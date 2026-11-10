using System.Text.Json;
using ComplicatedMarketBoard.Configuration;
using ComplicatedMarketBoard.Market;

namespace ComplicatedMarketBoard.Tests;

public sealed class MarketRefetchPolicyTests
{
    [Theory]
    [InlineData(ListingRefetchMode.Always, 1, false)]
    [InlineData(ListingRefetchMode.AfterAge, 299999, true)]
    [InlineData(ListingRefetchMode.AfterAge, 300000, false)]
    [InlineData(ListingRefetchMode.Manual, 90000000, true)]
    [InlineData((ListingRefetchMode)99, 1, false)]
    public void RevisitingUsesOriginalFetchAge(ListingRefetchMode mode, long elapsed, bool expected)
        => Assert.Equal(expected, MarketRefetchPolicy.CanReuse(mode, 300, 1000, 1000 + elapsed,
            successful: true, matchingIdentity: true, forceRefresh: false));

    [Theory]
    [InlineData(true, true, true, 1000, 2000)]
    [InlineData(false, true, false, 1000, 2000)]
    [InlineData(true, false, false, 1000, 2000)]
    [InlineData(true, true, false, 0, 2000)]
    [InlineData(true, true, false, 3000, 2000)]
    public void ManualStillFetchesForExplicitRefreshOrUnusableEvidence(bool success, bool match, bool force, long fetched, long now)
        => Assert.False(MarketRefetchPolicy.CanReuse(ListingRefetchMode.Manual, 300, fetched, now, success, match, force));

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(300, 300)]
    [InlineData(int.MaxValue, 86400)]
    public void AgeIsBounded(int input, int expected)
        => Assert.Equal(expected, MarketRefetchPolicy.NormalizeAge(input));

    [Theory]
    [InlineData("{}", ListingRefetchMode.Always, 300)]
    [InlineData("{\"ListingRefetch\":1,\"ListingRefetchAgeSeconds\":45}", ListingRefetchMode.AfterAge, 45)]
    [InlineData("{\"ListingRefetch\":2}", ListingRefetchMode.Manual, 300)]
    public void PreferencesRoundTripAndOldConfigPreservesBehavior(string json, ListingRefetchMode mode, int age)
    {
        var options = new JsonSerializerOptions { IncludeFields = true };
        var config = JsonSerializer.Deserialize<ComplicatedMarketBoardConfig>(json, options)!;
        var reloaded = JsonSerializer.Deserialize<ComplicatedMarketBoardConfig>(JsonSerializer.Serialize(config, options), options)!;
        Assert.Equal(mode, reloaded.ListingRefetch);
        Assert.Equal(age, reloaded.ListingRefetchAgeSeconds);
    }

    [Fact]
    public void IdentityIncludesResolvedWorldsCustomScopeQualityAndBothLimits()
    {
        var original = MarketRefetchPolicy.RequestIdentity("Scope", "custom-a", ["Siren", "Faerie"], false, 70, 70);
        Assert.Equal(original, MarketRefetchPolicy.RequestIdentity("Scope", "custom-a", ["Faerie", "Siren"], false, 70, 70));
        Assert.NotEqual(original, MarketRefetchPolicy.RequestIdentity("Other", "custom-a", ["Siren", "Faerie"], false, 70, 70));
        Assert.NotEqual(original, MarketRefetchPolicy.RequestIdentity("Scope", "custom-b", ["Siren", "Faerie"], false, 70, 70));
        Assert.NotEqual(original, MarketRefetchPolicy.RequestIdentity("Scope", "custom-a", ["Siren"], false, 70, 70));
        Assert.NotEqual(original, MarketRefetchPolicy.RequestIdentity("Scope", "custom-a", ["Siren", "Faerie"], true, 70, 70));
        Assert.NotEqual(original, MarketRefetchPolicy.RequestIdentity("Scope", "custom-a", ["Siren", "Faerie"], false, 71, 70));
        Assert.NotEqual(original, MarketRefetchPolicy.RequestIdentity("Scope", "custom-a", ["Siren", "Faerie"], false, 70, 71));
    }
}
