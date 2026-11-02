using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class RiftDailyProjection
{
    // 1545 carries current remaining plus the 530 -> 2 cap pair; 550 remains for the generic count field.
    public static async Task RefreshAsync(GameConnectionContext context, Func<bool>? guard = null, bool force = false)
    {
        if (context.Session.DaybreakEnterCount() is not { } count) return;
        if (!force && !context.Session.InDaybreakArea()) return;
        bool CanSend() => guard?.Invoke() ?? true;
        await context.Notify(NotiPacketType.CRACK_OF_DAYBREAK_INFO,
            RiftDailyPackets.Daybreak(count.Daily, RiftDailyPackets.DaybreakLimit), "crack_of_daybreak_info", CanSend);
        await context.Notify(NotiPacketType.DUNGEON_ENTER_COUNT_INFO,
            DungeonEnterCountPackets.Info(count.Dungeon, count.Daily, 0), "crack_of_daybreak_count", CanSend);
    }
}