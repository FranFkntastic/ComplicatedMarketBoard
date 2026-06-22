using ComplicatedMarketBoard.Assets;
using Lumina.Excel.Sheets;

namespace ComplicatedMarketBoard.Modules;

public enum MarketScopeKind
{
    Region,
    DataCenter,
    World,
    Custom,
    Header,
}

public sealed class CustomMarketScope
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> IncludedScopes { get; set; } = [];
}

public sealed record MarketScopeOption(
    string Name,
    string DisplayName,
    MarketScopeKind Kind,
    string RegionName = "",
    string DataCenterName = "",
    uint WorldId = 0);

public sealed record MarketScopeSelectorRow(
    string Name,
    string DisplayName,
    MarketScopeKind Kind,
    int Indent,
    bool IsAdditional = false,
    bool IsHomeWorld = false,
    string CustomScopeId = "");

public sealed class MarketScopeCatalog
{
    private readonly Dictionary<string, MarketScopeOption> scopesByName;
    private readonly Dictionary<string, string> canonicalNames;

    public MarketScopeCatalog()
    {
        Regions = Data.WorldSheet
            .Where(world => world.IsPublic)
            .Select(world => CanonicalRegionName(world.DataCenter.Value.Region.Value.Name.ToString()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(region => region, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DataCentersByRegion = Data.WorldSheet
            .Where(world => world.IsPublic)
            .Select(world => new
            {
                Region = CanonicalRegionName(world.DataCenter.Value.Region.Value.Name.ToString()),
                DataCenter = world.DataCenter.Value.Name.ToString(),
            })
            .Distinct()
            .GroupBy(item => item.Region)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.DataCenter).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList());

        WorldsByDataCenter = Data.WorldSheet
            .Where(world => world.IsPublic)
            .GroupBy(world => world.DataCenter.Value.Name.ToString())
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(world => world.Name.ToString(), StringComparer.OrdinalIgnoreCase)
                    .Select(world => new MarketScopeOption(
                        world.Name.ToString(),
                        world.Name.ToString(),
                        MarketScopeKind.World,
                        CanonicalRegionName(world.DataCenter.Value.Region.Value.Name.ToString()),
                        world.DataCenter.Value.Name.ToString(),
                        world.RowId))
                    .ToList());

        var options = new List<MarketScopeOption>();
        options.AddRange(Regions.Select(region => new MarketScopeOption(region, region, MarketScopeKind.Region, region)));
        options.AddRange(DataCentersByRegion.SelectMany(region => region.Value.Select(dataCenter => new MarketScopeOption(dataCenter, dataCenter, MarketScopeKind.DataCenter, region.Key, dataCenter))));
        options.AddRange(WorldsByDataCenter.SelectMany(group => group.Value));

        scopesByName = options.ToDictionary(option => option.Name, StringComparer.OrdinalIgnoreCase);
        canonicalNames = options.ToDictionary(option => CanonicalLookupKey(option.Name), option => option.Name, StringComparer.OrdinalIgnoreCase);
        canonicalNames["north-america"] = "North America";
        canonicalNames["north america"] = "North America";
    }

    public List<string> Regions { get; }
    public Dictionary<string, List<string>> DataCentersByRegion { get; }
    public Dictionary<string, List<MarketScopeOption>> WorldsByDataCenter { get; }

    public IReadOnlyCollection<MarketScopeOption> AllScopes => scopesByName.Values;

    public bool TryGetScope(string name, out MarketScopeOption option)
        => scopesByName.TryGetValue(name, out option!);

    public string? CanonicalizeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return null;

        return canonicalNames.TryGetValue(CanonicalLookupKey(trimmed), out var canonicalName)
            ? canonicalName
            : null;
    }

    public bool CanonicalizeConfigList(List<string> scopes)
    {
        var changed = false;
        var canonical = new List<string>();
        foreach (var scope in scopes)
        {
            var canonicalName = CanonicalizeName(scope);
            if (canonicalName is null)
            {
                if (!string.IsNullOrWhiteSpace(scope) && !canonical.Contains(scope, StringComparer.OrdinalIgnoreCase))
                    canonical.Add(scope);
                continue;
            }

            if (!string.Equals(scope, canonicalName, StringComparison.Ordinal))
                changed = true;

            if (!canonical.Contains(canonicalName, StringComparer.OrdinalIgnoreCase))
                canonical.Add(canonicalName);
            else
                changed = true;
        }

        if (!scopes.SequenceEqual(canonical))
        {
            scopes.Clear();
            scopes.AddRange(canonical);
            changed = true;
        }

        return changed;
    }

    public List<string> GetUnknownScopes(IEnumerable<string> scopes)
        => scopes
            .Where(scope => CanonicalizeName(scope) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IEnumerable<MarketScopeOption> GetWorldsInRegion(string regionName)
        => DataCentersByRegion.TryGetValue(regionName, out var dataCenters)
            ? dataCenters.SelectMany(GetWorldsInDataCenter)
            : [];

    public IEnumerable<MarketScopeOption> GetWorldsInDataCenter(string dataCenterName)
        => WorldsByDataCenter.TryGetValue(dataCenterName, out var worlds)
            ? worlds
            : [];

    public List<string> ExpandToWorldNames(IEnumerable<string> scopeNames)
    {
        var worlds = new List<string>();
        foreach (var scopeName in scopeNames)
        {
            var canonicalName = CanonicalizeName(scopeName);
            if (canonicalName is null || !TryGetScope(canonicalName, out var scope))
                continue;

            IEnumerable<MarketScopeOption> scopeWorlds = scope.Kind switch
            {
                MarketScopeKind.Region => GetWorldsInRegion(scope.Name),
                MarketScopeKind.DataCenter => GetWorldsInDataCenter(scope.Name),
                MarketScopeKind.World => [scope],
                _ => [],
            };

            foreach (var world in scopeWorlds)
            {
                if (!worlds.Contains(world.Name, StringComparer.OrdinalIgnoreCase))
                    worlds.Add(world.Name);
            }
        }

        return worlds;
    }

    public List<string> BuildQueryTargets(IEnumerable<string> scopeNames)
    {
        var selectedScopes = scopeNames
            .Select(CanonicalizeName)
            .Where(name => name is not null)
            .Select(name => scopesByName[name!])
            .ToList();

        var selectedRegions = selectedScopes
            .Where(scope => scope.Kind == MarketScopeKind.Region)
            .Select(scope => scope.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedDataCenters = selectedScopes
            .Where(scope => scope.Kind == MarketScopeKind.DataCenter && !selectedRegions.Contains(scope.RegionName))
            .Select(scope => scope.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targets = new List<string>();
        foreach (var region in selectedRegions.OrderBy(region => region, StringComparer.OrdinalIgnoreCase))
            targets.Add(region);

        foreach (var dataCenter in selectedDataCenters.OrderBy(dataCenter => dataCenter, StringComparer.OrdinalIgnoreCase))
            targets.Add(dataCenter);

        foreach (var world in selectedScopes
                     .Where(scope => scope.Kind == MarketScopeKind.World)
                     .Where(scope => !selectedRegions.Contains(scope.RegionName))
                     .Where(scope => !selectedDataCenters.Contains(scope.DataCenterName))
                     .OrderBy(scope => scope.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!targets.Contains(world.Name, StringComparer.OrdinalIgnoreCase))
                targets.Add(world.Name);
        }

        return targets;
    }

    public string NormalizeForUniversalis(string targetName)
        => string.Equals(targetName, "North America", StringComparison.OrdinalIgnoreCase)
            ? "North-America"
            : targetName;

    private static string CanonicalRegionName(string regionName)
        => string.Equals(regionName, "North-America", StringComparison.OrdinalIgnoreCase)
            ? "North America"
            : regionName;

    private static string CanonicalLookupKey(string name)
        => name.Trim().Replace("_", " ").Replace("-", " ").ToLowerInvariant();
}
