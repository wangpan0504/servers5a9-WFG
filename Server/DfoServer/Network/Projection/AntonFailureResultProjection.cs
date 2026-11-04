using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 本连接仅保留一个失败结果身份；不拥有失败资格、资产或退场事务。
internal sealed class AntonFailureResultProjection
{
    RaidCombatDisplayIdentity? writtenIdentity;

    // true表示原失败601已实际写出；false表示仍在排队或当前身份不允许展示，均不得提前离团。
    internal bool Post(GameConnectionContext context, RaidActor actor, AntonRaidBattleView view) => context.Owner.Execute(() =>
    {
        if (!view.PhaseExpired || view.ClockFaulted ||
            context.Session.AntonFailureResult(actor, view.Instance, view.Phase) is not { } result) return false;
        var identity = RaidCombatDisplayIdentity.Capture(actor, view);
        if (writtenIdentity == identity) return true;
        bool Current() => writtenIdentity != identity &&
            context.Session.AntonFailureResult(actor, view.Instance, view.Phase) == result;
        // 失败也使用Post的写前守卫；不能让QueuePublication仅保留外层会话条件。
        context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_RESULT, RaidCombatPackets.Result(result),
            "anton_phase_failed_result", Current, written: () => writtenIdentity = identity, afterWritten: () =>
            {
                // 已退出本人owner，才安排可能取得全分队门禁的原退场执行器。
                if (context.Session.AntonFailureResult(actor, view.Instance, view.Phase) == result &&
                    context.Session.CurrentRaidBattle(actor) is { } battle)
                    context.LukePhaseExit?.Notice(battle.View, actor);
            });
        return false;
    });
}
