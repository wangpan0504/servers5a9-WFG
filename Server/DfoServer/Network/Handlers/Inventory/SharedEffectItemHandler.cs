using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class SharedEffectItemHandler(InventoryBoosterService boosters, InventoryProjection inventory,
    TownProjection town, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.USE_SHARED_EFFECT_ITEM,
        Handle, nameof(SharedEffectItemHandler), UserStateRequirement.WorldReady);

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var request = SharedEffectItemRequests.Read(packet.Body);
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        byte outcome = 0;
        InventoryResult result;
        try
        {
            result = context.BlocksTownActions || request.Slot > short.MaxValue ? InventoryResult.Reject(17) :
                context.Session.PlanAndCommitInventory(character => boosters.UseSharedEffect(character, request.ItemId,
                    (short)request.Slot, out outcome), 17, usedItem: new(InventoryListType.Main, (short)request.Slot));
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            result = InventoryResult.Reject(17) with { RejectReason = error.Message };
        }
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, capture.Selected) &&
                context.Session.SelectionEpoch == capture.SelectionEpoch && context.Session.Phase == capture.Phase);
        log.Write("shared_effect_item_use", new { Connection = context.Id, request.ItemId, request.Slot,
            result.Accepted, result.Error, result.RejectReason, Outcome = outcome, Revision = result.Receipt?.Character.Revision });
        ushort wireError = result.Accepted ? (ushort)0 : result.Error == 4 ? (ushort)22 : result.Error == 17 ? (ushort)17 : (ushort)1;
        await context.Reply(CmdPacketType.USE_SHARED_EFFECT_ITEM, SharedEffectItemPackets.Result(request.Slot, outcome,
            wireError), "shared_effect_item_result", Guard);
        if (result.Receipt is not { } committed || !result.Accepted) return CommandResult.Handled;
        await town.SharedItemEffectAsync(context, request.ItemId, outcome, Guard);
        var slot = new InventorySlot(InventoryListType.Main, (short)request.Slot);
        var before = result.Plan!.BeforeIndex.Find(slot)!;
        var after = committed.Index.Find(slot);
        // ACK251原生扣一件。只有实际结果与此相同才省略源槽；奖励复用源槽时仍下发完整新实例。
        bool acknowledged = after is null || after == before with { Core = before.Core with { Value = before.Core.Value - 1 } };
        await inventory.RefreshAsync(context, result, acknowledgedSlots: acknowledged ? new HashSet<InventorySlot> { slot } : null);
        return CommandResult.Handled;
    }
}
