using Dalamud.Interface;
using ComplicatedMarketBoard.Assets;
using ComplicatedMarketBoard.Market;
using Miosuke.UiHelper;
using Franthropy.Dalamud.UI.Performance;

namespace ComplicatedMarketBoard.Windows.Controls;

public static class ScopeTreeEditor
{
    [RenderFrameWorkJustification("Expanded hierarchical nodes are installation-bounded by the FFXIV world catalog.", 256)]
    public static void Draw(
        List<string> selectedScopes,
        string suffix,
        bool cascadeDataCenter,
        bool includeCurrentWorld,
        string currentWorldName,
        Action onChanged)
    {
        var catalog = P.MainWindow.ScopeCatalog;
        if (catalog.CanonicalizeConfigList(selectedScopes))
            onChanged();

        if (includeCurrentWorld)
        {
            var currentWorldChecked = selectedScopes.Contains(MarketScopeCatalog.CurrentWorldScopeName, StringComparer.OrdinalIgnoreCase);
            var currentWorldLabel = string.IsNullOrWhiteSpace(currentWorldName)
                ? MarketScopeCatalog.CurrentWorldScopeName
                : $"{MarketScopeCatalog.CurrentWorldScopeName} ({currentWorldName})";

            if (ImGui.Checkbox($"{currentWorldLabel}##{suffix}-current-world", ref currentWorldChecked))
                SetScopeSelected(selectedScopes, MarketScopeCatalog.CurrentWorldScopeName, currentWorldChecked, false, onChanged);

            ImGui.Separator();
        }

        foreach (var region in catalog.Regions)
        {
            var regionChecked = selectedScopes.Contains(region, StringComparer.OrdinalIgnoreCase);
            if (ImGui.Checkbox($"##{suffix}-region-check-{region}", ref regionChecked))
                SetScopeSelected(selectedScopes, region, regionChecked, false, onChanged);

            ImGui.SameLine();
            var regionOpen = ImGui.TreeNodeEx($"{region}##{suffix}-region-tree-{region}", ImGuiTreeNodeFlags.SpanAvailWidth);
            if (regionOpen)
            {
                if (catalog.DataCentersByRegion.TryGetValue(region, out var dataCenters))
                {
                    foreach (var dataCenter in dataCenters)
                    {
                        var dataCenterChecked = selectedScopes.Contains(dataCenter, StringComparer.OrdinalIgnoreCase);
                        if (ImGui.Checkbox($"##{suffix}-dc-check-{dataCenter}", ref dataCenterChecked))
                            SetScopeSelected(selectedScopes, dataCenter, dataCenterChecked, cascadeDataCenter, onChanged);

                        ImGui.SameLine();
                        var dataCenterOpen = ImGui.TreeNodeEx($"{dataCenter}##{suffix}-dc-tree-{dataCenter}", ImGuiTreeNodeFlags.SpanAvailWidth);
                        if (dataCenterOpen)
                        {
                            foreach (var world in catalog.GetWorldsInDataCenter(dataCenter))
                            {
                                var worldChecked = selectedScopes.Contains(world.Name, StringComparer.OrdinalIgnoreCase);
                                if (ImGui.Checkbox($"{world.DisplayName}##{suffix}-world-{world.Name}", ref worldChecked))
                                    SetScopeSelected(selectedScopes, world.Name, worldChecked, false, onChanged);
                            }

                            ImGui.TreePop();
                        }
                    }
                }

                ImGui.TreePop();
            }
        }
    }

    [RenderFrameWorkJustification("Unknown entries render only in an explicit configuration editor for repair.", 256)]
    public static void DrawUnknownSavedEntries(List<string> selectedScopes, string suffix, Action onChanged)
    {
        var unknownScopes = P.MainWindow.ScopeCatalog.GetUnknownScopes(selectedScopes);
        if (unknownScopes.Count == 0)
            return;

        ImGui.TextColored(Ui.ColourCrimson, "Unknown saved entries");
        foreach (var unknownScope in unknownScopes)
        {
            ImGui.Text(unknownScope);
            ImGui.SameLine();
            ImGui.PushFont(UiBuilder.IconFont);
            if (ImGui.Button($"{FontAwesomeIcon.Trash.ToIconString()}##{suffix}-unknown-{unknownScope}"))
            {
                RemoveScope(selectedScopes, unknownScope);
                onChanged();
            }
            ImGui.PopFont();
        }
    }

    private static void SetScopeSelected(
        List<string> selectedScopes,
        string scopeName,
        bool selected,
        bool cascadeDataCenter,
        Action onChanged)
    {
        var catalog = P.MainWindow.ScopeCatalog;
        var canonicalName = catalog.CanonicalizeName(scopeName);
        if (canonicalName is null)
            return;

        if (selected)
        {
            AddScope(selectedScopes, canonicalName);
            if (cascadeDataCenter && catalog.TryGetScope(canonicalName, out var scope) && scope.Kind == MarketScopeKind.DataCenter)
            {
                foreach (var world in catalog.GetWorldsInDataCenter(scope.Name))
                    AddScope(selectedScopes, world.Name);
            }
        }
        else
        {
            RemoveScope(selectedScopes, canonicalName);
            if (cascadeDataCenter && catalog.TryGetScope(canonicalName, out var scope) && scope.Kind == MarketScopeKind.DataCenter)
            {
                foreach (var world in catalog.GetWorldsInDataCenter(scope.Name))
                    RemoveScope(selectedScopes, world.Name);
            }
        }

        onChanged();
    }

    private static void AddScope(List<string> selectedScopes, string scopeName)
    {
        if (!selectedScopes.Contains(scopeName, StringComparer.OrdinalIgnoreCase))
            selectedScopes.Add(scopeName);
    }

    private static void RemoveScope(List<string> selectedScopes, string scopeName)
        => selectedScopes.RemoveAll(scope => string.Equals(scope, scopeName, StringComparison.OrdinalIgnoreCase));
}
