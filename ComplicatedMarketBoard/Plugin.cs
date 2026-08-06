#pragma warning disable CS8618

using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Style;
using Dalamud.Plugin.Services;
using Miosuke.Messages;
using ComplicatedMarketBoard.Integrations.Universalis;
using ComplicatedMarketBoard.Assets;
using ComplicatedMarketBoard.Configuration;
using ComplicatedMarketBoard.Market;
using ComplicatedMarketBoard.Services;
using ComplicatedMarketBoard.Windows;
using Franthropy.Dalamud.Observations;
using Franthropy.Dalamud.Persistence;


namespace ComplicatedMarketBoard;

public sealed class ComplicatedMarketBoardPlugin : IDalamudPlugin
{
    public static string Name => "ComplicatedMarketBoard";
    public static string NameShort => "CMB";
    private const string CommandMain = "/cmb";
    private const string CommandMainAlt = "/mb";

    // PLUGIN
    internal static ComplicatedMarketBoardPlugin P;
    internal ComplicatedMarketBoardConfig Config;
    internal JsonConfigStore<ComplicatedMarketBoardConfig> ConfigStore { get; private set; }
    public DalamudLinkPayload? PluginPayload;
    public StyleModel PluginTheme { get; set; }
    public bool PluginThemeEnabled { get; set; }

    // MODULES
    public HoverSearchService HoverSearch { get; set; } = null!;
    public MarketRefreshService MarketRefresh { get; set; } = null!;
    public UniversalisClient Universalis { get; set; } = null!;
    public WorldTravelService WorldTravel { get; set; } = null!;
    public Integrations.Mmf.MarketContextIpcProvider MarketContextIpc { get; set; } = null!;
    private readonly DalamudSharedObservationHost? sharedObservationHost;

    // WINDOWS
    public ConfigWindow ConfigWindow { get; init; }
    public CustomScopeWindow CustomScopeWindow { get; init; }
    public MainWindow MainWindow { get; init; }
    public ChartsWindow ChartsWindow { get; init; }
    public WindowSystem WindowSystem = new("ComplicatedMarketBoard");





    public ComplicatedMarketBoardPlugin(
        IDalamudPluginInterface pluginInterface,
        IGameInventory gameInventory,
        IPlayerState playerState,
        IAddonLifecycle addonLifecycle,
        IPluginLog pluginLog)
    {
        // PLUGIN

        // dalamud service
        Service.Init(pluginInterface);
        try
        {
            sharedObservationHost = new DalamudSharedObservationHost(new DalamudSharedObservationHostOptions
            {
                PluginConfigDirectory = pluginInterface.GetPluginConfigDirectory(),
                PluginName = Name,
                PluginInstanceId = Guid.NewGuid().ToString("N"),
                GameBuild = Franthropy.Dalamud.Diagnostics.GamePatchCompatibilityGate.ReadCurrentGameVersion(),
                GameInventory = gameInventory,
                PlayerState = playerState,
                AddonLifecycle = addonLifecycle,
                Diagnostic = (message, exception) =>
                {
                    if (exception is null) pluginLog.Warning(message);
                    else pluginLog.Error(exception, message);
                },
            });
            sharedObservationHost.Start();
        }
        catch (Exception exception)
        {
            pluginLog.Error(exception, "CMB shared-observation hosting is unavailable.");
        }
        // plugin payload
        PluginPayload = Service.Chat.AddChatLinkHandler(1, pluginPayloadHandler);
        // lib
        MiosukeHelper.Init(
            pluginInterface,
            this,
            $"[{NameShort}] ",
            PluginPayload
        );


        // PLUGIN

        // plugin init
        P = this;
        // config init
        ConfigStore = new JsonConfigStore<ComplicatedMarketBoardConfig>(new JsonConfigStoreOptions
        {
            ConfigDirectory = pluginInterface.GetPluginConfigDirectory(),
            MainConfigFileName = "main.json",
            Diagnostic = (message, exception) =>
            {
                if (exception is null) pluginLog.Warning(message);
                else pluginLog.Error(exception, message);
            },
        });
        if (pluginInterface.ConfigFile.Exists) ConfigStore.TryMigrateFrom(pluginInterface.ConfigFile.FullName);
        Config = ConfigStore.Load();

        // theme
        ImGuiThemeLoadCustomOrDefault();

        // command handlers
        Service.Commands.AddHandler(CommandMain, new CommandInfo(OnCommandMain)
        {
            HelpMessage = "main command entry:\n" +
                "└ /cmb → open the main window (market data table).\n" +
                "└ /cmb c|config → open the configuration window."
        });
        Service.Commands.AddHandler(CommandMainAlt, new CommandInfo(OnCommandMain)
        {
            HelpMessage = "[ SAME AS ] → /cmb"
        });


        // MODULES

        Universalis = new UniversalisClient();
        HoverSearch = new HoverSearchService();
        MarketRefresh = new MarketRefreshService();
        WorldTravel = new WorldTravelService();
        MarketContextIpc = new Integrations.Mmf.MarketContextIpcProvider();


        // WINDOWS

        ConfigWindow = new ConfigWindow();
        CustomScopeWindow = new CustomScopeWindow();
        MainWindow = new MainWindow();
        ChartsWindow = new ChartsWindow(MainWindow);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(CustomScopeWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ChartsWindow);


        // HANDLERS

        Service.PluginInterface.UiBuilder.Draw += DrawUI;
        Service.PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;
        Service.PluginInterface.UiBuilder.OpenMainUi += DrawMainUI;
        Service.ClientState.Login += OnLogin;
        Service.ClientState.TerritoryChanged += OnTerritoryChanged;
        Service.Framework.Update += OnFrameUpdateWindow;
        Service.Framework.Update += OnFrameUpdateSearch;
    }

    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();

        // unload command handlers
        Service.Commands.RemoveHandler(CommandMain);
        Service.Commands.RemoveHandler(CommandMainAlt);

        // unload modules
        HoverSearch.Dispose();
        MarketRefresh.Dispose();
        Universalis.Dispose();
        MarketContextIpc.Dispose();
        sharedObservationHost?.Dispose();

        // unload windows
        ConfigWindow.Dispose();
        CustomScopeWindow.Dispose();
        MainWindow.Dispose();
        ChartsWindow.Dispose();

        // unload event handlers
        Service.PluginInterface.UiBuilder.Draw -= DrawUI;
        Service.PluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUI;
        Service.PluginInterface.UiBuilder.OpenMainUi -= DrawMainUI;
        Service.ClientState.Login -= OnLogin;
        Service.ClientState.TerritoryChanged -= OnTerritoryChanged;
        Service.Framework.Update -= OnFrameUpdateWindow;
        Service.Framework.Update -= OnFrameUpdateSearch;

        MiosukeHelper.Dispose();
    }

    private void pluginPayloadHandler(uint id, SeString text)
    {
        var payload = text.TextValue.Trim();
        if (string.Equals(payload, $"[{NameShort}]", StringComparison.OrdinalIgnoreCase))
        {
            MainWindow.Toggle();
        }
    }

    public void DrawUI()
    {
        WindowSystem.Draw();
    }

    public void DrawMainUI()
    {
        MainWindow.Toggle();
    }

    public void DrawConfigUI()
    {
        ConfigWindow.Toggle();
    }

    public static void ImGuiThemeLoadCustomOrDefault()
    {
        try
        {
            if (P.Config.CustomTheme != "")
            {
                var _theme = StyleModel.Deserialize(P.Config.CustomTheme);
                if (_theme is not null) P.PluginTheme = _theme;
                return;
            }
        }
        catch (Exception e)
        {
            P.Config.CustomTheme = "";
            P.Config.Save();
            Notice.Error($"Your custom theme is invalid and has been reset: {e.Message}");
        }
        finally
        {
            P.PluginTheme = Data.defaultTheme;
        }
    }


    public void OnCommandMain(string command, string args)
    {
        if (args == "c" || args == "config")
        {
            ConfigWindow.Toggle();
            return;
        }
        else
        {
            MainWindow.Toggle();
        }
    }

    public void OnLogin()
    {
        MainWindow.UpdateWorld();
    }

    public void OnTerritoryChanged(uint territoryId)
    {
        MainWindow.UpdateWorld();
    }

    private bool windowHotkeyHandled = false;
    public void OnFrameUpdateWindow(IFramework framework)
    {
        if (!Config.WindowHotkeyEnabled) return;
        if (!Miosuke.Action.Hotkey.IsActive(Config.WindowHotkey, true))
        {
            windowHotkeyHandled = false;
            return;
        }

        if (!windowHotkeyHandled)
        {
            if (Config.WindowHotkeyCanShow && !MainWindow.IsOpen)
            {
                windowHotkeyHandled = true;
                MainWindow.IsOpen = true;
            }
            else if (Config.WindowHotkeyCanHide && MainWindow.IsOpen)
            {
                windowHotkeyHandled = true;
                MainWindow.IsOpen = false;
            }
        }
    }

    private bool searchHotkeyHandled = false;
    public void OnFrameUpdateSearch(IFramework framework)
    {
        if (!Config.SearchHotkeyEnabled) return;
        if (!Miosuke.Action.Hotkey.IsActive(Config.SearchHotkey, !Config.SearchHotkeyLoose))
        {
            searchHotkeyHandled = false;
            return;
        }

        if (!searchHotkeyHandled)
        {
            if (MainWindow.IsOpen)
            {
                if (Config.SearchHotkeyCanHide && (HoverSearch.HoverItemId == 0))
                {
                    searchHotkeyHandled = true;
                    MainWindow.IsOpen = false;
                }
                else if (HoverSearch.SavedItemId != 0)
                {
                    searchHotkeyHandled = true;
                    HoverSearch.CheckItem(HoverSearch.SavedItemId);
                }
            }
            else if (Config.HotkeyBackgroundSearchEnabled && (HoverSearch.HoverItemId != 0))
            {
                searchHotkeyHandled = true;
                HoverSearch.CheckItem(HoverSearch.HoverItemId);
            }
        }
    }


    public Lumina.Excel.Sheets.World LocalPlayerCurrentWorld;
    public bool IsInGame => Service.ClientState.IsLoggedIn && Service.PlayerState.IsLoaded;
}
