using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed record LukeTimerDisplay(Guid Instance, int Phase, long Generation);

internal static partial class RaidCombatProjection
{
    static void PostLukeTimers(GameConnectionContext context, AntonRaidBattle battle,
        AntonRaidBattleView view, Func<bool> current)
    {
        context.Owner.Execute(() =>
        {
            // 只记本连接实际写出的原生显示键；不拥有能源进度，不另建时钟。
            foreach (var (timer, displayed) in context.LukeTimerDisplays.ToArray())
            {
                if (displayed.Instance == view.Instance && displayed.Phase == view.Phase &&
                    view.TimerGenerations.ContainsKey(timer)) continue;
                bool Clear() => current() && battle.IsCurrent(view) &&
                    context.LukeTimerDisplays.GetValueOrDefault(timer) == displayed;
                context.Sender.Post(0, (ushort)NotiPacketType.RAID_SET_TIMER,
                    RaidCombatPackets.LukeExpiredTimer(checked((sbyte)timer.Kind), timer.DungeonId),
                    "luke_rule_timer_clear", Clear, () => context.LukeTimerDisplays.Remove(timer));
            }
            foreach (var timer in view.Timers.Keys.Where(timer => timer.Kind is 1 or 2))
            {
                var display = new LukeTimerDisplay(view.Instance, view.Phase, view.TimerGenerations.GetValueOrDefault(timer));
                bool Show() => current() && battle.IsCurrent(view) &&
                    context.LukeTimerDisplays.GetValueOrDefault(timer) != display;
                // 同一代只写一次：原生每次收596都把起点改成now，普通同步不能反复重启进度条。
                context.Sender.PostPrepared(0, (ushort)NotiPacketType.RAID_SET_TIMER, 13,
                    () => battle.LukeRuleTimerSeconds(view.Instance, view.Phase, timer, display.Generation) is { } seconds
                        ? RaidCombatPackets.LukePositiveTimer(checked((sbyte)timer.Kind), timer.DungeonId, 0, seconds) : null,
                    "luke_rule_timer", Show, written: () => context.LukeTimerDisplays[timer] = display);
            }
            return true;
        });
    }
}
