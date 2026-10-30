using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 本服计时合同沿LukeRaidClocks/LukeRules的FromSeconds，不宣称已证明客户端转换单位。
    // 随机孵化沿86已实现策略，候选、计时和线上布局使用95资源及接收合同。
    void ExpireDueAntonClockLocked()
    {
        var timers = current.Timers.Where(row => current.TimerAnchors.TryGetValue(row.Key, out long anchor) &&
            lukeClock.GetElapsedTime(anchor) >= TimeSpan.FromSeconds(row.Value))
            .Select(row => (row.Key, Generation: current.TimerGenerations.GetValueOrDefault(row.Key))).ToArray();
        var deferred = current.Deferred.Where(row => row.Value.Tag == "[reserve dungeon state]" &&
            current.DeferredAnchors.TryGetValue(row.Key, out long anchor) &&
            lukeClock.GetElapsedTime(anchor) >= TimeSpan.FromSeconds(row.Value.RawIntegers[^1]))
            .Select(row => (row.Key, Generation: current.DeferredGenerations[row.Key])).ToArray();
        foreach (var row in timers)
        {
            if (TerminalRequested(current) || pendingTransition is not null) return;
            if (current.TimerGenerations.GetValueOrDefault(row.Key) != row.Generation) continue;
            var before = current;
            CommitBattleCandidate(before, Evaluate(before with { Timers = before.Timers.Remove(row.Key),
                TimerAnchors = before.TimerAnchors.Remove(row.Key), TimerGenerations = before.TimerGenerations.Remove(row.Key) },
                null, null, row.Key));
        }
        foreach (var row in deferred)
        {
            if (TerminalRequested(current) || pendingTransition is not null) return;
            if (current.DeferredGenerations.GetValueOrDefault(row.Key) != row.Generation) continue;
            _ = CompleteDeferred(Instance, current.Revision, row.Key);
        }
        foreach (var token in current.Deferred.Where(row => row.Value.Tag == "[open random hatchery]").Select(row => row.Key).ToArray())
        {
            if (TerminalRequested(current) || pendingTransition is not null) return;
            OpenAntonHatcheriesLocked(token);
        }
    }

    readonly Random antonHatcheryRandom;

    void OpenAntonHatcheriesLocked(int token)
    {
        if (!antonPhaseProgress || current.Phase != 1 || !current.Deferred.ContainsKey(token)) return;
        var info = script.Headers.SingleOrDefault(row => row.Tag == "[hatchery info]");
        // 86策略仅覆盖四选三；其它配置保留待决，不能套用硬编码候选。
        if (info is null || info.Values.Length != 6 || info.Values[0].Number != 4 || info.Values[1].Number != 3) return;
        var candidates = info.Values.Skip(2).Select(row => row.Number).ToArray();
        if (candidates.Any(id => !current.Dungeons.ContainsKey(id)) || candidates.Distinct().Count() != 4 ||
            !current.Symbols.ContainsKey("[infection dungeon index]") || !current.Symbols.ContainsKey("[is exist infection dungeon]")) return;
        int omitted = antonHatcheryRandom.Next(4);
        var opened = candidates.Where((_, index) => index != omitted).ToArray();
        int infected = opened[antonHatcheryRandom.Next(opened.Length)];
        var before = current;
        var next = before with { Deferred = before.Deferred.Remove(token),
            DeferredAnchors = before.DeferredAnchors.Remove(token), DeferredGenerations = before.DeferredGenerations.Remove(token),
            Symbols = before.Symbols.SetItem("[infection dungeon index]", infected) };
        foreach (int id in opened)
        {
            var definition = script.Phases[next.Phase].Dungeons.Single(row => row.Id == id);
            next = Evaluate(next with { Dungeons = next.Dungeons.SetItem(id, RaidDungeonState.Open),
                Symbols = next.Symbols.SetItem(definition.HpSymbol, definition.HpRaw),
                DungeonGenerations = next.DungeonGenerations.SetItem(id, checked(next.DungeonGenerations.GetValueOrDefault(id) + 1)) }, id, null, null);
        }
        CommitBattleCandidate(before, next);
    }
}
