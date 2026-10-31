using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class AntonNormalSequenceProjection
{
    public static async Task RefreshAsync(GameConnectionContext context, Func<bool> guard)
    {
        if (context.Session.CaptureAntonNormalSequence() is { } receipt)
            await context.Notify(NotiPacketType.SEQUENTIAL_DUNGEON_INFO, AntonNormalSequencePackets.Information(receipt.Info),
                "anton_normal_sequence", () => guard() && context.Session.CanPublishAntonNormalSequence(receipt));
    }
}
