using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    readonly LukeRaidPacingPolicy? lukePacing;

    bool AllowsLukeThroneLocked(int dungeon) => lukePacing is null || dungeon is not (5061 or 5062) || !current.LukeEnergyBlocked;

    AntonRaidBattleView ApplyLukeEnergyPacing(AntonRaidBattleView state, int? dungeon, RaidTimer? timer, RaidStageFact? fact)
    {
        if (lukePacing is null || state.Phase != 1) return state;
        if (timer == new RaidTimer(1, 5066))
            return state with { LukeEnergyBlocked = true, LukeEnergyOverloadEpoch = checked(state.LukeEnergyOverloadEpoch + 1) };
        if (dungeon == 5066 && state.Dungeons[5066] == RaidDungeonState.Clear)
            state = state with { LukeEnergyBlocked = false };
        if (state.LukeEnergyActivated || fact is not { Kind: RaidStageFactKind.EnterDungeon, Dungeon: 5061 }) return state;
        // 入王座事实先激活时钟，随后同一次规则求值开放储藏室并设置计时。
        // 已开放的兼容夹具仍沿同一个锚点初始化，不按人数切换规则。
        if (state.Dungeons.GetValueOrDefault(5066) != RaidDungeonState.Open)
            return state with { LukeEnergyActivated = true };
        var attack = new RaidTimer(1, 5066);
        long generation = checked(state.TimerSequence + 1);
        return state with { LukeEnergyActivated = true, TimerSequence = generation,
            Timers = state.Timers.SetItem(attack, lukePacing.StorageAttackSeconds),
            TimerGenerations = state.TimerGenerations.SetItem(attack, generation),
            TimerAnchors = state.TimerAnchors.SetItem(attack, lukeClock.GetTimestamp()) };
    }

    internal Guid? LukeEnergyReturnRun(RaidActor actor)
    {
        lock (gate) return !actor.IsCurrent || !members.ContainsKey(actor) ? null : dungeonRuns.Values
            .SingleOrDefault(run => run.Installed && run.EnergyReturnEpoch != 0 && run.Active.Contains(actor))?.Id;
    }

    internal bool RequiresLukeEnergyReturn(RaidActor actor, Guid run)
    {
        lock (gate) return actor.IsCurrent && members.ContainsKey(actor) &&
            dungeonRuns.TryGetValue(run, out var currentRun) && currentRun.Installed &&
            currentRun.EnergyReturnEpoch != 0 && currentRun.Phase == current.Phase && currentRun.Active.Contains(actor);
    }
}
