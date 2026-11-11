using ComplicatedMarketBoard.Integrations.Universalis;

namespace ComplicatedMarketBoard.Market;

// Item-owned and intentionally ephemeral; history eviction releases all observations.
public sealed class MarketObservationCache
{
    private readonly LinkedList<(string Identity, UniversalisResponse Response)> entries = new();
    public const int Capacity = 4;

    public void Remember(string identity, UniversalisResponse response)
    {
        if (string.IsNullOrEmpty(identity) || response.Status != UniversalisResponseStatus.Success) return;
        var node = entries.First;
        while (node != null)
        {
            var next = node.Next;
            if (node.Value.Identity == identity) entries.Remove(node);
            node = next;
        }
        entries.AddFirst((identity, response));
        if (entries.Count > Capacity) entries.RemoveLast();
    }

    public UniversalisResponse? Find(string identity)
    {
        for (var node = entries.First; node != null; node = node.Next)
        {
            if (node.Value.Identity != identity) continue;
            var response = node.Value.Response;
            entries.Remove(node);
            entries.AddFirst(node);
            return response;
        }
        return null;
    }
}
