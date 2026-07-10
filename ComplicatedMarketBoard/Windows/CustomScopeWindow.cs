using Dalamud.Interface;
using Dalamud.Interface.Components;
using ComplicatedMarketBoard.Assets;
using ComplicatedMarketBoard.Market;
using ComplicatedMarketBoard.Windows.Controls;
using Miosuke.Configuration;
using Miosuke.UiHelper;

namespace ComplicatedMarketBoard.Windows;

public class CustomScopeWindow : Window, IDisposable
{
    private string newCustomScopeName = "";
    private string editingCustomScopeId = "";

    public CustomScopeWindow() : base("Custom Market Scopes###ComplicatedMarketBoardCustomMarketScopes")
    {
        Size = new Vector2(520, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void PreDraw()
    {
        if (P.Config.EnableTheme)
        {
            P.PluginTheme.Push();
            Data.NotoSans17.Push();
            P.PluginThemeEnabled = true;
        }
    }

    public override void PostDraw()
    {
        if (P.PluginThemeEnabled)
        {
            P.PluginTheme.Pop();
            Data.NotoSans17.Pop();
            P.PluginThemeEnabled = false;
        }
    }

    public void Dispose()
    {
    }

    public void Open()
    {
        IsOpen = true;
    }

    public void OpenForNew()
    {
        var customScope = new CustomMarketScope { Name = GetDefaultScopeName() };
        P.Config.CustomMarketScopes.Add(customScope);
        editingCustomScopeId = customScope.Id;
        SaveAndRefreshWorldList();
        IsOpen = true;
    }

    public void OpenForScope(string customScopeId)
    {
        editingCustomScopeId = customScopeId;
        IsOpen = true;
    }

    public override void OnClose()
    {
        P.Config.Save();
    }

    public override void Draw()
    {
        var suffix = $"###{Name}";

        ImGui.TextColored(Ui.ColourCyan, "Saved market scopes");
        ImGuiComponents.HelpMarker("Saved scopes combine regions, data centers, worlds, and Current World into one reusable market target.");

        ImGui.SetNextItemWidth(Math.Max(180.0f, ImGui.GetContentRegionAvail().X - 36.0f));
        ImGui.InputText($"{suffix}-new-name", ref newCustomScopeName, 48);
        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{FontAwesomeIcon.Plus.ToIconString()}##{suffix}-add"))
            AddScopeFromInput();
        ImGui.PopFont();

        ImGui.Separator();

        if (P.Config.CustomMarketScopes.Count == 0)
        {
            ImGui.TextDisabled("No custom scopes saved.");
            return;
        }

        for (var i = 0; i < P.Config.CustomMarketScopes.Count; i++)
        {
            var customScope = P.Config.CustomMarketScopes[i];
            ImGui.PushID($"{suffix}-{customScope.Id}");

            DrawScopeHeader(customScope, ref i);

            if (editingCustomScopeId == customScope.Id)
            {
                ImGui.Indent();
                ScopeTreeEditor.Draw(
                    customScope.IncludedScopes,
                    $"{suffix}-{customScope.Id}-picker",
                    false,
                    true,
                    P.MainWindow.GetCurrentWorldScopeName(),
                    () => OnScopeContentsChanged(customScope.Id));
                ScopeTreeEditor.DrawUnknownSavedEntries(
                    customScope.IncludedScopes,
                    $"{suffix}-{customScope.Id}-unknown",
                    () => OnScopeContentsChanged(customScope.Id));
                ImGui.Unindent();
            }

            ImGui.PopID();
            if (i >= 0 && i < P.Config.CustomMarketScopes.Count - 1)
                ImGui.Separator();
        }
    }

    private void DrawScopeHeader(CustomMarketScope customScope, ref int index)
    {
        var scopeName = customScope.Name;
        ImGui.SetNextItemWidth(Math.Max(160.0f, ImGui.GetContentRegionAvail().X - 126.0f));
        if (ImGui.InputText("##name", ref scopeName, 48))
        {
            customScope.Name = scopeName;
            SaveAndRefreshWorldList();
        }

        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{FontAwesomeIcon.Check.ToIconString()}##select"))
            P.MainWindow.SelectCustomMarketScope(customScope.Id);
        ImGui.PopFont();
        ImGuiComponents.HelpMarker("Use this saved scope as the active market target.");

        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{(editingCustomScopeId == customScope.Id ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown).ToIconString()}##edit"))
            editingCustomScopeId = editingCustomScopeId == customScope.Id ? "" : customScope.Id;
        ImGui.PopFont();

        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{FontAwesomeIcon.Trash.ToIconString()}##delete"))
        {
            DeleteScope(index);
            index--;
            ImGui.PopFont();
            return;
        }
        ImGui.PopFont();

        ImGui.TextDisabled(GetCustomScopeSummary(customScope));
    }

    private void AddScopeFromInput()
    {
        var name = string.IsNullOrWhiteSpace(newCustomScopeName) ? GetDefaultScopeName() : newCustomScopeName.Trim();
        var customScope = new CustomMarketScope { Name = name };
        P.Config.CustomMarketScopes.Add(customScope);
        editingCustomScopeId = customScope.Id;
        newCustomScopeName = "";
        SaveAndRefreshWorldList();
    }

    private void DeleteScope(int index)
    {
        var customScope = P.Config.CustomMarketScopes[index];
        if (P.Config.selectedCustomScopeId == customScope.Id)
            P.Config.selectedCustomScopeId = "";

        if (editingCustomScopeId == customScope.Id)
            editingCustomScopeId = "";

        P.Config.CustomMarketScopes.RemoveAt(index);
        SaveAndRefreshWorldList();
    }

    private void OnScopeContentsChanged(string customScopeId)
    {
        SaveAndRefreshWorldList();
        if (P.Config.selectedCustomScopeId == customScopeId)
            P.MainWindow.RefreshSelectedMarketScope();
    }

    private void SaveAndRefreshWorldList()
    {
        P.Config.Save();
        P.MainWindow.UpdateWorld();
    }

    private string GetCustomScopeSummary(CustomMarketScope customScope)
    {
        var worldCount = P.MainWindow.ScopeCatalog.ExpandToWorldNames(
            customScope.IncludedScopes,
            P.MainWindow.GetCurrentWorldScopeName()).Count;
        if (worldCount == 0)
            return "No worlds selected";

        var dynamicSuffix = customScope.IncludedScopes.Contains(MarketScopeCatalog.CurrentWorldScopeName, StringComparer.OrdinalIgnoreCase)
            ? $" including {MarketScopeCatalog.CurrentWorldScopeName}"
            : "";
        return $"{worldCount} world{(worldCount == 1 ? "" : "s")}{dynamicSuffix}";
    }

    private static string GetDefaultScopeName()
        => P.Config.CustomMarketScopes.Count == 0
            ? "New custom scope"
            : $"New custom scope {P.Config.CustomMarketScopes.Count + 1}";
}
