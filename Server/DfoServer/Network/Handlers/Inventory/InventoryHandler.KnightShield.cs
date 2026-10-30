using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Protocol;
using System.Data.Common;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed partial class InventoryHandler
{
    async Task<CommandResult> MoveShieldAsync(GameConnectionContext context, InventoryMoveRequest request)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch));
        InventoryResult result;
        try
        {
            result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(23) :
                context.Session.PlanAndCommitInventory(character => knightShields!.Move(character, request), 23);
        }
        catch (Exception error) when (error is DbException or InvalidOperationException)
        {
            result = InventoryResult.Reject(23) with { RejectReason = "knight_shield_commit_rejected" };
        }
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, before.Selected) &&
                context.Session.SelectionEpoch == before.SelectionEpoch);
        log?.Write("knight_shield_deck", new { Connection = context.Id, Command = 19,
            Source = (byte)request.Source.List, SourceSlot = request.Source.Index,
            Destination = (byte)request.Destination.List, DestinationSlot = request.Destination.Index,
            result.Accepted, result.Error, result.RejectReason, Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.MOVE_ITEMSPACE, InventoryPackets.Move(request, result),
            result.Accepted ? "knight_shield_committed" : "knight_shield_rejected", Guard);
        // ACK19按deck0创建客户端临时盾核心；随后用真实槽24差量安装持久化的完整核心。
        if (result.Accepted && result.Receipt is { } committed && result.Plan is { } plan &&
            !plan.ChangedSlots.Contains(DfoServer.Game.Inventory.Services.InventoryKnightShieldService.MainSlot))
        {
            // 同模板拖动也会让ACK19重建临时24；即使存档实体不变也须恢复真实孔位、徽章等完整核心。
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(committed.Character.Inventory,
                InventoryListType.Equipment, [DfoServer.Game.Inventory.Services.InventoryKnightShieldService.MainSlot], index: committed.Index),
                "knight_shield_existing_core", Guard);
            await projection.RefreshCharacterAsync(context, committed.Character, "knight_shield_existing", Guard);
        }
        else await projection.RefreshAsync(context, result);
        var character = result.Receipt?.Character ?? before.Selected;
        if (character is not null)
            await context.Notify(NotiPacketType.SEND_DECK_INFO, KnightShieldPackets.Deck(knightShields!.EffectiveDeck(character)),
                "knight_shield_authoritative_deck", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> ChangeShieldDeckAsync(GameConnectionContext context, Packet packet)
    {
        var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch));
        InventoryResult result;
        try
        {
            var request = KnightShieldRequests.Change(packet.Body);
            result = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(23) :
                context.Session.PlanAndCommitInventory(character => knightShields!.Change(character, request), 23);
        }
        catch (InvalidDataException)
        {
            result = InventoryResult.Reject(23) with { RejectReason = "malformed_knight_shield_deck" };
        }
        catch (Exception error) when (error is DbException or InvalidOperationException)
        {
            result = InventoryResult.Reject(23) with { RejectReason = "knight_shield_commit_rejected" };
        }
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, before.Selected) &&
                context.Session.SelectionEpoch == before.SelectionEpoch);
        log?.Write("knight_shield_deck", new { Connection = context.Id, Command = 657,
            result.Accepted, result.Error, result.RejectReason, Revision = result.Receipt?.Character.Revision });
        await context.Reply(CmdPacketType.CHANGE_DECK_INFO, KnightShieldPackets.Changed(result),
            result.Accepted ? "knight_shield_deck_committed" : "knight_shield_deck_rejected", Guard);
        if (!result.Accepted && before.Selected is { } character)
            await context.Notify(NotiPacketType.SEND_DECK_INFO, KnightShieldPackets.Deck(knightShields!.EffectiveDeck(character)),
                "knight_shield_deck_restore", Guard);
        return CommandResult.Handled;
    }
}
