using DfoServer.Game.Pvp;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Projection;

internal sealed class PvpMissionProjection(PvpMissionBook book)
{
    internal bool QueueRewards(GameConnectionContext context, InventoryResult? result)
    {
        if (result?.Plan is not { } plan || result.Receipt is not { } receipt) return true;
        return context.Owner.Execute(() =>
        {
            bool Current() => context.Owner.IsCurrent && receipt.Generation == context.Owner.Generation && context.Session.Ready &&
                context.Session.SelectionEpoch == receipt.SelectionEpoch && context.Session.Selected?.Id == receipt.Character.Id;
            if (!Current()) return false;
            // 在原owner内取最新已提交资产并入队。连续训练奖励不能因下一次revision而丢掉前一格差量。
            var inventory = context.Session.Selected!.Inventory;
            var slots = plan.ChangedSlots.Where(slot => slot.List == InventoryListType.Main).ToArray();
            if (slots.Length > 0 && !context.Sender.TryPost(0, (ushort)NotiPacketType.UPDATE_ITEM_LIST,
                InventoryPackets.Delta(inventory, InventoryListType.Main, slots), "pvp_mission_reward", Current)) return false;
            return plan.OverflowItems.IsEmpty || context.Sender.TryPost(0, (ushort)NotiPacketType.MAILBOX_ALARM,
                MailboxPackets.Alarm(), "pvp_mission_reward_mail", Current);
        });
    }
    internal bool Queue(GameConnectionContext context, bool update = false, Func<bool>? current = null)
    {
        if (book.Capture(context.Session) is not { } snapshot) return true;
        return context.Sender.TryPost(0, (ushort)(update ? NotiPacketType.UPDATE_PVP_MISSION : NotiPacketType.PVP_MISSION_LIST),
            PvpMissionPackets.List(snapshot.Rows), update ? "pvp_mission_update" : "pvp_mission_initialize",
            () => snapshot.Actor.IsCurrent() && (current?.Invoke() ?? true));
    }
}
