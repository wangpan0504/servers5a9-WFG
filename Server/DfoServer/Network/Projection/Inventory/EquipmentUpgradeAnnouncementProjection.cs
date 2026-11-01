using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

internal sealed class EquipmentUpgradeAnnouncementProjection(OnlineSessionDirectory online, ItemCatalog items,
    int? reinforcementLevel, int? amplificationLevel, ServerLog log)
{
    public void Enhancement(GameConnectionContext context, InventoryResult result, EnhancementReceipt receipt) =>
        Publish(context, result, EquipmentUpgradeAnnouncement.Enhancement(result, receipt,
            receipt.Mode is EnhancementMode.Amplify or EnhancementMode.LuckyAmplify ? amplificationLevel : reinforcementLevel));

    public void Refinement(GameConnectionContext context, InventoryResult result, RefinementReceipt receipt) =>
        Publish(context, result, EquipmentUpgradeAnnouncement.Refinement(result, receipt));

    void Publish(GameConnectionContext context, InventoryResult result, EquipmentUpgradeAnnouncement? notice)
    {
        if (notice is null || result.Receipt is not { } committed) return;
        byte server = context.Channel.ServerIndex;
        var current = context.Session.CaptureServerBroadcastGuard(server, committed);
        if (current is null) return;
        byte[] body = EquipmentUpgradeAnnouncementPackets.Body(committed.Character.Name, items.Get(notice.Item.ItemId).Name, notice);
        int recipients = online.PostServerGroup(server, (ushort)NotiPacketType.SERVER_NOTICE_MESSAGE, body,
            "equipment_upgrade_announcement", current);
        log.Write("equipment_upgrade_announcement", new { Connection = context.Id, Character = committed.Character.Id,
            Server = server, notice.Kind, notice.Item.ItemId, notice.Level, notice.Succeeded, Recipients = recipients,
            Revision = committed.Character.Revision });
    }
}
