using System.Text.Json;
using ComplicatedMarketBoard.Configuration;

namespace ComplicatedMarketBoard.Tests;

public sealed class UniversalisConcurrencyConfigTests
{
    [Theory]
    [InlineData("{}", 3, false)]
    [InlineData("{\"UniversalisMaxConcurrentRequests\":4}", 4, false)]
    [InlineData("{\"UniversalisMaxConcurrentRequests\":0}", 1, true)]
    [InlineData("{\"UniversalisMaxConcurrentRequests\":99}", 8, true)]
    public void ConfigDefaultsBoundsAndPersistenceAgree(string json, int expected, bool changed)
    {
        var options = new JsonSerializerOptions { IncludeFields = true };
        var config = JsonSerializer.Deserialize<ComplicatedMarketBoardConfig>(json, options)!;
        Assert.Equal(changed, config.NormalizeUniversalisConcurrency());
        Assert.Equal(expected, config.UniversalisMaxConcurrentRequests);
        Assert.False(config.NormalizeUniversalisConcurrency());
        var reloaded = JsonSerializer.Deserialize<ComplicatedMarketBoardConfig>(JsonSerializer.Serialize(config, options), options)!;
        Assert.Equal(expected, reloaded.UniversalisMaxConcurrentRequests);
    }
}
