using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class FatigueProjection
{
    public static void PostCurrent(GameConnectionContext context)
    {
        if (context.Session.CaptureFatigueDisplay() is not { } receipt) return;
        context.Sender.Post(0, (ushort)NotiPacketType.FATIGUE, FatiguePackets.Current(receipt.State),
            "fatigue_debug_changed", () => !context.Stop.IsCancellationRequested && context.Session.CanPublishFatigueDisplay(receipt));
    }
}
