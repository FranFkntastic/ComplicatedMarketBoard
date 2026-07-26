using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Miosuke.Configuration;

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

    public override void Draw()
    {
        if (ImGui.SmallButton("Dock charts into the main window"))
        {
            P.Config.ChartsDetached = false;
            P.Config.Save();
            IsOpen = false;
        }
        ImGui.Separator();
        mainWindow.DrawCharts();
    }
}
