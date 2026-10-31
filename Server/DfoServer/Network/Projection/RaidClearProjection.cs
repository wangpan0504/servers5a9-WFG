using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class RaidClearProjection
{
    internal static void Refresh(GameConnectionContext context, Func<bool>? current = null, bool initialize = false)
    {
        context.Owner.Execute(() =>
        {
            if (current?.Invoke() == false || context.Session.CaptureRaidClearCounts(context.RaidClearLukeCycle) is not { } receipt) return false;
            var body = RaidPackets.WeeklyClearInfo(receipt.Counts.Select(row =>
                new RaidPackets.WeeklyClear(row.Type, row.DailyUsed, row.WeeklyUsed)).ToArray());
            bool Guard() => current?.Invoke() != false && context.Session.CanPublishRaidClearCounts(receipt, context.RaidClearLukeCycle) &&
                (initialize || context.RaidClearDisplay is not { } previous || previous.Epoch != receipt.Epoch || !body.AsSpan().SequenceEqual(previous.Body));
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_WEEKLY_CLEAR_INFO, body, "raid_weekly_clear_snapshot", Guard,
                () => context.RaidClearDisplay = (receipt.Epoch, body));
            return true;
        });
    }
}
