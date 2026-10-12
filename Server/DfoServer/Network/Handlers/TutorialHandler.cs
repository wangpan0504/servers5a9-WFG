using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class TutorialHandler(TutorialCatalog tutorials, ItemCatalog items, GoldLimitCatalog limits,
    EpicPieceCatalog pieces, InventoryProjection inventory) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.CHANGE_TUTORIAL_FLAG, FlagAsync, nameof(TutorialHandler));
    async Task<CommandResult> FlagAsync(GameConnectionContext context, Packet packet)
    {
        var request = TutorialRequests.Flag(packet.Body);
        if (request.Prefix != 0 || request.Commit != 1 || context.Activity?.BlocksTownActions == true)
            return CommandResult.Unhandled;
        var result = context.Session.ChangeTutorialFlag(request.Progress, tutorials, items, limits, pieces);
        if (result is null) return CommandResult.Unhandled;
        bool Guard() => context.Session.CanPublishTutorial(result);
        await context.Reply(CmdPacketType.CHANGE_TUTORIAL_FLAG, TutorialPackets.Flag(result), "tutorial_checkpoint", Guard);
        if (result.Inventory is { } granted) await inventory.RefreshAsync(context, granted);
        return CommandResult.Handled;
    }
}
