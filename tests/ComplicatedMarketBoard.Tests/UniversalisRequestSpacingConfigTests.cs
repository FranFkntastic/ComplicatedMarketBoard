using System.Text.Json;
using ComplicatedMarketBoard.Configuration;

namespace ComplicatedMarketBoard.Tests;

public sealed class UniversalisRequestSpacingConfigTests
{
    [Theory]
    [InlineData("{}", 200, false)]
    [InlineData("{\"UniversalisRequestSpacingMilliseconds\":100}", 100, false)]
    [InlineData("{\"UniversalisRequestSpacingMilliseconds\":50}", 50, false)]
    [InlineData("{\"UniversalisRequestSpacingMilliseconds\":1000}", 1000, false)]
    [InlineData("{\"UniversalisRequestSpacingMilliseconds\":0}", 50, true)]
    [InlineData("{\"UniversalisRequestSpacingMilliseconds\":-1}", 50, true)]
    [InlineData("{\"UniversalisRequestSpacingMilliseconds\":2000}", 1000, true)]
    public void ConfigDefaultsBoundsAndPersistenceAgree(string json, int expected, bool changed)
    {
        var options = new JsonSerializerOptions { IncludeFields = true };
        var config = JsonSerializer.Deserialize<ComplicatedMarketBoardConfig>(json, options)!;
        Assert.Equal(changed, config.NormalizeUniversalisRequestSpacing());
        Assert.Equal(expected, config.UniversalisRequestSpacingMilliseconds);
        Assert.False(config.NormalizeUniversalisRequestSpacing());
        var reloaded = JsonSerializer.Deserialize<ComplicatedMarketBoardConfig>(JsonSerializer.Serialize(config, options), options)!;
        Assert.Equal(expected, reloaded.UniversalisRequestSpacingMilliseconds);
    }
}
