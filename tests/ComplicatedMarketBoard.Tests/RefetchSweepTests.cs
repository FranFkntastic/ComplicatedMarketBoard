using ComplicatedMarketBoard.Configuration;
using ComplicatedMarketBoard.Market;
using ComplicatedMarketBoard.Integrations.Universalis;

namespace ComplicatedMarketBoard.Tests;

public class RefetchSweepTests
{
    [Fact]
    public void MigrationRunsOnceAndPreservesLaterChoice()
    {
        var config = new ComplicatedMarketBoardConfig { ListingRefetch = ListingRefetchMode.Always, ListingRefetchAgeSeconds = 9 };
        Assert.True(config.Migrate());
        Assert.Equal(2, config.Version);
        Assert.Equal(ListingRefetchMode.AfterAge, config.ListingRefetch);
        Assert.Equal(300, config.ListingRefetchAgeSeconds);
        config.ListingRefetch = ListingRefetchMode.Always;
        Assert.False(config.Migrate());
        Assert.Equal(ListingRefetchMode.Always, config.ListingRefetch);
    }

    [Fact]
    public void RetentionIsBoundedLruAndRejectsFailuresOrInvalidIdentity()
    {
        var cache = new MarketObservationCache();
        var response = new UniversalisResponse { Status = UniversalisResponseStatus.Success, FetchTime = 123 };
        foreach (var key in new[] { "a", "b", "c", "d" }) cache.Remember(key, response);
        Assert.Same(response, cache.Find("a"));
        cache.Remember("e", response);
        Assert.Null(cache.Find("b"));
        cache.Remember("a", new UniversalisResponse { Status = UniversalisResponseStatus.ServerError });
        cache.Remember("", response);
        Assert.Null(cache.Find(""));
        Assert.Same(response, cache.Find("a"));
        Assert.Equal(123, response.FetchTime);
    }
}
