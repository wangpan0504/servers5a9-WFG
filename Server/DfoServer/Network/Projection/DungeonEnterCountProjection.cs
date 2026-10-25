using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class DungeonEnterCountProjection
{
    public static async Task RefreshAsync(GameConnectionContext context, DungeonCatalog? catalog, Func<bool>? guard = null,
        bool onlyForSelection = false)
    {
        if (catalog is null || context.Session.CaptureDungeonEnterCounts(catalog.EnterCounts, onlyForSelection) is not { } receipt) return;
        bool CanSend() => (guard?.Invoke() ?? true) && context.Session.CanPublishDungeonEnterCounts(receipt);
        foreach (var count in receipt.Counts)
            await context.Notify(NotiPacketType.DUNGEON_ENTER_COUNT_INFO, DungeonEnterCountPackets.Remaining(count),
                "dungeon_enter_count", CanSend);
    }
}
