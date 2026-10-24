using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class GriefTowerProjection
{
    public static async Task SelectionAsync(GameConnectionContext context, DungeonCatalog? catalog,
        int legendDungeon, string eventName, Func<bool> guard)
    {
        var grief = catalog is null ? null : context.Session.GriefTowerSelection(catalog);
        if (grief is { } state)
            await context.Notify(NotiPacketType.TOWER_DUNGEON_CLEAR_INFO,GriefTowerPackets.ClearState(state.ClearedToday),
                "grief_tower_clear_state",guard);
        await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON,
            DungeonPackets.EmptySelectNotice(legendDungeon,grief?.Layer ?? 0,context.Session.DespairSelectionLayer()),eventName,guard);
    }
}
