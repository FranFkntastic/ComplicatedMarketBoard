using System.Text.Json.Serialization;

namespace ComplicatedMarketBoard.Integrations.Universalis;

public sealed class UniversalisAggregateResponse
{
    [JsonPropertyName("results")]
    public List<UniversalisAggregateItem> Results { get; set; } = [];

    [JsonPropertyName("failedItems")]
    public List<ulong> FailedItems { get; set; } = [];
}

public sealed class UniversalisAggregateItem
{
    [JsonPropertyName("itemId")]
    public ulong ItemId { get; set; }

    [JsonPropertyName("nq")]
    public UniversalisAggregateQuality Nq { get; set; } = new();

    [JsonPropertyName("hq")]
    public UniversalisAggregateQuality Hq { get; set; } = new();

    [JsonPropertyName("worldUploadTimes")]
    public List<UniversalisAggregateWorldUploadTime> WorldUploadTimes { get; set; } = [];
}

public sealed class UniversalisAggregateQuality
{
    [JsonPropertyName("minListing")]
    public UniversalisAggregateMinimums MinListing { get; set; } = new();
}

public sealed class UniversalisAggregateMinimums
{
    [JsonPropertyName("world")]
    public UniversalisAggregateMinimum? World { get; set; }

    [JsonPropertyName("dc")]
    public UniversalisAggregateMinimum? DataCenter { get; set; }

    [JsonPropertyName("region")]
    public UniversalisAggregateMinimum? Region { get; set; }
}

public sealed class UniversalisAggregateMinimum
{
    [JsonPropertyName("price")]
    public long Price { get; set; }

    [JsonPropertyName("worldId")]
    public uint? WorldId { get; set; }
}

public sealed class UniversalisAggregateWorldUploadTime
{
    [JsonPropertyName("worldId")]
    public uint WorldId { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}
