#pragma warning disable CS8618

using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Style;
using Dalamud.Plugin.Services;
using Miosuke.Configuration;
using Miosuke.Messages;
using ComplicatedMarketBoard.API;
using ComplicatedMarketBoard.Assets;
using ComplicatedMarketBoard.Configuration;
using ComplicatedMarketBoard.Modules;
using ComplicatedMarketBoard.Windows;


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
    public DalamudLinkPayload? PluginPayload;
    public StyleModel PluginTheme { get; set; }
    public bool PluginThemeEnabled { get; set; }

    // MODULES
    public HoveredItem HoveredItem { get; set; } = null!;
    public PriceChecker PriceChecker { get; set; } = null!;
    public Universalis Universalis { get; set; } = null!;

    // WINDOWS
    public ConfigWindow ConfigWindow { get; init; }
    public MainWindow MainWindow { get; init; }
    public WindowSystem WindowSystem = new("ComplicatedMarketBoard");





    public ComplicatedMarketBoardPlugin(IDalamudPluginInterface pluginInterface)
    {
        // PLUGIN

        // dalamud service
        Service.Init(pluginInterface);
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
        MioConfig.Setup(MainConfigFileName: "main.json");
        if (Service.PluginInterface.ConfigFile.Exists) MioConfig.Migrate<ComplicatedMarketBoardConfig>(Service.PluginInterface.ConfigFile.FullName);
        Config = MioConfig.Init<ComplicatedMarketBoardConfig>();

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

        Universalis = new Universalis();
        HoveredItem = new HoveredItem();
        PriceChecker = new PriceChecker();


        // WINDOWS

        ConfigWindow = new ConfigWindow();
        MainWindow = new MainWindow();
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);


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
        HoveredItem.Dispose();
        PriceChecker.Dispose();
        Universalis.Dispose();

        // unload windows
        ConfigWindow.Dispose();
        MainWindow.Dispose();

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
                if (Config.SearchHotkeyCanHide && (HoveredItem.HoverItemId == 0))
                {
                    searchHotkeyHandled = true;
                    MainWindow.IsOpen = false;
                }
                else if (HoveredItem.SavedItemId != 0)
                {
                    searchHotkeyHandled = true;
                    HoveredItem.CheckItem(HoveredItem.SavedItemId);
                }
            }
            else if (Config.HotkeyBackgroundSearchEnabled && (HoveredItem.HoverItemId != 0))
            {
                searchHotkeyHandled = true;
                HoveredItem.CheckItem(HoveredItem.HoverItemId);
            }
        }
    }


    public Lumina.Excel.Sheets.World LocalPlayerCurrentWorld;
    public bool IsInGame => Service.ClientState.IsLoggedIn && Service.PlayerState.IsLoaded;
}
