using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class FatigueConsumableHandler(FatigueConsumableService service, InventoryProjection inventory, ServerLog log)
{
    public async Task<CommandResult> UseAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        bool validWire = request.List == (byte)InventoryListType.Main && request.Slot <= short.MaxValue &&
            request.Reserved == 0 && request.FirstArgument == 0 && request.SecondArgument == 0;
        var result = context.Session.UseFatigueConsumable(new(InventoryListType.Main, unchecked((short)request.Slot)), service,
            !validWire || context.Activity?.BlocksTownActions == true);
        if (result.Outcome == ConsumableUseOutcome.CloseConnection) return CommandResult.CloseConnection;
        var guard = result.Guard ?? throw new InvalidOperationException("疲劳药剂缺少原场景身份");
        bool Guard() => context.Session.CanPublishConsumable(guard);
        log.Write("consumable_use", new { Connection = context.Id, Opcode = (ushort)CmdPacketType.USE_STACKABLE_ACTION,
            request.Action, request.Slot, Item = result.FatigueRecovery?.Source.Core.ItemId,
            Accepted = result.Outcome == ConsumableUseOutcome.Committed, result.Reason, result.SourceUid });
        if (result.FatigueRecovery is null || result.Inventory?.Receipt is not { } receipt)
        {
            await context.Reply(CmdPacketType.USE_STACKABLE_ACTION, StackableActionPackets.Rejected(request, 19), "fatigue_consumable_rejected", Guard);
            return CommandResult.Handled;
        }
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION, StackableActionPackets.Used(request), "fatigue_consumable_used", Guard);
        await context.Notify(NotiPacketType.FATIGUE, FatiguePackets.Current(context.Session.FatigueForClient(receipt.Character)),
            "fatigue_consumable_balance", Guard);
        await inventory.RefreshAsync(context, result.Inventory,
            new HashSet<InventorySlot> { new(InventoryListType.Main, (short)request.Slot) }, refreshEquipment: false);
        return CommandResult.Handled;
    }
}
