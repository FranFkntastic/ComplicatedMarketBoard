using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using ComplicatedMarketBoard.Assets;
using Franthropy.Dalamud.Travel;
using Franthropy.Dalamud.Worlds;
using Miosuke;

namespace ComplicatedMarketBoard.Services;

public sealed class WorldTravelService
{
    private readonly LifestreamTravelCommandBuilder commandBuilder;

    public WorldTravelService()
    {
        var worlds = Data.WorldSheet
            .Where(world => world.IsPublic)
            .Select(world => new WorldInfo(
                world.Name.ToString(),
                world.DataCenter.Value.Name.ToString(),
                world.DataCenter.Value.Region.Value.Name.ToString(),
                world.RowId));

        commandBuilder = new LifestreamTravelCommandBuilder(new WorldCatalog(worlds));
    }

    public WorldTravelCommandResult TravelToMarketBoard(string targetWorld)
    {
        var buildResult = BuildTravelRequest(targetWorld, out var request);
        if (!buildResult.Success || request is null)
        {
            Notify(buildResult.Message, NotificationType.Error);
            return buildResult;
        }

        var handled = Service.Commands.ProcessCommand(request.Command);
        if (!handled)
        {
            var failure = WorldTravelCommandResult.Fail(
                $"Command was not handled: {request.Command}. Lifestream may be missing or disabled.",
                request.Command);
            Notify(failure.Message, NotificationType.Error);
            return failure;
        }

        var message = request.IsCurrentWorld
            ? "Sent Lifestream travel to the nearest market board."
            : $"Sent Lifestream travel to {request.TargetWorld}'s market board.";
        Notify(message, NotificationType.Info);
        return WorldTravelCommandResult.Ok(message, request.Command);
    }

    public WorldTravelCommandResult CopyMarketBoardTravelCommand(string targetWorld)
    {
        var buildResult = BuildTravelRequest(targetWorld, out var request);
        if (!buildResult.Success || request is null)
        {
            Notify(buildResult.Message, NotificationType.Error);
            return buildResult;
        }

        ImGui.SetClipboardText(request.Command);
        var message = $"Copied {request.Command}.";
        Notify(message, NotificationType.Info);
        return WorldTravelCommandResult.Ok(message, request.Command);
    }

    private WorldTravelCommandResult BuildTravelRequest(string targetWorld, out WorldTravelRequest? request)
    {
        return commandBuilder.TryBuildMarketBoardTravel(
            targetWorld,
            P.MainWindow.GetCurrentWorldScopeName(),
            out request);
    }

    private static void Notify(string message, NotificationType type)
    {
        Service.NotificationManager.AddNotification(new Notification
        {
            Content = message,
            Type = type,
        });
        Service.Log.Info($"[CMB] {message}");
    }
}
