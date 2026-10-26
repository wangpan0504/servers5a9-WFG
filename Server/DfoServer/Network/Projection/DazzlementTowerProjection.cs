using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class DazzlementTowerProjection
{
    public static async Task BindingsAsync(GameConnectionContext context, Func<bool> guard)
    {
        if (context.Session.DazzlementTowerBindings() is not { } notice) return;
        await context.Notify(NotiPacketType.TOWER_OF_DAZZLEMENT_ENTER_CHARAC_LIST, DazzlementTowerPackets.Bindings(notice),
            "dazzlement_tower_bindings", () => guard() && context.Session.CanPublishDazzlementTower(notice));
    }
}
