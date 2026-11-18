using System.Text.Json;
using System.Threading;
using ComplicatedMarketBoard.Integrations.Universalis;
using ComplicatedMarketBoard.Market;
using Dalamud.Plugin.Ipc;

namespace ComplicatedMarketBoard.Integrations;

internal sealed class CompetitionIpcProvider : IDisposable
{
    private const string Prefix = "ComplicatedMarketBoard.v1.";
    private const int FreshMs = 5 * 60_000;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Item(uint ItemId, bool Hq);
    private sealed record Request(string Schema, string ScopeKey, Item[] Items, bool Force);
    private sealed record Scope(string Key, string Label, string Kind);
    private sealed record Listing(long PricePerUnit, long Quantity, bool Hq, string WorldName, string RetainerName);
    private sealed record Row(uint ItemId, bool Hq, string Status, string? Reason, long FetchedUtcMs,
        long LastUploadUtcMs, bool Truncated, Listing[] Listings);
    private sealed class Slot(string identity, Row row)
    {
        public string Identity { get; } = identity;
        public Row Row = row;
        public long RetryAt;
        public bool Busy;
    }
    // Only the framework thread reads or writes cache state, including publication.
    private readonly Dictionary<(string, Item), Slot> cache = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Scope> standard = P.MainWindow.ScopeCatalog.AllScopes
        .Where(s => s.Kind is MarketScopeKind.Region or MarketScopeKind.DataCenter or MarketScopeKind.World)
        .Select(s => new Scope($"{s.Kind.ToString().ToLowerInvariant()}:{P.MainWindow.ScopeCatalog.NormalizeForUniversalis(s.Name)}",
            s.DisplayName, s.Kind.ToString().ToLowerInvariant())).ToDictionary(s => s.Key);
    private readonly ICallGateProvider<uint, string> open = Service.PluginInterface.GetIpcProvider<uint, string>(Prefix + "OpenItem");
    private readonly ICallGateProvider<string> scopes = Service.PluginInterface.GetIpcProvider<string>(Prefix + "GetMarketScopes");
    private readonly ICallGateProvider<string, string> competition = Service.PluginInterface.GetIpcProvider<string, string>(Prefix + "GetCompetition");
    private readonly ICallGateProvider<string, uint[], object> changed = Service.PluginInterface.GetIpcProvider<string, uint[], object>(Prefix + "CompetitionChanged");

    public CompetitionIpcProvider()
    {
        open.RegisterFunc(Open);
        scopes.RegisterFunc(() => OnFramework(GetScopes));
        competition.RegisterFunc(json => OnFramework(() => GetCompetition(json)));
    }

    private static T OnFramework<T>(Func<T> action) => Service.Framework.IsInFrameworkUpdateThread
        ? action() : Service.Framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();

    private static string Open(uint itemId)
    {
        bool opened = false;
        try { opened = OnFramework(() => P.IsInGame && P.MainWindow.OpenSearchItem(itemId)); }
        catch (Exception ex) { Service.Log.Warning($"[CMB IPC] OpenItem {itemId}: {ex.Message}"); }
        return JsonSerializer.Serialize(new { schema = "complicated-market-board-open-item-response/v1", itemId, opened });
    }

    private string GetScopes()
    {
        var region = P.IsInGame ? P.MainWindow.ScopeCatalog.NormalizeForUniversalis(
            Service.PlayerState.HomeWorld.Value.DataCenter.Value.Region.Value.Name.ToString()) : "";
        var key = $"region:{region}";
        return JsonSerializer.Serialize(new { schema = "cmb-market-scopes/v1", defaultScopeKey = standard.ContainsKey(key) ? key : "",
            scopes = standard.Values.Concat(P.Config.CustomMarketScopes.Select(s => new Scope("custom:" + s.Id, s.Name, "custom"))) }, Json);
    }

    private string GetCompetition(string json)
    {
        Request? request = null;
        try { request = JsonSerializer.Deserialize<Request>(json, Json); }
        catch (JsonException ex) { Service.Log.Warning($"[CMB IPC] Invalid competition request: {ex.Message}"); }
        var key = request?.ScopeKey ?? "";
        var catalog = P.MainWindow.ScopeCatalog;
        standard.TryGetValue(key, out var scope);
        var custom = scope == null ? P.Config.CustomMarketScopes.Find(s => "custom:" + s.Id == key) : null;
        var label = scope?.Label ?? custom?.Name ?? key;
        var current = P.IsInGame ? P.MainWindow.GetCurrentWorldScopeName() : "";
        var targets = scope != null ? new List<string> { scope.Label } :
            custom == null || !P.IsInGame ? [] : catalog.BuildQueryTargets(custom.IncludedScopes, current);
        var invalidMember = custom?.IncludedScopes.FirstOrDefault(name =>
            catalog.CanonicalizeName(name) is not { } canonical ||
            (canonical == MarketScopeCatalog.CurrentWorldScopeName && catalog.CanonicalizeName(current) == null));
        var error = request?.Schema != "cmb-competition-request/v1" ? "Unknown competition request schema." :
            !P.IsInGame ? "Not logged in." : scope == null && custom == null ? "Unknown market scope." :
            invalidMember != null ? $"Unresolved market scope member: {invalidMember}." :
            targets.Count == 0 ? "Scope has no market targets." : null;
        var limit = Math.Clamp(P.Config.UniversalisListings, 1, 100);
        var identity = $"{error}|{limit}|{string.Join("|", targets)}";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        List<(Item Item, Slot Slot)> work = [];
        List<Row> items = [];
        foreach (var item in (request?.Items ?? []).OfType<Item>().Distinct())
        {
            var reason = error ?? (item.ItemId == 0 ? "Invalid item ID." : null);
            if (!cache.TryGetValue((key, item), out var slot) || slot.Identity != identity)
            {
                slot = cache[(key, item)] = new(identity, new(item.ItemId, item.Hq, reason == null ? "pending" : "unavailable",
                    reason, 0, 0, false, []));
                if (reason != null) Warn(key, item.ItemId, reason);
            }
            var row = slot.Row;
            if (row.Status == "fresh" && now - row.FetchedUtcMs >= FreshMs)
                row = row with { Status = "stale" };
            items.Add(row);
            if (reason == null && !slot.Busy && (request!.Force || (row.Status != "fresh" && now >= slot.RetryAt)))
            {
                slot.Busy = true;
                work.Add((item, slot));
            }
        }
        if (work.Count > 0)
        {
            var token = lifetime.Token;
            var queryTargets = targets.Select(t => (Name: catalog.NormalizeForUniversalis(t),
                World: catalog.TryGetScope(t, out var option) && option.Kind == MarketScopeKind.World ? t : null)).ToArray();
            foreach (var group in work.GroupBy(w => w.Item.Hq))
                foreach (var batch in group.Chunk(100))
                    _ = Task.Run(() => Refresh(key, batch, queryTargets, limit, token), token);
        }
        if (cache.Count > 2048)
        {
            foreach (var old in cache.Where(p => !p.Value.Busy).OrderBy(p => p.Value.Row.FetchedUtcMs)
                         .Take(cache.Count - 2048).Select(p => p.Key).ToArray())
                cache.Remove(old);
        }
        return JsonSerializer.Serialize(new { schema = "cmb-competition/v1", scopeKey = key, scopeLabel = label, items }, Json);
    }

    private async Task Refresh(string key, (Item Item, Slot Slot)[] batch, (string Name, string? World)[] targets,
        int limit, CancellationToken token)
    {
        var ids = batch.Select(w => w.Item.ItemId).ToArray();
        var hq = batch[0].Item.Hq;
        var results = new Dictionary<uint, Row>();
        var errors = new Dictionary<uint, string>();
        try
        {
            var partitions = new List<(string? World, Dictionary<uint, MarketDataCurrent> Items)>();
            foreach (var target in targets)
                partitions.Add((target.World, await P.Universalis.GetCompetitionBatchAsync(target.Name, ids, hq, limit + 1, token)));
            foreach (var id in ids)
            {
                try
                {
                    var rows = new List<Listing>();
                    long upload = 0;
                    foreach (var part in partitions)
                    {
                        if (!part.Items.TryGetValue(id, out var data))
                            throw new InvalidOperationException("Universalis has no data for this item in the requested scope.");
                        upload = Math.Max(upload, data.LastUploadTime);
                        foreach (var l in data.Listings)
                        {
                            var world = string.IsNullOrWhiteSpace(l.WorldName) ? part.World : l.WorldName;
                            if (world == null || string.IsNullOrWhiteSpace(l.RetainerName) || l.Hq != hq || l.PricePerUnit <= 0 || l.Quantity <= 0)
                                throw new InvalidOperationException("Universalis returned an incomplete or mismatched listing.");
                            rows.Add(new(l.PricePerUnit, l.Quantity, hq, world, l.RetainerName));
                        }
                    }
                    results[id] = new(id, hq, "fresh", null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), upload,
                        rows.Count > limit, rows.OrderBy(l => l.PricePerUnit).Take(limit).ToArray());
                }
                catch (Exception ex) { errors[id] = ex.Message; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) { foreach (var id in ids) errors[id] = ex.Message; }

        if (token.IsCancellationRequested) return;
        await Service.Framework.RunOnFrameworkThread(() =>
        {
            if (token.IsCancellationRequested) return;
            List<uint> landed = [];
            foreach (var (item, slot) in batch)
            {
                if (cache.GetValueOrDefault((key, item)) != slot) continue;
                var id = item.ItemId;
                slot.Busy = false;
                if (errors.TryGetValue(id, out var reason))
                {
                    Warn(key, id, reason);
                    slot.Row = slot.Row with { Status = slot.Row.FetchedUtcMs == 0 ? "unavailable" : "stale", Reason = reason };
                    slot.RetryAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 30_000;
                }
                else slot.Row = results[id];
                landed.Add(id);
            }
            if (landed.Count == 0) return;
            try { changed.SendMessage(key, landed.ToArray()); }
            catch (Exception ex) { Service.Log.Warning($"[CMB IPC] CompetitionChanged {key}: {ex.Message}"); }
        });
    }

    private static void Warn(string scope, uint id, string reason) =>
        Service.Log.Warning($"[CMB IPC] Competition {scope}, item {id}: {reason}");

    public void Dispose()
    {
        lifetime.Cancel();
        competition.UnregisterFunc();
        scopes.UnregisterFunc();
        open.UnregisterFunc();
        lifetime.Dispose();
    }
}
