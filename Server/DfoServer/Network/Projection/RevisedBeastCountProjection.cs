using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
namespace DfoServer.Network.Projection;

internal static class RevisedBeastCountProjection
{
    public static async Task RefreshAsync(GameConnectionContext context,DungeonRoomCatalog rooms,Func<bool>? guard=null)
    {
        if (context.Session.CaptureRevisedBeastCounts(rooms) is not { } receipt) return;
        await context.Notify(NotiPacketType.BEAST_DUNGEON_INCOUNT,DungeonPackets.RevisedBeastNativeIncount(receipt.Counts),
            "revised_beast_native_incount",()=> (guard?.Invoke() ?? true) && context.Session.CanPublishRevisedBeastCounts(receipt));
    }
}
