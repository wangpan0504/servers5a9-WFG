using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    internal static void AntonTimersForScene(GameConnectionContext context, Func<bool> current, bool replayState = true)
    {
        if (context.Session.AntonRaidTimerSnapshot() is not { } snapshot) return;
        bool Guard() => current() && context.Session.CanPublishRaidBattle(snapshot.Actor, snapshot.View);
        PostAntonTimers(context, snapshot.Battle, snapshot.View, Guard);
        if (snapshot.View.State != AntonRaidBattleState.Fighting) return;
        if (replayState) PostState(context, snapshot.View, snapshot.Actor, Guard, "anton_scene_phase_state");
        else
        {
            // 668只恢复服务端绑定，原生恢复握手仍保留原拒绝；仅补已闭合的时间投影。
            context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_SET_TIMER, 13,
                () => snapshot.Battle.LukePhaseSeconds(snapshot.View.Instance, snapshot.View.Phase,
                    snapshot.View.PhaseClockGeneration) is { } seconds
                    ? RaidCombatPackets.AntonWaitTimer(seconds, checked((uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds))) : null,
                "anton_rejoin_phase_timer", Guard);
            context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_REMAIN_TIME, 5,
                () => snapshot.Battle.LukePhaseSeconds(snapshot.View.Instance, snapshot.View.Phase,
                    snapshot.View.PhaseClockGeneration) is { } seconds ? RaidCombatPackets.PhaseRemaining(seconds) : null,
                "anton_rejoin_phase_remaining", Guard);
        }
    }

    static void PostAntonTimers(GameConnectionContext context, AntonRaidBattle battle,
        AntonRaidBattleView view, Func<bool> current)
    {
        if (!battle.HasAntonRewards) return;
        context.Owner.Execute(() =>
        {
            foreach (var timer in context.AntonTimerDisplays.Where(key => !view.Timers.ContainsKey(key)).ToArray())
            {
                // 01DD0810在end<now时拒绝读取。发送已经结束的1秒区间，避免kind2零分母。
                context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_SET_TIMER, 13,
                    () =>
                    {
                        context.AntonTimerDisplays.Remove(timer);
                        return RaidCombatPackets.AntonDungeonTimer(timer.Kind, timer.DungeonId, 1,
                            checked((uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1)));
                    }, "anton_dungeon_timer_clear", () => current() && battle.IsCurrent(view));
            }
            foreach (var timer in view.Timers.Keys)
            {
                long generation = view.TimerGenerations.GetValueOrDefault(timer);
                context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_SET_TIMER, 13,
                    () =>
                    {
                        if (battle.LukeRuleTimerSeconds(view.Instance, view.Phase, timer, generation) is not { } seconds) return null;
                        context.AntonTimerDisplays.Add(timer);
                        return RaidCombatPackets.AntonDungeonTimer(timer.Kind, timer.DungeonId, seconds,
                            checked((uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds)));
                    }, "anton_dungeon_timer", () => current() && battle.IsCurrent(view));
            }
            return true;
        });
    }
}
