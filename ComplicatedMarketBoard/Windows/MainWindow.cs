using System.Globalization;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Textures;
using Lumina.Excel.Sheets;
using Lumina.Extensions;
using Miosuke.Configuration;
using Miosuke.UiHelper;
using ComplicatedMarketBoard.API;
using ComplicatedMarketBoard.Assets;
using ComplicatedMarketBoard.Modules;


namespace ComplicatedMarketBoard.Windows;

public class MainWindow : Window, IDisposable
{
    private static readonly float[] ListingColumnBaseWidths = [70.0f, 40.0f, 80.0f, 80.0f, 90.0f];
    private static readonly float[] HistoryColumnBaseWidths = [70.0f, 40.0f, 80.0f, 80.0f, 90.0f];
    private static readonly float[] WorldUpdateColumnBaseWidths = [44.0f, 68.0f];
    private const float ResizeHandleHeight = 4.0f;
    private const float ResizeHandleWidth = 4.0f;
    private const float MinimumTableHeight = 60.0f;
    private const int MinimumRightPanelWidth = 80;
    private const int SearchResultLimit = 20;
    private static readonly List<ItemSearchResult> SearchableItems = Data.ItemSheet
        .Where(item => item.RowId > 0 && item.ItemSearchCategory.RowId != 0)
        .Select(item => new ItemSearchResult(item.RowId, item.Name.ToString()))
        .Where(item => !string.IsNullOrWhiteSpace(item.Name))
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public MainWindow() : base(
        "ComplicatedMarketBoard",
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        Size = new Vector2(350, 450);
        SizeCondition = ImGuiCond.FirstUseEver;


        CurrentItem.Id = 4691;
        CurrentItem.InGame = Data.ItemSheet.GetRow(4691)!;
        CurrentItemLabel = "(/ω＼)";
        CurrentItemIcon = Service.Texture.GetFromGameIcon(new GameIconLookup(CurrentItem.InGame.Icon));
        if (P.Config.selectedCustomScopeId != "" || P.Config.selectedWorld != "") lastSelectedWorld = GetSelectedMarketScopeLabel();
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

    public override void OnOpen()
    {
        UpdateWorld();
    }

    public override void OnClose()
    {
        P.PriceChecker.SearchHistoryClean();
    }

    public void Dispose()
    {
    }


    public PriceChecker.GameItem CurrentItem { get; set; } = new PriceChecker.GameItem();
    public ISharedImmediateTexture CurrentItemIcon = null!;
    public string CurrentItemLabel = "";

    public void CurrentItemUpdate(PriceChecker.GameItem gameItem)
    {
        CurrentItem = gameItem;
        CurrentItemIcon = Service.Texture.GetFromGameIcon(new GameIconLookup(CurrentItem.InGame.Icon))!;
        CurrentItem.Name = CurrentItem.InGame.Name.ToString();
        CurrentItemLabel = CurrentItem.Name;
    }

    public string lastSelectedWorld = "";
    private bool searchHistoryOpen = true;

    private int selectedListing = -1;
    private int selectedHistory = -1;
    private string itemSearchText = "";
    private string lastItemSearchText = "";
    private List<ItemSearchResult> itemSearchResults = [];
    private bool itemSearchOpen;

    private ListingSortColumn listingSortColumn = ListingSortColumn.Selling;
    private bool listingSortDescending;
    private HistorySortColumn historySortColumn = HistorySortColumn.Date;
    private bool historySortDescending = true;

    public int LoadingQueue = 0;
    public bool RefreshInProgress { get; private set; }
    public string RefreshStatusText { get; private set; } = "Market data idle";
    public long RefreshStartedAt { get; private set; }
    public long RefreshCompletedAt { get; private set; }
    public float RefreshProgress { get; private set; }
    public string RefreshErrorText { get; private set; } = "";

    public MarketScopeCatalog ScopeCatalog { get; } = new();
    public List<MarketScopeSelectorRow> worldList = [];
    public string playerHomeWorld = "";

    private enum ListingSortColumn
    {
        Selling,
        Quantity,
        Total,
        World,
        Retainer,
    }

    private enum HistorySortColumn
    {
        Sold,
        Quantity,
        Date,
        World,
        Buyer,
    }

    private sealed record ItemSearchResult(uint Id, string Name);

    public void BeginMarketDataRefresh(string itemName)
    {
        RefreshInProgress = true;
        RefreshStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RefreshCompletedAt = 0;
        RefreshProgress = 0.10f;
        RefreshErrorText = "";
        RefreshStatusText = $"Preparing market request for {itemName}";
    }

    public void UpdateMarketDataRefresh(string statusText, float progress)
    {
        RefreshStatusText = statusText;
        RefreshProgress = Math.Clamp(progress, 0.0f, 0.95f);
    }

    public void CompleteMarketDataRefresh(string itemName)
    {
        RefreshInProgress = false;
        RefreshCompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RefreshProgress = 1.0f;
        RefreshErrorText = "";
        RefreshStatusText = $"Market data refreshed for {itemName}";
    }

    public void FailMarketDataRefresh(string errorText)
    {
        RefreshInProgress = false;
        RefreshCompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RefreshProgress = 1.0f;
        RefreshErrorText = errorText;
        RefreshStatusText = $"Market refresh failed: {errorText}";
    }


    public override void Draw()
    {
        // -------------------------------- [  ui settings  ] --------------------------------
        // global
        var spacing = ImGui.GetStyle().ItemSpacing;

        // user
        var rightColWidth = P.Config.rightColWidth;
        var LeftColWidth = ImGui.GetWindowWidth() - rightColWidth - ResizeHandleWidth - spacing.X;

        // -------------------------------- [  run check  ] --------------------------------
        // plugin.HoveredItem.CheckLastItem();


        // -------------------------------- [  column left  ] --------------------------------
        ImGui.BeginChild("col_left", new Vector2(LeftColWidth, 0), false, ImGuiWindowFlags.NoScrollbar);

        // icon and name
        if (CurrentItem.Id > 0)
        {
            DrawItemIcon();
            ImGui.SameLine();
            DrawItemName();
        }

        // refresh button

        ImGui.SetCursorPosY(ImGui.GetTextLineHeightWithSpacing() + 1.1f * spacing.Y + P.Config.ButtonSizeOffset[1]);
        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX()
            + ImGui.GetContentRegionAvail().X
            - P.Config.WorldComboWidth
            - 2 * (P.Config.ButtonSizeOffset[0] + 0.5f * spacing.X)
        );
        DrawRefreshButton(P.Config.ButtonSizeOffset[0]);
        ImGui.SameLine();

        // HQ filter button
        ImGui.SetCursorPosY(ImGui.GetTextLineHeightWithSpacing() + 1.1f * spacing.Y + P.Config.ButtonSizeOffset[1]);
        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX()
            + ImGui.GetContentRegionAvail().X
            - P.Config.WorldComboWidth
            - 1 * (P.Config.ButtonSizeOffset[0] + 0.5f * spacing.X)
        );
        DrawHqFilterButton(P.Config.ButtonSizeOffset[0]);
        ImGui.SameLine();

        // world selection dropdown
        ImGui.SetCursorPosY(ImGui.GetTextLineHeightWithSpacing() + 1 * spacing.Y + P.Config.ButtonSizeOffset[1]);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - P.Config.WorldComboWidth);
        DrawWorldCombo(P.Config.WorldComboWidth);

        DrawItemSearchBar(spacing);
        DrawMarketDataStatusBar(spacing);

        // price table
        if (CurrentItem.Id > 0)
        {
            DrawPriceTables(ImGui.GetContentRegionAvail().Y);
        }

        ImGui.EndChild();
        ImGui.SameLine(0, 0);
        DrawVerticalResizeHandle("main right panel width", ImGui.GetContentRegionAvail().Y, deltaX =>
        {
            var maxRightPanelWidth = Math.Max(MinimumRightPanelWidth, (int)(ImGui.GetWindowWidth() - MinimumTableHeight - ResizeHandleWidth - spacing.X));
            P.Config.rightColWidth = Math.Clamp(P.Config.rightColWidth - (int)Math.Round(deltaX), MinimumRightPanelWidth, maxRightPanelWidth);
            P.Config.Save();
        });
        ImGui.SameLine();


        // -------------------------------- [  column right  ] --------------------------------
        ImGui.BeginGroup();


        // -------------------------------- [  buttons  ] --------------------------------
        var rightColTableWidth = rightColWidth - 2 * spacing.X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() - 0.5f * spacing.X);
        ImGui.BeginChild("col_right buttons", new Vector2(rightColTableWidth, 24 + 2 * spacing.Y + P.Config.ButtonSizeOffset[1]), true, ImGuiWindowFlags.NoScrollbar);

        // buttons
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 0.7f * spacing.Y);  // move the cursor up a bit for all buttons

        // history button
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() - 0.3f * spacing.X);
        DrawHistoryButton();
        ImGui.SameLine();

        // bin button
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() - 0.2f * spacing.X);
        DrawBinButton();
        ImGui.SameLine();

        // config button
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() - 0.2f * spacing.X);
        DrawConfigButton();

        ImGui.EndChild();


        // -------------------------------- [  item lists  ] --------------------------------
        var rightPaneHeight = ImGui.GetContentRegionAvail().Y;
        var dataColHeight = P.Config.WorldUpdateTableHeight + ImGui.GetTextLineHeightWithSpacing() + ResizeHandleHeight + 2f * spacing.Y;
        var searchHistoryHeight = Math.Max(MinimumTableHeight, rightPaneHeight - dataColHeight);

        ImGui.BeginChild("col_right search_history", new Vector2(rightColTableWidth - spacing.X, searchHistoryHeight), false, ImGuiWindowFlags.HorizontalScrollbar);
        DrawSearchHistory();
        ImGui.EndChild();


        // -------------------------------- [  velocity  ] --------------------------------
        ImGui.Separator();
        DrawVelocity();

        ImGui.Separator();

        DrawHorizontalResizeHandle("world freshness height", deltaY =>
        {
            var maxWorldUpdateHeight = Math.Max(MinimumTableHeight, rightPaneHeight - MinimumTableHeight - ImGui.GetTextLineHeightWithSpacing() - ResizeHandleHeight - 2f * spacing.Y);
            P.Config.WorldUpdateTableHeight = Math.Clamp(P.Config.WorldUpdateTableHeight - deltaY, MinimumTableHeight, maxWorldUpdateHeight);
            P.Config.Save();
        });

        // -------------------------------- [  world outdated  ] --------------------------------
        ImGui.BeginChild("col_right world_outdated", new Vector2(rightColTableWidth - spacing.X, P.Config.WorldUpdateTableHeight), false, ImGuiWindowFlags.HorizontalScrollbar);
        DrawWorldOutdated(spacing, rightColTableWidth);
        ImGui.EndChild();


        // column right end
        ImGui.EndGroup();
    }

    public void UpdateWorld()
    {
        if (!P.IsInGame) return;

        if (P.Config.OverridePlayerHomeWorld)
        {
            var world = Service.Data.GetExcelSheet<World>().First(x => x.Name.ToString() == P.Config.PlayerHomeWorld);
            var dataCentre = world.DataCenter;
            var otherWorldsInDc = Service.Data.GetExcelSheet<World>()!
                .Where(x => x.DataCenter.RowId == dataCentre.RowId && x.IsPublic && x.Name != world.Name)
                .OrderBy(x => x.Name.ToString())
                .Select(x => x.Name.ToString());
            updateWorldList(
                dataCentre.Value.Region.Value.Name.ToString(),
                dataCentre.Value!.Name.ToString(),
                world.Name.ToString(),
                [.. otherWorldsInDc]
            );
        }
        else
        {
            var world = Service.PlayerState.CurrentWorld.Value;
            var dataCentre = world.DataCenter;
            var otherWorldsInDc = Service.Data.GetExcelSheet<World>()!
                .Where(x => x.DataCenter.RowId == dataCentre.RowId && x.IsPublic && x.Name != world.Name)
                .OrderBy(x => x.Name.ToString())
                .Select(x => x.Name.ToString());
            updateWorldList(
                dataCentre.Value.Region.Value.Name.ToString(),
                dataCentre.Value.Name.ToString(),
                world.Name.ToString(),
                [.. otherWorldsInDc]
            );
        }
    }

    private void updateWorldList(string region, string dataCentre, string homeWorld, List<string> worldsInDc)
    {
        var changed = ScopeCatalog.CanonicalizeConfigList(P.Config.AdditionalWorlds);
        foreach (var customScope in P.Config.CustomMarketScopes)
            changed |= ScopeCatalog.CanonicalizeConfigList(customScope.IncludedScopes);
        if (changed)
            P.Config.Save();

        var additionalWorlds = P.Config.AdditionalWorlds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        worldList.Clear();

        var visibleRegions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { region };
        var visibleDataCenters = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { dataCentre };
        var dataCentersExpandedByRegion = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var additionalScope in additionalWorlds)
        {
            if (!ScopeCatalog.TryGetScope(additionalScope, out var scope))
                continue;

            if (scope.Kind == MarketScopeKind.Region)
            {
                visibleRegions.Add(scope.Name);
                if (ScopeCatalog.DataCentersByRegion.TryGetValue(scope.Name, out var dataCenters))
                    foreach (var regionDataCenter in dataCenters)
                    {
                        visibleDataCenters.Add(regionDataCenter);
                        dataCentersExpandedByRegion.Add(regionDataCenter);
                    }
            }
            else if (scope.Kind == MarketScopeKind.DataCenter)
            {
                visibleRegions.Add(scope.RegionName);
                visibleDataCenters.Add(scope.Name);
            }
            else if (scope.Kind == MarketScopeKind.World)
            {
                visibleRegions.Add(scope.RegionName);
                visibleDataCenters.Add(scope.DataCenterName);
            }
        }

        worldList.Add(new MarketScopeSelectorRow("regions", "Regions", MarketScopeKind.Header, 0));
        foreach (var visibleRegion in visibleRegions.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            worldList.Add(new MarketScopeSelectorRow(
                visibleRegion,
                visibleRegion,
                MarketScopeKind.Region,
                1,
                additionalWorlds.Contains(visibleRegion)));

        worldList.Add(new MarketScopeSelectorRow("data-centers", "Data Centers", MarketScopeKind.Header, 0));
        foreach (var visibleRegion in visibleRegions.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            if (!ScopeCatalog.DataCentersByRegion.TryGetValue(visibleRegion, out var dataCenters))
                continue;

            foreach (var visibleDataCenter in dataCenters.Where(visibleDataCenters.Contains).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                worldList.Add(new MarketScopeSelectorRow(
                    visibleDataCenter,
                    visibleDataCenter,
                    MarketScopeKind.DataCenter,
                    1,
                    additionalWorlds.Contains(visibleDataCenter)));

                var worldsInDataCenter = ScopeCatalog.GetWorldsInDataCenter(visibleDataCenter).ToList();
                var hasExplicitWorldsInDataCenter = worldsInDataCenter.Any(world => additionalWorlds.Contains(world.Name));
                var showAllWorlds = string.Equals(visibleDataCenter, dataCentre, StringComparison.OrdinalIgnoreCase)
                                    || dataCentersExpandedByRegion.Contains(visibleDataCenter)
                                    || additionalWorlds.Contains(visibleDataCenter) && !hasExplicitWorldsInDataCenter;
                var worldsToShow = worldsInDataCenter.Where(world => showAllWorlds || additionalWorlds.Contains(world.Name));

                foreach (var world in worldsToShow)
                {
                    worldList.Add(new MarketScopeSelectorRow(
                        world.Name,
                        world.DisplayName,
                        MarketScopeKind.World,
                        2,
                        additionalWorlds.Contains(world.Name),
                        string.Equals(world.Name, homeWorld, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        if (P.Config.CustomMarketScopes.Count > 0)
        {
            worldList.Add(new MarketScopeSelectorRow("custom-scopes", "Custom Scopes", MarketScopeKind.Header, 0));
            foreach (var customScope in P.Config.CustomMarketScopes.OrderBy(scope => scope.Name, StringComparer.OrdinalIgnoreCase))
            {
                var displayName = string.IsNullOrWhiteSpace(customScope.Name) ? "Unnamed custom scope" : customScope.Name;
                worldList.Add(new MarketScopeSelectorRow(
                    customScope.Id,
                    displayName,
                    MarketScopeKind.Custom,
                    1,
                    false,
                    false,
                    customScope.Id));
            }
        }

        playerHomeWorld = homeWorld;
        if (P.Config.selectedWorld == "" && P.Config.selectedCustomScopeId == "")
        {
            P.Config.selectedWorld = dataCentre;
        }
    }

    public static ulong ParseItemId(string clipboardText)
    {
        var clipboardTextTrimmed = clipboardText.Trim();
        var inGame = Data.ItemSheet.Single(i => i.Name == clipboardTextTrimmed);
        Service.Log.Info($"Clipboard text: {clipboardTextTrimmed}, Item ID: {inGame.RowId}");
        return inGame.RowId;
        // debug
        // if (inGame is not null)
        // {
        //     return inGame.RowId;
        // }
        // return 0;
    }

    private void DrawItemIcon()
    {
        ImGui.SetCursorPosY(0);

        if (ImGui.ImageButton(CurrentItemIcon.GetWrapOrEmpty().Handle, new Vector2(40, 40), Vector2.Zero, Vector2.One, 2))
        {
            if (Miosuke.Action.Hotkey.IsActive([VirtualKey.CONTROL], !P.Config.SearchHotkeyLoose))
            {
                var clipboardItemId = ParseItemId(ImGui.GetClipboardText());
                P.PriceChecker.DoCheckAsync(clipboardItemId);
            }
            else
            {
                ImGui.LogToClipboard();
                ImGui.LogText(CurrentItem.Name);
                ImGui.LogFinish();
            }
        }
        if (ImGui.BeginPopupContextItem($"testiconcontextmenu##{CurrentItem.Id}"))
        {
            if (ImGui.Selectable("Copy Name (LClick)"))
            {
                ImGui.SetClipboardText(CurrentItem.Name);
            }
            if (ImGui.Selectable("Copy ID"))
            {
                ImGui.SetClipboardText(CurrentItem.Id.ToString());
            }
            if (ImGui.Selectable("New search from clipboard (Ctrl+LClick)"))
            {
                var clipboardItemId = ParseItemId(ImGui.GetClipboardText());
                P.PriceChecker.DoCheckAsync(clipboardItemId);
            }
            ImGui.EndPopup();
        }
    }

    private void DrawItemName()
    {
        ImGui.SetCursorPosY(ImGui.GetCursorPosY());

        Data.Axis20.Push();

        ImGui.Text(CurrentItemLabel);
        if (LoadingQueue > 0)
        {
            ImGui.SameLine();
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.ColourHq);
            ImGui.Text($"{(char)FontAwesomeIcon.Spinner}");
            ImGui.PopStyleColor();
            ImGui.PopFont();
        }

        Data.Axis20.Pop();
    }

    private void DrawRefreshButton(float size)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{(char)FontAwesomeIcon.Repeat}", new Vector2(size, size)))
        {
            P.PriceChecker.DoCheckRefreshAsync(CurrentItem);
        }
        ImGui.PopFont();
    }

    private void DrawHqFilterButton(float size)
    {
        var _iconColour = Ui.ColourWhite;
        if (P.Config.FilterHq) _iconColour = Ui.ColourHq;
        if (P.Config.UniversalisHqOnly) _iconColour = Ui.ColourBlue;
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.PushStyleColor(ImGuiCol.Text, _iconColour);
        if (ImGui.Button($"{(char)FontAwesomeIcon.Splotch}", new Vector2(size, size)))
        {
            if (Miosuke.Action.Hotkey.IsActive([VirtualKey.CONTROL], !P.Config.SearchHotkeyLoose))
            {
                P.Config.UniversalisHqOnly = !P.Config.UniversalisHqOnly;
            }
            else
            {
                P.Config.FilterHq = !P.Config.FilterHq;
            }
        }
        ImGui.PopStyleColor();
        ImGui.PopFont();
    }

    private void DrawWorldCombo(float width)
    {
        if (P.PluginThemeEnabled)
        {
            Data.NotoSans17.Pop();
        }

        ImGui.SetNextItemWidth(width);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(width, ImGui.GetTextLineHeightWithSpacing() * 4),
            new Vector2(Math.Max(width, P.Config.WorldComboWidth), Math.Max(120.0f, P.Config.WorldComboPopupHeight)));
        if (ImGui.BeginCombo($"###{Name}selectedWorld", GetSelectedMarketScopeLabel()))
        {
            foreach (var scope in worldList)
            {
                if (scope.Kind == MarketScopeKind.Header)
                {
                    ImGui.TextColored(Ui.ColourCyan, scope.DisplayName);
                    continue;
                }

                if (scope.IsHomeWorld) ImGui.PushStyleColor(ImGuiCol.Text, Ui.ColourHq);

                var label = $"{new string(' ', scope.Indent * 2)}{GetScopeIcon(scope.Kind)}  {scope.DisplayName}{(scope.IsAdditional ? "*" : "")}";
                var isSelected = scope.Kind == MarketScopeKind.Custom
                    ? scope.CustomScopeId == P.Config.selectedCustomScopeId
                    : P.Config.selectedCustomScopeId == "" && scope.Name == P.Config.selectedWorld;

                if (ImGui.Selectable(label, isSelected))
                {
                    if (scope.Kind == MarketScopeKind.Custom)
                    {
                        P.Config.selectedCustomScopeId = scope.CustomScopeId;
                    }
                    else
                    {
                        P.Config.selectedWorld = scope.Name;
                        P.Config.selectedCustomScopeId = "";
                    }

                    P.Config.Save();

                    if (GetSelectedMarketScopeLabel() != lastSelectedWorld)
                    {
                        Service.Log.Debug($"Fetch data of {GetSelectedMarketScopeLabel()}");
                        P.PriceChecker.DoCheckRefreshAsync(CurrentItem);
                    }

                    lastSelectedWorld = GetSelectedMarketScopeLabel();
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }

                if (scope.IsHomeWorld) ImGui.PopStyleColor();
            }

            ImGui.EndCombo();
        }

        if (P.PluginThemeEnabled)
        {
            Data.NotoSans17.Push();
        }
    }

    public string GetSelectedMarketScopeLabel()
    {
        if (P.Config.selectedCustomScopeId != "")
        {
            var customScope = P.Config.CustomMarketScopes.FirstOrDefault(scope => scope.Id == P.Config.selectedCustomScopeId);
            if (customScope is not null)
                return string.IsNullOrWhiteSpace(customScope.Name) ? "Unnamed custom scope" : customScope.Name;
        }

        return P.Config.selectedWorld;
    }

    private static string GetScopeIcon(MarketScopeKind kind) => kind switch
    {
        MarketScopeKind.Region => $"{(char)SeIconChar.ExperienceFilled}",
        MarketScopeKind.DataCenter => $"{(char)SeIconChar.Experience}",
        MarketScopeKind.Custom => $"{(char)FontAwesomeIcon.LayerGroup}",
        _ => "",
    };

    private void DrawItemSearchBar(Vector2 spacing)
    {
        ImGui.SetCursorPosX(0);
        ImGui.Spacing();

        var inputWidth = Math.Max(120.0f, ImGui.GetContentRegionAvail().X - P.Config.ButtonSizeOffset[0] - spacing.X);
        ImGui.SetNextItemWidth(inputWidth);
        if (ImGui.InputText($"###{Name}itemSearch", ref itemSearchText, 96, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            RefreshItemSearchResults();
            if (itemSearchResults.Count > 0)
                SearchItem(itemSearchResults[0]);
        }

        if (ImGui.IsItemActivated())
            itemSearchOpen = true;

        if (ImGui.IsItemEdited())
        {
            RefreshItemSearchResults();
            itemSearchOpen = !string.IsNullOrWhiteSpace(itemSearchText);
        }

        if (string.IsNullOrWhiteSpace(itemSearchText) && !ImGui.IsItemActive())
        {
            var min = ImGui.GetItemRectMin();
            ImGui.GetWindowDrawList().AddText(
                new Vector2(min.X + 5.0f, min.Y + 2.0f),
                ImGui.GetColorU32(Ui.ColourWhite4),
                "Search marketable items...");
        }

        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{(char)FontAwesomeIcon.Times}", new Vector2(P.Config.ButtonSizeOffset[0], ImGui.GetItemRectSize().Y)))
        {
            itemSearchText = "";
            itemSearchResults.Clear();
            itemSearchOpen = false;
        }
        ImGui.PopFont();

        if (!itemSearchOpen || itemSearchResults.Count == 0)
            return;

        var resultHeight = Math.Min(itemSearchResults.Count, 8) * ImGui.GetTextLineHeightWithSpacing() + spacing.Y;
        ImGui.SetCursorPosX(0);
        ImGui.BeginChild(
            "item search results",
            new Vector2(0, resultHeight),
            true,
            ImGuiWindowFlags.None);

        ItemSearchResult? selectedResult = null;
        foreach (var result in itemSearchResults)
        {
            if (ImGui.Selectable($"{result.Name} [{result.Id}]##search{result.Id}", (uint)CurrentItem.Id == result.Id))
                selectedResult = result;
        }

        ImGui.EndChild();

        if (selectedResult is not null)
            SearchItem(selectedResult);
    }

    private void RefreshItemSearchResults()
    {
        var query = itemSearchText.Trim();
        if (query == lastItemSearchText)
            return;

        lastItemSearchText = query;
        itemSearchResults.Clear();

        if (query.Length == 0)
            return;

        itemSearchResults = SearchableItems
            .Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Id.ToString(CultureInfo.InvariantCulture) == query)
            .OrderBy(item => item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name.Length)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(SearchResultLimit)
            .ToList();
    }

    private void SearchItem(ItemSearchResult result)
    {
        itemSearchText = result.Name;
        lastItemSearchText = result.Name;
        itemSearchOpen = false;
        itemSearchResults.Clear();
        P.PriceChecker.DoCheckAsync(result.Id);
    }

    private void DrawPriceTables(float availableHeight)
    {
        if (!P.Config.EnableRecentHistory)
        {
            DrawCurrentListingTable(availableHeight);
            return;
        }

        var gap = Math.Max(0, P.Config.spaceBetweenTables);
        var listingHeight = availableHeight / 2 + P.Config.soldTableOffset;
        listingHeight = Math.Clamp(listingHeight, MinimumTableHeight, availableHeight - MinimumTableHeight - ResizeHandleHeight - gap);
        var historyHeight = Math.Max(MinimumTableHeight, availableHeight - listingHeight - ResizeHandleHeight - gap);

        DrawCurrentListingTable(listingHeight);
        DrawHorizontalResizeHandle("listing history split", deltaY =>
        {
            var baseHeight = availableHeight / 2;
            var minOffset = MinimumTableHeight - baseHeight;
            var maxOffset = availableHeight - MinimumTableHeight - ResizeHandleHeight - gap - baseHeight;
            P.Config.soldTableOffset = (int)Math.Round(Math.Clamp(P.Config.soldTableOffset + deltaY, minOffset, maxOffset));
            P.Config.Save();
        });

        if (gap > 0)
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + gap);
        }

        DrawHistoryEntryTable(historyHeight);
    }

    private static void DrawHorizontalResizeHandle(string id, Action<float> onDrag)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Ui.ColourCyan);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Ui.ColourHq);
        ImGui.Button($"###{id}", new Vector2(ImGui.GetContentRegionAvail().X, ResizeHandleHeight));
        ImGui.PopStyleColor(3);

        if (ImGui.IsItemActive())
        {
            onDrag(ImGui.GetIO().MouseDelta.Y);
        }
    }

    private static void DrawVerticalResizeHandle(string id, float height, Action<float> onDrag)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Ui.ColourCyan);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Ui.ColourHq);
        ImGui.Button($"###{id}", new Vector2(ResizeHandleWidth, height));
        ImGui.PopStyleColor(3);

        if (ImGui.IsItemActive())
        {
            onDrag(ImGui.GetIO().MouseDelta.X);
        }
    }

    private static void SyncColumnWidthOffsets(float[] baseWidths, float[] offsets)
    {
        var changed = false;
        for (var i = 0; i < baseWidths.Length; i++)
        {
            var offset = ImGui.GetColumnWidth(i) - baseWidths[i];
            if (Math.Abs(offset - offsets[i]) > 0.5f)
            {
                offsets[i] = offset;
                changed = true;
            }
        }

        if (changed)
        {
            P.Config.Save();
        }
    }

    private static float[] EnsureColumnWidthOffsets(float[] offsets, int count)
    {
        if (offsets.Length == count)
        {
            return offsets;
        }

        var resized = new float[count];
        Array.Copy(offsets, resized, Math.Min(offsets.Length, count));
        return resized;
    }

    private static string GetSortLabel(string label, bool isActive, bool descending)
        => isActive ? $"{label} {(descending ? "v" : "^")}" : label;

    private static long GetListingTotal(MarketDataListing listing)
        => listing.PricePerUnit * listing.Quantity + (P.Config.TotalIncludeTax ? listing.Tax : 0);

    private string GetListingWorld(MarketDataListing listing)
        => CurrentItem.UniversalisResponse.IsCrossWorld ? listing.WorldName : P.Config.selectedWorld;

    private static string GetListingRetainer(MarketDataListing listing)
        => listing.RetainerName;

    private string GetHistoryWorld(MarketDataEntry entry)
        => CurrentItem.UniversalisResponse.IsCrossWorld ? entry.WorldName : P.Config.selectedWorld;

    private static string GetHistoryBuyer(MarketDataEntry entry)
        => entry.BuyerName;

    private void SetListingSortColumn(ListingSortColumn column)
    {
        if (listingSortColumn == column)
        {
            listingSortDescending = !listingSortDescending;
            return;
        }

        listingSortColumn = column;
        listingSortDescending = false;
    }

    private void SetHistorySortColumn(HistorySortColumn column)
    {
        if (historySortColumn == column)
        {
            historySortDescending = !historySortDescending;
            return;
        }

        historySortColumn = column;
        historySortDescending = column == HistorySortColumn.Date;
    }

    private void DrawListingSortHeader(string label, ListingSortColumn column)
    {
        ImGui.TableNextColumn();
        ImGui.PushStyleColor(ImGuiCol.Text, Ui.ColourCyan);
        if (ImGui.Selectable(GetSortLabel(label, listingSortColumn == column, listingSortDescending)))
        {
            SetListingSortColumn(column);
        }
        ImGui.PopStyleColor();
    }

    private void DrawHistorySortHeader(string label, HistorySortColumn column)
    {
        ImGui.TableNextColumn();
        ImGui.PushStyleColor(ImGuiCol.Text, Ui.ColourCyan);
        if (ImGui.Selectable(GetSortLabel(label, historySortColumn == column, historySortDescending)))
        {
            SetHistorySortColumn(column);
        }
        ImGui.PopStyleColor();
    }

    private void DrawListingHeaderRow()
    {
        ImGui.TableNextRow();
        DrawListingSortHeader("Selling", ListingSortColumn.Selling);
        DrawListingSortHeader("Q", ListingSortColumn.Quantity);
        DrawListingSortHeader("Total", ListingSortColumn.Total);
        DrawListingSortHeader("World", ListingSortColumn.World);
        DrawListingSortHeader("Retainer", ListingSortColumn.Retainer);
    }

    private void DrawHistoryHeaderRow()
    {
        ImGui.TableNextRow();
        DrawHistorySortHeader("Sold", HistorySortColumn.Sold);
        DrawHistorySortHeader("Q", HistorySortColumn.Quantity);
        DrawHistorySortHeader("Date", HistorySortColumn.Date);
        DrawHistorySortHeader("World", HistorySortColumn.World);
        DrawHistorySortHeader("Buyer", HistorySortColumn.Buyer);
    }

    private List<MarketDataListing> GetSortedListings()
    {
        var listings = CurrentItem.UniversalisResponse.Listings.AsEnumerable();
        if (P.Config.FilterHq)
        {
            listings = listings.Where(l => l.Hq);
        }

        listings = listingSortColumn switch
        {
            ListingSortColumn.Selling => listingSortDescending
                ? listings.OrderByDescending(l => l.PricePerUnit)
                : listings.OrderBy(l => l.PricePerUnit),
            ListingSortColumn.Quantity => listingSortDescending
                ? listings.OrderByDescending(l => l.Quantity)
                : listings.OrderBy(l => l.Quantity),
            ListingSortColumn.Total => listingSortDescending
                ? listings.OrderByDescending(GetListingTotal)
                : listings.OrderBy(GetListingTotal),
            ListingSortColumn.World => listingSortDescending
                ? listings.OrderByDescending(GetListingWorld)
                : listings.OrderBy(GetListingWorld),
            ListingSortColumn.Retainer => listingSortDescending
                ? listings.OrderByDescending(GetListingRetainer)
                : listings.OrderBy(GetListingRetainer),
            _ => listings,
        };

        return listings.ToList();
    }

    private List<MarketDataEntry> GetSortedHistory()
    {
        var entries = CurrentItem.UniversalisResponse.Entries.AsEnumerable();
        if (P.Config.FilterHq)
        {
            entries = entries.Where(e => e.Hq);
        }

        entries = historySortColumn switch
        {
            HistorySortColumn.Sold => historySortDescending
                ? entries.OrderByDescending(e => e.PricePerUnit)
                : entries.OrderBy(e => e.PricePerUnit),
            HistorySortColumn.Quantity => historySortDescending
                ? entries.OrderByDescending(e => e.Quantity)
                : entries.OrderBy(e => e.Quantity),
            HistorySortColumn.Date => historySortDescending
                ? entries.OrderByDescending(e => e.Timestamp)
                : entries.OrderBy(e => e.Timestamp),
            HistorySortColumn.World => historySortDescending
                ? entries.OrderByDescending(GetHistoryWorld)
                : entries.OrderBy(GetHistoryWorld),
            HistorySortColumn.Buyer => historySortDescending
                ? entries.OrderByDescending(GetHistoryBuyer)
                : entries.OrderBy(GetHistoryBuyer),
            _ => entries,
        };

        return entries.ToList();
    }

    private void DrawCurrentListingTable(float height)
    {
        ImGui.BeginChild("col_left current_listings", new Vector2(0, height));
        P.Config.sellingColWidthOffset = EnsureColumnWidthOffsets(P.Config.sellingColWidthOffset, ListingColumnBaseWidths.Length);

        if (ImGui.BeginTable(
            "col_left current_listings table",
            5,
            ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Selling", ImGuiTableColumnFlags.WidthFixed, ListingColumnBaseWidths[0] + P.Config.sellingColWidthOffset[0]);
            ImGui.TableSetupColumn("Q", ImGuiTableColumnFlags.WidthFixed, ListingColumnBaseWidths[1] + P.Config.sellingColWidthOffset[1]);
            ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, ListingColumnBaseWidths[2] + P.Config.sellingColWidthOffset[2]);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, ListingColumnBaseWidths[3] + P.Config.sellingColWidthOffset[3]);
            ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, ListingColumnBaseWidths[4] + P.Config.sellingColWidthOffset[4]);

            DrawListingHeaderRow();

            var marketDataListings = GetSortedListings();

            bool isColourPushed;
            for (var index = 0; index < marketDataListings.Count; index++)
            {
                var listing = marketDataListings[index];
                isColourPushed = false;
                if (P.Config.MarkHigherThanVendor && CurrentItem.VendorSelling > 0 && listing.PricePerUnit >= CurrentItem.VendorSelling)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, Ui.ColourCrimson);
                    isColourPushed = true;
                }
                else if (listing.Hq)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, Ui.ColourHq);
                    isColourPushed = true;
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                // Selling
                var selling = $"{listing.PricePerUnit}";
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + P.Config.tableRowHeightOffset);
                if (P.Config.NumbersAlignRight)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + P.Config.NumbersAlignRightOffset);
                    Ui.AlignRight(selling);
                }
                if (ImGui.Selectable($"{selling}##listing{index}", selectedListing == index, ImGuiSelectableFlags.SpanAllColumns))
                {
                    selectedListing = index;
                }
                ImGui.TableNextColumn();

                // Q
                var quantity = $"{listing.Quantity:##,###}";
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + P.Config.tableRowHeightOffset);
                if (P.Config.NumbersAlignRight)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + P.Config.NumbersAlignRightOffset);
                    Ui.AlignRight(quantity);
                }
                ImGui.Text(quantity);
                ImGui.TableNextColumn();

                // Total
                var totalPrice = GetListingTotal(listing);
                var total = totalPrice.ToString("N0", CultureInfo.CurrentCulture);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + P.Config.tableRowHeightOffset);
                if (P.Config.NumbersAlignRight)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + P.Config.NumbersAlignRightOffset);
                    Ui.AlignRight(total);
                }
                ImGui.Text(total);
                ImGui.TableNextColumn();

                if (isColourPushed) ImGui.PopStyleColor();

                // World
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + P.Config.tableRowHeightOffset);
                ImGui.Text(GetListingWorld(listing));
                ImGui.TableNextColumn();

                // Retainer
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + P.Config.tableRowHeightOffset);
                ImGui.Text(GetListingRetainer(listing));
            }

            SyncColumnWidthOffsets(ListingColumnBaseWidths, P.Config.sellingColWidthOffset);
            ImGui.EndTable();
        }

        // === item price table 1 ===
        ImGui.EndChild();
    }

    private void DrawHistoryEntryTable(float height)
    {
        // === item price table 2 ===
        ImGui.BeginChild("col_left history_entries", new Vector2(0, height));
        P.Config.soldColWidthOffset = EnsureColumnWidthOffsets(P.Config.soldColWidthOffset, HistoryColumnBaseWidths.Length);

        if (ImGui.BeginTable(
            "col_left history_entries table",
            5,
            ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Sold", ImGuiTableColumnFlags.WidthFixed, HistoryColumnBaseWidths[0] + P.Config.soldColWidthOffset[0]);
            ImGui.TableSetupColumn("Q", ImGuiTableColumnFlags.WidthFixed, HistoryColumnBaseWidths[1] + P.Config.soldColWidthOffset[1]);
            ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.WidthFixed, HistoryColumnBaseWidths[2] + P.Config.soldColWidthOffset[2]);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, HistoryColumnBaseWidths[3] + P.Config.soldColWidthOffset[3]);
            ImGui.TableSetupColumn("Buyer", ImGuiTableColumnFlags.WidthFixed, HistoryColumnBaseWidths[4] + P.Config.soldColWidthOffset[4]);

            DrawHistoryHeaderRow();

            var marketDataEntries = GetSortedHistory();

            for (var index = 0; index < marketDataEntries.Count; index++)
            {
                var entry = marketDataEntries[index];
                if (entry.Hq) ImGui.PushStyleColor(ImGuiCol.Text, Ui.ColourHq);

                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                // Sold
                var sold = $"{entry.PricePerUnit}";
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + P.Config.tableRowHeightOffset);
                if (P.Config.NumbersAlignRight)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + P.Config.NumbersAlignRightOffset);
                    Ui.AlignRight(sold);
                }
                if (ImGui.Selectable($"{entry.PricePerUnit}##history{index}", selectedHistory == index, ImGuiSelectableFlags.SpanAllColumns))
                {
                    selectedHistory = index;
                }
                ImGui.TableNextColumn();

                // Q
                var quantity = $"{entry.Quantity:##,###}";
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + P.Config.tableRowHeightOffset);
                if (P.Config.NumbersAlignRight)
                {
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + P.Config.NumbersAlignRightOffset);
                    Ui.AlignRight(quantity);
                }
                ImGui.Text(quantity);
                ImGui.TableNextColumn();

                // Date
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + P.Config.tableRowHeightOffset);
                ImGui.Text($"{DateTimeOffset.FromUnixTimeSeconds(entry.Timestamp).LocalDateTime:MM-dd HH:mm}");
                ImGui.TableNextColumn();

                // World
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + P.Config.tableRowHeightOffset);
                ImGui.Text(GetHistoryWorld(entry));
                ImGui.TableNextColumn();

                // Buyer
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + P.Config.tableRowHeightOffset);
                ImGui.Text(GetHistoryBuyer(entry));

                if (entry.Hq) ImGui.PopStyleColor();
            }

            SyncColumnWidthOffsets(HistoryColumnBaseWidths, P.Config.soldColWidthOffset);
            ImGui.EndTable();
        }

        // === item price table 2 ===
        ImGui.EndChild();
    }

    private void DrawHistoryButton()
    {
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.PushStyleColor(ImGuiCol.Text, searchHistoryOpen ? Ui.ColourHq : Ui.ColourWhite);
        if (ImGui.Button($"{(char)FontAwesomeIcon.List}", new Vector2(P.Config.ButtonSizeOffset[0], ImGui.GetItemRectSize().Y)))
        {
            searchHistoryOpen = !searchHistoryOpen;
        }
        ImGui.PopStyleColor();
        ImGui.PopFont();
    }

    private void DrawBinButton()
    {
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{(char)FontAwesomeIcon.Trash}", new Vector2(P.Config.ButtonSizeOffset[0], ImGui.GetItemRectSize().Y)))
        {
            if (Miosuke.Action.Hotkey.IsActive([VirtualKey.CONTROL], !P.Config.SearchHotkeyLoose))
            {
                P.PriceChecker.GameItemCacheList.Clear();
            }
            else
            {
                P.PriceChecker.GameItemCacheList.RemoveAll(i => i.Id == CurrentItem.Id);
            }
        }
        ImGui.PopFont();
    }

    private static void DrawConfigButton()
    {
        ImGui.PushFont(UiBuilder.IconFont);
        if (ImGui.Button($"{(char)FontAwesomeIcon.Cog}", new Vector2(P.Config.ButtonSizeOffset[0], ImGui.GetItemRectSize().Y)))
        {
            P.DrawConfigUI();
        }
        ImGui.PopFont();
    }

    private void DrawMarketDataStatusBar(Vector2 spacing)
    {
        if (CurrentItem.Id == 0)
            return;

        ImGui.Spacing();
        ImGui.BeginChild(
            "market data refresh status",
            new Vector2(0, ImGui.GetTextLineHeightWithSpacing() + spacing.Y),
            true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 0.2f * spacing.Y);

        if (RefreshInProgress)
        {
            var elapsed = FormatDuration(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - RefreshStartedAt);
            ImGui.ProgressBar(
                RefreshProgress,
                new Vector2(-1, ImGui.GetTextLineHeightWithSpacing()),
                $"{RefreshStatusText}... {elapsed}");
        }
        else
        {
            ImGui.TextColored(GetMarketRefreshStatusColour(), GetMarketRefreshStatusText());
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(GetMarketDataStatusTooltip());
        ImGui.EndChild();
    }

    private Vector4 GetMarketRefreshStatusColour()
    {
        var response = CurrentItem.UniversalisResponse;
        if (response.Status != UniversalisResponseStatus.Success)
            return Ui.ColourCrimson;

        if (response.FetchTime == 0)
            return Ui.ColourWhite3;

        return Ui.ColourCyan;
    }

    private string GetMarketRefreshStatusText()
    {
        var response = CurrentItem.UniversalisResponse;
        if (response.Status != UniversalisResponseStatus.Success)
            return $"Market refresh failed: {GetUniversalisStatusLabel(response.Status)}";

        if (response.FetchTime == 0)
            return "Market data not loaded";

        var fetchedAgo = FormatDuration(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - response.FetchTime);
        return $"Market data refreshed {fetchedAgo} ago";
    }

    private string GetMarketDataStatusTooltip()
    {
        var response = CurrentItem.UniversalisResponse;
        if (RefreshInProgress)
        {
            var elapsed = RefreshStartedAt > 0
                ? FormatDuration(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - RefreshStartedAt)
                : "unknown";

            if (response.WorldOutOfDate.Count == 0)
                return $"{RefreshStatusText}\nElapsed: {elapsed}";

            return $"{RefreshStatusText}\nElapsed: {elapsed}\n\n{GetMarketFreshnessTooltip()}";
        }

        if (response.Status != UniversalisResponseStatus.Success)
            return $"Universalis status: {GetUniversalisStatusLabel(response.Status)}.";

        if (response.WorldOutOfDate.Count == 0)
            return "No freshness data was returned for this item.";

        return GetMarketFreshnessTooltip();
    }

    private string GetMarketFreshnessTooltip()
    {
        var response = CurrentItem.UniversalisResponse;
        var freshness = response.WorldOutOfDate.OrderByDescending(w => w.Value).ToList();
        var newest = freshness.MinBy(w => w.Value);
        var oldest = freshness.MaxBy(w => w.Value);
        var min = freshness.Min(w => w.Value);
        var avg = freshness.Average(w => w.Value);
        var max = freshness.Max(w => w.Value);
        var fetchedAt = response.FetchTime > 0
            ? FormatTimestamp(response.FetchTime)
            : "unknown";
        var newestUpload = response.LatestUploadTime > 0
            ? FormatTimestamp(response.LatestUploadTime)
            : "unknown";

        return
            $"Fetched: {fetchedAt}\n" +
            $"Newest upload: {newestUpload}\n" +
            $"Freshness: {min:F2} / {avg:F2} / {max:F2} hrs min/avg/max\n" +
            $"Freshest market: {newest.Key} ({newest.Value:F2} hrs)\n" +
            $"Stalest market: {oldest.Key} ({oldest.Value:F2} hrs)\n" +
            $"Worlds: {response.WorldOutOfDate.Count}\n" +
            $"Listings: {response.Listings.Count}\n" +
            $"Recent sales: {response.Entries.Count}";
    }

    private static string GetUniversalisStatusLabel(ulong status) => status switch
    {
        UniversalisResponseStatus.Success => "ok",
        UniversalisResponseStatus.ServerError => "server error",
        UniversalisResponseStatus.InvalidData => "invalid data",
        UniversalisResponseStatus.UserCancellation => "request timed out",
        UniversalisResponseStatus.UnknownError => "unknown error",
        _ => $"status {status}",
    };

    private static string FormatDuration(long milliseconds)
    {
        if (milliseconds < 0)
            milliseconds = 0;

        var duration = TimeSpan.FromMilliseconds(milliseconds);
        if (duration.TotalMinutes < 1)
            return $"{duration.TotalSeconds:F1}s";
        if (duration.TotalHours < 1)
            return $"{duration.TotalMinutes:F1}m";
        if (duration.TotalDays < 1)
            return $"{duration.TotalHours:F2}h";
        return $"{duration.TotalDays:F2}d";
    }

    private static string FormatTimestamp(long unixMilliseconds)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    private void DrawWorldOutdated(Vector2 spacing, float rightColTableWidth)
    {
        if (ImGui.BeginTable(
            "col_right world_outdated table",
            2,
            ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Hrs", ImGuiTableColumnFlags.WidthFixed, WorldUpdateColumnBaseWidths[0] + P.Config.WorldUpdateColWidthOffset[0]);
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, WorldUpdateColumnBaseWidths[1] + P.Config.WorldUpdateColWidthOffset[1]);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + P.Config.WorldUpdateColPaddingOffset[0]);
            ImGui.TextColored(Ui.ColourCyan, "Hrs");
            ImGui.TableNextColumn();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + P.Config.WorldUpdateColPaddingOffset[1]);
            ImGui.TextColored(Ui.ColourCyan, "World");

            foreach (var i in CurrentItem.WorldOutOfDate)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + P.Config.WorldUpdateColPaddingOffset[0]);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 0.5f * spacing.Y);

                Ui.AlignRight($"{i.Value:F2}");
                ImGui.Text($"{i.Value:F2}");
                ImGui.TableNextColumn();

                ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 0.5f * spacing.Y);
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + P.Config.WorldUpdateColPaddingOffset[1]);
                ImGui.Text($"{i.Key}");
            }

            SyncColumnWidthOffsets(WorldUpdateColumnBaseWidths, P.Config.WorldUpdateColWidthOffset);
            ImGui.EndTable();
        }
    }

    private void DrawVelocity()
    {
        var velocity = CurrentItem.UniversalisResponse.Velocity;
        ImGui.TextColored(Ui.ColourCyan, "Market velocity");
        ImGuiComponents.HelpMarker(
            "Universalis sale velocity for this item. Higher values mean more recent sales volume in the selected market."
        );
        ImGui.Text($"{(int)velocity}");
    }

    private void DrawSearchHistory()
    {
        if (searchHistoryOpen)
        {
            foreach (var item in P.PriceChecker.GameItemCacheList)
            {
                if (ImGui.Selectable($"{item.Name}", (uint)CurrentItem.Id == item.Id))
                {
                    P.PriceChecker.DoCheckAsync(item.Id);
                }
            }
        }
    }
}
