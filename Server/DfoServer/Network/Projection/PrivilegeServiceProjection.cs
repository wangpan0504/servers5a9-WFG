using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class PrivilegeServiceProjection
{
    public static async Task PostAsync(GameConnectionContext context, DfoServer.Game.Inventory.Models.InventoryCommitReceipt? expected = null,
        bool initialization = false)
    {
        if (context.Session.CapturePrivilegeService(initialization) is not { } snapshot) return;
        bool Current() => context.Session.CanPublishPrivilegeService(snapshot, initialization) &&
            (expected is null || context.Session.CanPublishInventory(expected));
        using var publication = context.Sender.QueuePublication(Current);
        await context.Notify(NotiPacketType.PRIVILEGE_SERVICE_BUY_CHARAC_VIEW, PrivilegeServicePackets.Roster(snapshot),
            "privilege_character_roster", Current);
        await context.Notify(NotiPacketType.PRIVILEGE_SERVICE_BUY_INFO,
            PrivilegeServicePackets.Information(snapshot.Receipt.Character.Inventory.PrivilegeService, snapshot.At),
            "privilege_service_info", Current);
    }
}
