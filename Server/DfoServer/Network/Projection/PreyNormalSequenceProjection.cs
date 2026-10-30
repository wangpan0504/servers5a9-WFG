using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class PreyNormalSequenceProjection
{
    public static async Task RefreshAsync(GameConnectionContext context, Func<bool> guard)
    {
        if (context.Session.CapturePreyNormalSequence() is { } receipt)
            await context.Notify(NotiPacketType.SEQUENTIAL_DUNGEON_INFO, PreyNormalSequencePackets.Information(receipt.Info),
                "prey_normal_sequence", () => guard() && context.Session.CanPublishPreyNormalSequence(receipt));
    }
}
