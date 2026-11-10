using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class DungeonIncountTicketHandler(DungeonIncountTicketService service, IDungeonIncountTicketStore store,
    InventoryProjection inventory, ServerLog log)
{
    public async Task<CommandResult> UseAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        bool valid = DungeonIncountTicketService.Handles(request.Action) && request.List == 0 && request.Slot <= short.MaxValue &&
            request.Reserved == 0 && request.FirstArgument == 0 && request.SecondArgument == 0;
        var result = context.Session.UseDungeonIncountTicket(new(InventoryListType.Main, unchecked((short)request.Slot)), service, store,
            !valid || context.BlocksTownActions, request.Action);
        if (result.Outcome == ConsumableUseOutcome.CloseConnection) return CommandResult.CloseConnection;
        bool Guard() => context.Session.CanPublishConsumable(result.Guard);
        bool accepted = result.Outcome == ConsumableUseOutcome.Committed;
        log.Write("dungeon_incount_ticket", new { Connection = context.Id, request.Slot, request.Action, Accepted = accepted,
            result.Reason, Revision = result.Inventory?.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request),
            accepted ? "dungeon_incount_ticket_used" : "dungeon_incount_ticket_rejected", Guard);
        if (accepted)
        {
            foreach (var count in result.Counts.Where(count => count.Dungeon != 530))
                await context.Notify(NotiPacketType.DUNGEON_ENTER_COUNT_INFO, DungeonEnterCountPackets.Remaining(count), "dungeon_incount_ticket_balance", Guard);
            if (result.Counts.Any(count => count.Dungeon == 530))
                await RiftDailyProjection.RefreshAsync(context, Guard, force: true);
            if (request.Action == 59)
                foreach (var count in context.Session.EnterCounts())
                    await context.Notify(NotiPacketType.DUNGEON_ENTER_COUNT_INFO, DungeonEnterCountPackets.Remaining(count), "dungeon_ticket_reset_balance", Guard);
            if (request.Action == 160)
                await AntonNormalSequenceProjection.RefreshAsync(context, Guard);
            await inventory.RefreshAsync(context, result.Inventory!, new HashSet<InventorySlot> { new(InventoryListType.Main, (short)request.Slot) });
        }
        return CommandResult.Handled;
    }
}
