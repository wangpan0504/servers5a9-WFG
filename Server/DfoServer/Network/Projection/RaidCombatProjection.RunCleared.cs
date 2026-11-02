using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    internal static void RunCleared(GameConnectionContext context, AntonRaidBattle.LukeRunCleared cleared, RaidActor actor)
    {
        // 此包只有op4，不携旧位置；已提交事件不受退场/同团新revision影响。
        // 原actor、owner、选角epoch及Battle绑定仍须在写帧时成立，不能投到新角色/新团。
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_DUNGEON_PARTICIPATION_INFO,
            RaidCombatPackets.RunCleared(cleared.Dungeon, cleared.Members), "luke_run_cleared",
            () => context.Session.CanPublishLukeRunCleared(actor, cleared));
    }
}
