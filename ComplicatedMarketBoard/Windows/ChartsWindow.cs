using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ComplicatedMarketBoard.Windows;

public class ChartsWindow : Window, IDisposable
{
    private readonly MainWindow mainWindow;

    public ChartsWindow(MainWindow mainWindow)
        : base("CMB Charts##ComplicatedMarketBoardCharts")
    {
        this.mainWindow = mainWindow;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(380, 300),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        Flags = P.Config.ChartsChinCollapsed
            ? ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse
            : ImGuiWindowFlags.None;
    }

    public override void Draw()
    {
        if (P.Config.ChartsChinCollapsed)
        {
            if (ImGui.BeginPopupContextWindow("charts-chin-context"))
            {
                if (ImGui.Selectable("Show title bar"))
                {
                    P.Config.ChartsChinCollapsed = false;
                    P.Config.Save();
                }
                ImGui.EndPopup();
            }
        }
        else
        {
            if (ImGui.SmallButton("Hide title bar"))
            {
                P.Config.ChartsChinCollapsed = true;
                P.Config.Save();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Dock charts into the main window"))
            {
                P.Config.ChartsDetached = false;
                P.Config.Save();
                IsOpen = false;
            }
            ImGui.Separator();
        }
        mainWindow.DrawCharts();
    }
}
