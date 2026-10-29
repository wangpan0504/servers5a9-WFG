using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class DungeonEnterCountProjection
{
    public static async Task RefreshAsync(GameConnectionContext context, DungeonCatalog? catalog, Func<bool>? guard = null,
        bool onlyForSelection = false)
    {
        if (catalog is null) return;
        await EkernCycleProjection.RefreshAsync(context,guard);
        if (context.Session.CaptureDungeonEnterCounts(catalog.EnterCounts, onlyForSelection) is { } receipt)
            await PublishAsync(context, receipt, guard);
        await RefreshGriefAsync(context, catalog, guard, onlyForSelection);
        await RefreshDespairAsync(context, catalog, guard, onlyForSelection);
    }

    public static async Task RefreshGriefAsync(GameConnectionContext context, DungeonCatalog? catalog, Func<bool>? guard = null,
        bool onlyForSelection = false)
    {
        if (catalog is not null && context.Session.CaptureGriefTowerEnterCounts(catalog.GriefTower, onlyForSelection) is { } receipt)
            await PublishAsync(context, receipt, guard);
    }

    public static async Task RefreshDespairAsync(GameConnectionContext context, DungeonCatalog? catalog, Func<bool>? guard = null,
        bool onlyForSelection = false)
    {
        if (catalog is not null && context.Session.CaptureDespairTowerEnterCounts(catalog.Despair, onlyForSelection) is { } receipt)
            await PublishAsync(context, receipt, guard);
    }

    static async Task PublishAsync(GameConnectionContext context, DungeonEnterCountReceipt receipt, Func<bool>? guard)
    {
        bool CanSend() => (guard?.Invoke() ?? true) && context.Session.CanPublishDungeonEnterCounts(receipt);
        foreach (var count in receipt.Counts)
            await context.Notify(NotiPacketType.DUNGEON_ENTER_COUNT_INFO, DungeonEnterCountPackets.Remaining(count),
                "dungeon_enter_count", CanSend);
    }
}
