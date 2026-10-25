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

internal sealed class TalismanHandler(InventoryTalismanService service, InventoryTalismanCompoundService compound,
    ItemCatalog items, InventoryProjection projection, ServerLog log, TimeProvider? clock = null) : IGameCommandModule
{
    readonly TimeProvider timer = clock ?? TimeProvider.System;

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.EQUIP_RUNE, Equip, nameof(TalismanHandler));
        commands.Register(CmdPacketType.REMOVE_RUNE, Remove, nameof(TalismanHandler));
        commands.Register(CmdPacketType.COMPOUND_RUNE, Compound, nameof(TalismanHandler));
        commands.Register(CmdPacketType.TALISMAN_REFINE, Refine, nameof(TalismanHandler));
    }

    Task<CommandResult> Equip(GameConnectionContext context, Packet packet) => Execute(context, packet,
        body =>
        {
            var request = TalismanRequests.Equip(body);
            var socket = new InventorySlot(InventoryListType.Talisman, (short)request.TargetSlot);
            return (character => service.Equip(character, request, Now()),
                _ => TalismanPackets.Equipped(request.TargetSlot, request.ItemId), () => [socket],
                $"target={request.TargetSlot} source={request.SourceSlot} item={request.ItemId}");
        });

    Task<CommandResult> Remove(GameConnectionContext context, Packet packet) => Execute(context, packet,
        body =>
        {
            ushort target = TalismanRequests.Remove(body);
            var socket = new InventorySlot(InventoryListType.Talisman, (short)target);
            return (character => service.Remove(character, target, Now()), _ => TalismanPackets.Removed(target),
                () => [socket], $"target={target}");
        });

    Task<CommandResult> Compound(GameConnectionContext context, Packet packet) => Execute(context, packet,
        body =>
        {
            var request = TalismanRequests.Compound(body);
            TalismanCompoundReceipt? receipt = null;
            return (character => compound.Compound(character, request, items.TalismanCompoundCosts(), Now(), out receipt),
                _ => receipt is { } product
                    ? TalismanPackets.Compounded((ushort)product.ProductSlot, product.ItemId, product.Upgraded)
                    : TalismanPackets.Failure(InventoryTalismanService.Rejection),
                () => receipt is { } product ? [new InventorySlot(InventoryListType.Talisman, product.ProductSlot)] : [],
                // 族/模式/品级是资源表索引，实测字节尚未全部落实，先在日志里留原值便于比对。
                $"family={request.Family} mode={request.Mode} rule={request.Rule} slots={string.Join(',', request.SourceSlots)}");
        });

    Task<CommandResult> Refine(GameConnectionContext context, Packet packet) => Execute(context, packet,
        body =>
        {
            var request = TalismanRequests.Refine(body);
            var target = new InventorySlot(InventoryListType.Talisman, request.TalismanSlot);
            return (character => service.Refine(character, request, items.TalismanRefineCosts(), Now()),
                committed => TalismanPackets.Refined(committed.Index.Find(target)!.Core.ItemId, (uint)target.Index),
                () => [target], $"material={request.MaterialSlot} target={request.TalismanSlot}");
        });

    async Task<CommandResult> Execute(GameConnectionContext context, Packet packet,
        Func<byte[], (Func<Character, InventoryResult> Plan, Func<InventoryCommitReceipt, byte[]> Reply,
            Func<InventorySlot[]> Preload, string Detail)> parse)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        (Func<Character, InventoryResult> Plan, Func<InventoryCommitReceipt, byte[]> Reply,
            Func<InventorySlot[]> Preload, string Detail) action;
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
            Action = result.Plan?.Reason ?? result.RejectReason,
            Revision = result.Receipt?.Character.Revision,
            action.Detail
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
        // 客户端 0x067B/0x067C/0x06CE 回执只重画护石窗口与列表，**不写物品容器**（0069A170/0069A4A0 全路径无容器写入）。
        // 图标完全靠回执那一刻的重画读取，因此提交后的孔位行必须先于回执到达；否则第一次操作看不到变化，
        // 要等下一次回执重画才补显。源槽不在预载内：客户端在 ACK 清理时才归还拖拽预留数量，绝对差量随后补发。
        if (action.Preload() is { Length: > 0 } preload)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,
                InventoryPackets.Delta(committed.Character.Inventory, InventoryListType.Talisman, preload, index: committed.Index),
                "talisman_result_preload", Guard);
        await context.Reply((CmdPacketType)packet.Opcode, action.Reply(committed), "talisman_committed", Guard);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    long Now() => timer.GetUtcNow().ToUnixTimeSeconds();
}
