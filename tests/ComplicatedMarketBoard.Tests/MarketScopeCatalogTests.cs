using ComplicatedMarketBoard.Market;

namespace ComplicatedMarketBoard.Tests;

public sealed class MarketScopeCatalogTests
{
    private static readonly MarketWorldInfo[] Worlds =
    [
        new("Faerie", "Aether", "North-America", 54),
        new("Gilgamesh", "Aether", "North-America", 63),
        new("Leviathan", "Primal", "North-America", 64),
    ];

    [Fact]
    public void BuildQueryTargets_RegionAbsorbsNestedDataCentersAndWorlds()
    {
        var catalog = new MarketScopeCatalog(Worlds);

        var targets = catalog.BuildQueryTargets(["North America", "Aether", "Faerie", "Leviathan"]);

        Assert.Equal(["North America"], targets);
    }

    [Fact]
    public void BuildQueryTargets_DataCenterAbsorbsOnlyItsOwnWorlds()
    {
        var catalog = new MarketScopeCatalog(Worlds);

        var targets = catalog.BuildQueryTargets(["Aether", "Faerie", "Leviathan"]);

        Assert.Equal(["Aether", "Leviathan"], targets);
    }

    [Fact]
    public void BuildQueryTargets_ResolvesCurrentWorldAtRequestTime()
    {
        var catalog = new MarketScopeCatalog(Worlds);

        var targets = catalog.BuildQueryTargets([MarketScopeCatalog.CurrentWorldScopeName, "Aether"], "Leviathan");

        Assert.Equal(["Aether", "Leviathan"], targets);
    }
}
