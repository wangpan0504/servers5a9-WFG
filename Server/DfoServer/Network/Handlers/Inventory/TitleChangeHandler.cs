using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class TitleChangeHandler(InventoryTitleChangeService titles,
    InventoryProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.USE_TITLE_CHANGE_ITEM, Handle, nameof(TitleChangeHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        TitleChangeRequest request;
        try { request = TitleChangeRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.USE_TITLE_CHANGE_ITEM,
                TitleChangePackets.Ack(InventoryResult.Reject(19), default), "title_change_malformed");
            return CommandResult.Handled;
        }

        TitleChangeOutcome outcome = default;
        var result = context.BlocksTownActions ? InventoryResult.Reject(19) :
            context.Session.PlanAndCommitInventory(
                character => titles.Apply(character, request, out outcome),
                19,
                effectItem: new(InventoryListType.Main, request.ThreadSlot),
                useTarget: new(InventoryListType.Main, request.TitleSlot));
        var receipt = result.Receipt;
        log.Write("title_change", new
        {
            Connection = context.Id,
            request.ThreadSlot,
            request.TitleSlot,
            result.Accepted,
            result.Error,
            outcome.IsSuccess,
            outcome.ResultTitleItemId,
            outcome.UsedThreadItemId,
            Revision = receipt?.Character.Revision,
            Gold = receipt?.Character.Inventory.Gold
        });
        await projection.RefreshAsync(context, result);
        await context.Reply(CmdPacketType.USE_TITLE_CHANGE_ITEM, TitleChangePackets.Ack(result, outcome),
            result.Accepted ? "title_change_committed" : "title_change_rejected",
            receipt is not null ? () => context.Session.CanPublishInventory(receipt) : null);
        return CommandResult.Handled;
    }
}
