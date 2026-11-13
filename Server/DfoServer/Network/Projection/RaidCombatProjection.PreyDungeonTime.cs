using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    internal static void PostPreyDungeonTime(GameConnectionContext context, FormalDungeonReceipt scene)
    {
        if (scene.Entry.RaidEntry is not { Kind: RaidKind.Prey, PreyGuide: false }) return;
        // 原生单图时钟独立于597导航余时。加载和换房只恢复原Run余时，发送排队不延长期限。
        context.Sender.PostPrepared(0, (ushort)NotiPacketType.DUNGEON_LIMIT_TIME, 4,
            () => context.Session.PreyDungeonSeconds(scene) is { } seconds ? DungeonPackets.DungeonLimitTime(seconds) : null,
            "prey_dungeon_remaining", () => context.Session.PreyDungeonSeconds(scene) is > 0);
    }
}
