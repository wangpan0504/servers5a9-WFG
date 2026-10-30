using DfoServer.Game.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class OrdinaryLukeSequenceProjection
{
    public static async Task RefreshAsync(GameConnectionContext context, Func<bool> guard)
    {
        if (context.Session.CaptureOrdinaryLukeSequence(OrdinaryLukeProgress.SequenceId) is { } receipt)
            await context.Notify(NotiPacketType.SEQUENTIAL_DUNGEON_INFO, OrdinaryLukeSequencePackets.Information(receipt.Info),
                "ordinary_luke_sequence", () => guard() && context.Session.CanPublishOrdinaryLukeSequence(receipt));
    }
}
