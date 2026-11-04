using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class RiftDailyProjection
{
    // 专用1545以总额度及已用映射显示；550沿既有最多两次的日剩余显示，二者字段含义不同。
    public static async Task RefreshAsync(GameConnectionContext context, Func<bool>? guard = null, bool force = false)
    {
        if (context.Session.DaybreakEnterCount() is not { } count) return;
        if (!force && !context.Session.InDaybreakArea()) return;
        bool CanSend() => guard?.Invoke() ?? true;
        await context.Notify(NotiPacketType.CRACK_OF_DAYBREAK_INFO,
            RiftDailyPackets.Daybreak(RiftDailyPackets.DaybreakLimit, checked((uint)(RiftDailyPackets.DaybreakLimit - count.Daily))),
            "crack_of_daybreak_info", CanSend);
        await context.Notify(NotiPacketType.DUNGEON_ENTER_COUNT_INFO,
            DungeonEnterCountPackets.Info(count.Dungeon, count.Daily, 0), "crack_of_daybreak_count", CanSend);
    }
}
