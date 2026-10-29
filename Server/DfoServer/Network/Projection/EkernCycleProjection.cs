using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class EkernCycleProjection
{
    public static async Task RefreshAsync(GameConnectionContext context, Func<bool>? guard = null)
    {
        if (context.Session.CaptureEkernCycle(entranceOnly:true) is not { } receipt) return;
        await context.Notify(NotiPacketType.DUNGEON_COUNTING_INFO,EkernCyclePackets.Info(receipt.Progress),"ekern_cycle_progress",
            () => (guard?.Invoke() ?? true) && context.Session.CanPublishEkernCycle(receipt));
    }
}
