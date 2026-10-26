using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class RaidCombatProjection
{
    public static void Notice(GameConnectionContext context, AntonRaidBattleView view, RaidActor actor)
    {
        bool Guard() => context.Session.CanPublishRaidBattle(actor, view);
        // 卢克首个Run策略尚无奖励记录，不能借安徒恩投影打开空窗口。
        if (view.State == AntonRaidBattleState.Reward && context.Session.CurrentRaidBattle(actor)?.Kind == RaidKind.Luke &&
            context.Session.RaidCombatResult(view) is null) return;
        // 601先安装结果记录；587=3随后打开读取该记录的窗口（023B3086、0239727C）。
        if (view.State == AntonRaidBattleState.Reward && context.Session.RaidCombatResult(view) is { } result)
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_RESULT, RaidCombatPackets.Result(result), "raid_phase_result", Guard);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_STATE, RaidCombatPackets.State(view), "raid_phase_state", Guard);
    }
}
