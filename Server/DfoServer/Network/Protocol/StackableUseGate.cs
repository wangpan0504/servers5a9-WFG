using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers;
using DfoServer.Network.Parsers.Inventory;

namespace DfoServer.Network.Protocol;

// 在效果分发前检查源STK；目标型协议只装配已确认的物品世界兼容上下文。
internal static class StackableUseGate
{
    public static async Task<bool> RejectAsync(ItemCatalog items, GameConnectionContext context, Packet packet)
    {
        InventorySlot position;
        StackableActionUseRequest action = default;
        StackableUseRequest? use = null;
        EnchantmentRequest? enchant = null;
        ExpertJobEnchantStoreRequest? beadStore = null;
        AmplificationRequest? amplify = null;
        InventorySlot? targetSlot = null;
        try
        {
            if (packet.Opcode == (ushort)CmdPacketType.USE_STACKABLE_ACTION &&
                StackableActionRequests.TryUse(packet.Body, out action) &&
                action.List == (byte)InventoryListType.Main && action.Slot <= short.MaxValue)
                position = new(InventoryListType.Main, (short)action.Slot);
            else if (packet.Opcode == (ushort)CmdPacketType.USE_STACKABLE &&
                     (use = StackableUseRequests.Parse(packet.Body)) is not null)
                position = use.Position;
            else if (packet.Opcode == (ushort)CmdPacketType.ENCHANT_BY_BEAD)
            {
                enchant = EnchantmentRequests.Read(packet.Body);
                position = enchant.Source;
                targetSlot = enchant.Target;
            }
            else if (packet.Opcode == (ushort)CmdPacketType.USE_ENCHANT_STORE)
            {
                var store = ExpertJobRequests.ReadEnchantStore(packet.Body);
                if (store.Third != 2 || store.Fourth != 0 || store.Sixth != 0 || store.Second > int.MaxValue ||
                    store.Fifth > short.MaxValue || store.Seventh > short.MaxValue) return false;
                beadStore = store;
                position = new(InventoryListType.Main, (short)store.Seventh);
                targetSlot = new(InventoryListType.Main, (short)store.Fifth);
            }
            else if (packet.Opcode == (ushort)CmdPacketType.INVEST_ITEM_AMPLIFY_OPTION || packet.Opcode == (ushort)CmdPacketType.PURIFY_ITEM)
            {
                amplify = packet.Opcode == (ushort)CmdPacketType.PURIFY_ITEM ? AmplificationRequests.Purify(packet.Body) : AmplificationRequests.Invest(packet.Body);
                position = new(InventoryListType.Main, amplify.MaterialSlot);
                if (packet.Opcode == (ushort)CmdPacketType.INVEST_ITEM_AMPLIFY_OPTION)
                    targetSlot = new(InventoryListType.Main, amplify.TargetSlot);
            }
            else return false;
        }
        catch (InvalidDataException) { return false; } // 正文错误交回原协议，不改变各命令的失败布局。

        // 原成功请求仍须由消费处理器完成歧义/未决收尾，不能改发一个普通地点失败 ACK。
        if (use is not null && packet.Body.Length == 15 &&
            context.Session.MustHandleConsumableReplay(ConsumableUseRequests.Read(packet.Body))) return false;

        var capture = context.Owner.Execute(() => (Character: context.Session.Selected,
            Epoch: context.Session.SelectionEpoch, Phase: context.Session.Phase,
            Source: context.Session.Selected?.Inventory.Find(position),
            Target: targetSlot is { } target ? context.Session.Selected?.Inventory.Find(target) : null));
        // 源缺失可能是重复请求，保留原处理器的回执恢复；模板不匹配也交给原校验。
        if (capture is not { Character: { } character, Source: { } source } ||
            use is not null && source.Core.ItemId != use.ItemId ||
            amplify is not null && source.Core.ItemId != amplify.MaterialItemId ||
            targetSlot is not null && capture.Target is null ||
            amplify is not null && capture.Target is { } materialTarget && materialTarget.Core.ItemId != amplify.TargetItemId) return false;
        if (beadStore is { } storeRequest && (storeRequest.First != character.Id || context.Activity?.BlocksTownActions == true ||
            !ReferenceEquals(context.Session.CaptureExpertJobTrade(), character))) return false;

        StackableUseRules rules;
        ItemWorlds? targetWorlds;
        try
        {
            rules = items.StackableUseRules(source.Core.ItemId);
            // 601兼容本人宝珠；职业卡片与跨玩家附魔仍是材料用途，不进入主动使用门禁。
            if (beadStore is not null && !rules.IsDirectEnchantment) return false;
            targetWorlds = targetSlot is not null && capture.Target is { } target ? items.UsableWorlds(target.Core.ItemId) : null;
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
        { return false; } // 资源错误交给原动作解析入口处理。
        var rejection = context.Session.CheckStackableUse(character, capture.Epoch, capture.Phase, rules, targetWorlds);
        if (rejection == StackableUseRejection.None) return false;

        byte[] reply = use is not null ?
            StackableUsePackets.Result((ushort)use.Position.Index, (byte)use.Position.List, use.Token, use.ItemId, 123) :
            enchant is not null ? EnchantmentPackets.Ack(InventoryResult.Reject(InventoryEnchantmentService.Rejection), enchant) :
            beadStore is not null ? ExpertJobPackets.Fail(InventoryEnchantmentService.Rejection) :
            amplify is not null ? AmplificationPackets.Ack(InventoryResult.Reject(InventoryAmplificationService.Rejection), null) :
            StackableActionPackets.Rejected(action);
        string reason = rejection switch
        {
            StackableUseRejection.Place => "stackable_place_rejected",
            StackableUseRejection.World => "stackable_world_rejected",
            _ => "stackable_condition_rejected"
        };
        await context.Reply((CmdPacketType)packet.Opcode, reply, reason,
            () => context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, character) &&
                context.Session.SelectionEpoch == capture.Epoch && context.Session.Phase == capture.Phase));
        return true;
    }
}
