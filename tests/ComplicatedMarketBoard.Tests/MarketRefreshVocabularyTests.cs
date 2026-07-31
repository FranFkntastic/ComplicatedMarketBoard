using ComplicatedMarketBoard.Market;
using Franthropy.Dalamud.UI.Seasonal;

namespace ComplicatedMarketBoard.Tests;

public sealed class MarketRefreshVocabularyTests
{
    [Fact]
    public void Seasonal_UsesStandardVocabularyBeforeThanksgiving()
    {
        var vocabulary = MarketRefreshVocabulary.Create(
            HolidaySpiritMode.Seasonal,
            new DateOnly(2026, 7, 26));

        Assert.False(vocabulary.Festive);
        Assert.Contains(
            "Checking latest market prices",
            vocabulary.CheckingLatestPrices("Potion", "Faerie", 1, 3));
    }

    [Fact]
    public void Seasonal_UsesStarlightVocabularyAfterThanksgiving()
    {
        var vocabulary = MarketRefreshVocabulary.Create(
            HolidaySpiritMode.Seasonal,
            new DateOnly(2026, 11, 27));

        Assert.True(vocabulary.Festive);
        Assert.Contains(
            "Starlight price ledger",
            vocabulary.CheckingLatestPrices("Potion", "Faerie", 1, 3));
        Assert.Equal(
            "Checked 3/8 Starlight ledgers for Potion",
            vocabulary.WorldsVerified("Potion", 3, 8));
        Assert.Contains(
            "naughty list",
            vocabulary.WaitingForListings(2, TimeSpan.FromSeconds(18)));
    }

    [Fact]
    public void AlwaysAndOff_OverrideTheCalendar()
    {
        Assert.True(MarketRefreshVocabulary.Create(
            HolidaySpiritMode.Always,
            new DateOnly(2026, 7, 26)).Festive);
        Assert.False(MarketRefreshVocabulary.Create(
            HolidaySpiritMode.Off,
            new DateOnly(2026, 12, 24)).Festive);
    }

    [Fact]
    public void Standard_WorldProgressReportsCompletedWork()
    {
        Assert.Equal(
            "Verified 3/8 worlds for Potion",
            MarketRefreshVocabulary.Standard.WorldsVerified("Potion", 3, 8));
    }
}
