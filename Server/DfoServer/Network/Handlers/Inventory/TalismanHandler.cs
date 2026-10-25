using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class TalismanHandler(InventoryTalismanService service, ItemCatalog items,
    InventoryProjection projection, ServerLog log, TimeProvider? clock = null) : IGameCommandModule
{
    readonly TimeProvider timer = clock ?? TimeProvider.System;

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.EQUIP_RUNE, Equip, nameof(TalismanHandler));
        commands.Register(CmdPacketType.REMOVE_RUNE, Remove, nameof(TalismanHandler));
        commands.Register(CmdPacketType.TALISMAN_REFINE, Refine, nameof(TalismanHandler));
    }

    Task<CommandResult> Equip(GameConnectionContext context, Packet packet) => Execute(context, packet,
        body =>
        {
            var request = TalismanRequests.Equip(body);
            return (character => service.Equip(character, request, Now()),
                _ => TalismanPackets.Equipped(request.TargetSlot, request.ItemId), null);
        });

    Task<CommandResult> Remove(GameConnectionContext context, Packet packet) => Execute(context, packet,
        body =>
        {
            ushort target = TalismanRequests.Remove(body);
            return (character => service.Remove(character, target, Now()), _ => TalismanPackets.Removed(target), null);
        });

    Task<CommandResult> Refine(GameConnectionContext context, Packet packet) => Execute(context, packet,
        body =>
        {
            var request = TalismanRequests.Refine(body);
            var target = new InventorySlot(InventoryListType.Talisman, request.TalismanSlot);
            return (character => service.Refine(character, request, items.TalismanRefineCosts(), Now()),
                committed => TalismanPackets.Refined(committed.Index.Find(target)!.Core.ItemId, (uint)target.Index), target);
        });

    async Task<CommandResult> Execute(GameConnectionContext context, Packet packet,
        Func<byte[], (Func<Character, InventoryResult> Plan, Func<InventoryCommitReceipt, byte[]> Reply, InventorySlot? Product)> parse)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        (Func<Character, InventoryResult> Plan, Func<InventoryCommitReceipt, byte[]> Reply, InventorySlot? Product) action;
        try { action = parse(packet.Body); }
        catch (InvalidDataException)
        {
            log.Write("talisman_action", new { Connection = context.Id, Opcode = packet.Opcode, Accepted = false, Reason = "malformed" });
            await context.Reply((CmdPacketType)packet.Opcode, TalismanPackets.Failure(InventoryTalismanService.Rejection), "talisman_malformed");
            return CommandResult.Handled;
        }
        var result = context.BlocksTownActions ? InventoryResult.Reject(InventoryTalismanService.Rejection) :
            context.Session.PlanAndCommitInventory(action.Plan, InventoryTalismanService.Rejection);
        bool Guard() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
            context.Owner.Execute(() => ReferenceEquals(capture.Selected, context.Session.Selected) &&
                capture.SelectionEpoch == context.Session.SelectionEpoch && capture.Phase == context.Session.Phase);
        log.Write("talisman_action", new
        {
            Connection = context.Id,
            Opcode = packet.Opcode,
            result.Accepted,
            result.Error,
            Action = result.Plan?.Reason,
            Revision = result.Receipt?.Character.Revision
        });
        if (!result.Accepted)
        {
            await context.Reply((CmdPacketType)packet.Opcode, TalismanPackets.Failure(result.Error), "talisman_rejected", Guard);
            if (capture.Selected is { } rejected)
                await context.Notify(NotiPacketType.ITEM_LIST,
                    InventoryPackets.SpecialFull(rejected.Inventory, InventoryListType.Talisman), "talisman_rejected_restore", Guard);
            return CommandResult.Handled;
        }
        var committed = result.Receipt ?? throw new InvalidOperationException("护石操作成功缺少实际提交回执");
        if (action.Product is { } product)
            // 结果窗按回执槽位立即读取完整孔色；源控件在ACK清理后会归还预留数量，随后再发绝对差量。
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,
                InventoryPackets.Delta(committed.Character.Inventory, InventoryListType.Talisman, [product], index: committed.Index),
                "talisman_result_preload", Guard);
        await context.Reply((CmdPacketType)packet.Opcode, action.Reply(committed), "talisman_committed", Guard);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    long Now() => timer.GetUtcNow().ToUnixTimeSeconds();
}
