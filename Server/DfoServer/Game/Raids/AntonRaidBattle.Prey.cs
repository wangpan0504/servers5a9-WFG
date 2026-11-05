using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    readonly Random preyRandom;
    static bool PreyBranchMatches(RaidDungeon dungeon, ImmutableDictionary<string, int> symbols) =>
        !dungeon.Declarations.Any(field => field.Tag == "[single type]" &&
            field.Values is [{ } value] && value.Number != symbols.GetValueOrDefault("[single raid type]"));
    // 042A7C00的资源枚举；与015B3830的AREA_DGN_TYPE消费者一致。
    static int PreyDungeonType(RaidDungeon dungeon) => dungeon.Declarations.FirstOrDefault(row => row.Tag == "[type]")?.Values
        .FirstOrDefault()?.Text switch
        {
            "magic tower" => 1, "garden" => 2, "big garden" => 3,
            "sanctuary" => 4, "slenikon defence" => 5, "boss" => 6, _ => 0
        };
    int PreyHeader(string tag, int index = 0) => script.Headers.Single(row => row.Tag == tag).Values[index].Number;
    ImmutableDictionary<string, int> InitializePreySymbols(int phase, ImmutableDictionary<string, int> symbols)
    {
        if (!PreyGuide) return InitializePreyTeamSymbols(phase, symbols);
        if (phase == 0)
        {
            // 单人布局在实例创建时冻结；两阶段沿原symbol继承，不把分支改成另一套圣地。
            symbols = symbols.SetItem("[single raid type]", preyRandom.Next(2)).SetItem("[slenikon hp]", PreyHeader("[max hp]"));
            // 本服布局策略：七个初始据点隔位分布，分支在实例内冻结；不声称是原服随机布局算法。
            int slot = 2;
            foreach (var dungeon in script.Phases[0].Dungeons.Where(row => PreyBranchMatches(row, symbols)))
            {
                symbols = symbols.SetItem($"[area_{slot}]", dungeon.Id).SetItem($"[area_{slot}_dgn_type]", PreyDungeonType(dungeon));
                slot += 2;
            }
        }
        return symbols;
    }

    // 普雷的ON CLEAR是正式Run事件，9999不是需要重复通关的次数。
    internal AntonRaidBattleChange? CompletePreyDungeon((RaidActor Actor, DungeonClearFact Fact)[] facts)
    {
        if (!PreyGuide) return CompletePreyTeamDungeon(facts);
        lock (gate)
        {
            if (Kind != RaidKind.Prey || facts is not [var row] || !row.Actor.IsCurrent ||
                current.PhaseExpired || LukePhaseDueLocked() || pendingTransition is not null ||
                !dungeonRuns.TryGetValue(row.Fact.Identity.InstanceId, out var run) || !run.Installed ||
                run.Active.Count != 1 || !run.Active.Contains(row.Actor) || run.Cleared || !CurrentObjective(run) ||
                row.Fact.DungeonId != run.Dungeon || row.Fact.ClearMap is null || row.Fact.ConfirmedDeathEvents.Count == 0 ||
                !entries.TryGetValue((run.Id, row.Actor.Character), out var entry) || !ReferenceEquals(entry.Run, run) ||
                row.Fact.Identity.CharacterId != entry.Identity.CharacterId || row.Fact.Identity.QuestId != entry.Identity.QuestId ||
                row.Fact.Identity.TaskAttempt != entry.Identity.TaskAttempt || row.Fact.Identity.Epoch < entry.Identity.Epoch) return null;
            int dungeon = checked((int)run.Dungeon);
            var definition = script.Phases[run.Phase].Dungeons.Single(value => value.Id == dungeon);
            var before = current;
            bool repeatable = definition.Declarations.Any(field => field.Tag == "[type]" &&
                field.Values is [{ Text: "slenikon defence" }]);
            var candidate = repeatable ? before : before with
            {
                Dungeons = before.Dungeons.SetItem(dungeon, RaidDungeonState.Clear),
                Symbols = before.Symbols.SetItem(definition.HpSymbol, 0)
            };
            candidate = RemovePreyArea(candidate, dungeon);
            candidate = EvaluatePrey(candidate, repeatable ? null : dungeon, definition.HpSymbol, null, dungeon);
            // 执行成功才消费本Run；同一死亡/117重放不能再触发产物、商人或阶段候选。
            RecordGuildClearParty([row.Actor.Character]);
            run.ClearFacts.Add(row.Actor, row.Fact);
            run.FreezeClearedMembers();
            var key = (run.Phase, row.Actor.Character);
            lukeParticipation[key] = checked(lukeParticipation.GetValueOrDefault(key) + 1);
            CommitBattleCandidate(before, candidate);
            return new(before, current);
        }
    }

    internal bool AllowsPreyEntry(int dungeon)
    {
        if (!PreyGuide) return AllowsPreyTeamEntry(dungeon);
        lock (gate) return Kind == RaidKind.Prey && pendingTransition is null && !TerminalRequested(current) &&
            !LukePhaseDueLocked() && current.Symbols.GetValueOrDefault("[prepare raid]") == 1 &&
            current.Dungeons.GetValueOrDefault(dungeon, RaidDungeonState.Hide) == RaidDungeonState.Open &&
            script.Phases[current.Phase].Dungeons.Any(row => row.Id == dungeon && PreyBranchMatches(row, current.Symbols) &&
                (PreyDungeonType(row) != 5 || Enumerable.Range(1, 16).Any(slot => current.Symbols[$"[area_{slot}]"] == dungeon)));
    }

    AntonRaidBattleView EvaluatePrey(AntonRaidBattleView state, int? dungeon, string? symbol, RaidTimer? timer, int? clear = null)
    {
        if (!PreyGuide) return EvaluatePreyTeam(state, dungeon, symbol, timer, clear);
        if (timer == new RaidTimer(9000, 0))
        {
            state = state with { Symbols = state.Symbols.SetItem("[prepare raid]", 1) };
            symbol = "[prepare raid]";
            timer = null;
        }
        if (timer == new RaidTimer(9001, 0))
        {
            if (state.Symbols["[slenikon invincibility]"] == 0)
                state = DamagePreyShip(state, PreyHeader("[hp decrease]", 1));
            if (state.PhaseExpired) return state;
            state = SetPreyTimer(state, new(9001, 0), PreyHeader("[hp decrease]"));
        }
        var events = new Queue<(int? Dungeon, string? Symbol, RaidTimer? Timer, int? Clear)>();
        events.Enqueue((dungeon, symbol, timer, clear));
        int budget = 4096;
        while (!TerminalRequested(state) && events.TryDequeue(out var changed))
        {
            if (--budget == 0) throw new InvalidDataException("普雷引导规则超过事件预算");
            foreach (var rule in script.Phases[state.Phase].Rules)
            {
                if (TerminalRequested(state)) break;
                if (!rule.Trigger.All(Matches) || rule.Trigger.All(clause => clause.Tag == "[if]") &&
                    (changed.Dungeon is not null || changed.Timer is not null || changed.Clear is not null ||
                        changed.Symbol is not null && !rule.Trigger.Any(clause => clause.Symbol == changed.Symbol))) continue;
                foreach (var action in rule.Behavior)
                {
                    if (TerminalRequested(state)) break;
                    switch (action.Tag)
                    {
                        case "[set]": Write(action.Symbol!, action.RawIntegers[0]); break;
                        case "[assign]": Write(action.Symbol!, state.Symbols[action.Operator!]); break;
                        case "[set dungeon state]": SetDungeon(action.DungeonId!.Value, action.State!.Value); break;
                        case "[set timer]":
                        case "[raid timer]":
                            var key = Timer(action);
                            state = SetPreyTimer(state, key, action.RawIntegers[2]);
                            break;
                        case "[reset timer]":
                        case "[reset raid timer]":
                            var reset = Timer(action);
                            state = state with { Timers = state.Timers.Remove(reset), TimerAnchors = state.TimerAnchors.Remove(reset),
                                TimerGenerations = state.TimerGenerations.Remove(reset) };
                            break;
                        case "[clear phase]":
                            state = state with { State = AntonRaidBattleState.Reward, Timers = state.Timers.Clear(),
                                TimerAnchors = state.TimerAnchors.Clear(), TimerGenerations = state.TimerGenerations.Clear() };
                            break;
                        case "[reserve spawn]":
                            int tower = action.DungeonId!.Value;
                            string prefix = tower == PreyHeader("[single magic tower 1]") ? "4993" : "4994";
                            int position = Enumerable.Range(1, 16).FirstOrDefault(slot => state.Symbols[$"[area_{slot}]"] == tower);
                            Write($"[{prefix} magic tower spawn position]", position == 0 ? 0 : position + 300);
                            Write($"[{prefix} magic tower spawn time]", checked((int)lukeClock.GetUtcNow().ToUnixTimeSeconds() + action.RawIntegers[1]));
                            break;
                        case "[spawn]":
                            state = SpawnPreyDefence(state, action.DungeonId!.Value);
                            break;
                        case "[slenikon]": state = AdvancePreyShip(state); break;
                        default: throw new InvalidDataException("未支持普雷引导行为：" + action.Tag);
                    }
                }
            }

            bool Matches(RaidClause clause) => clause.Tag switch
            {
                "[if]" => state.Symbols[clause.Symbol!] == clause.RawIntegers[0],
                "[on change symbol]" => changed.Symbol == clause.Symbol,
                "[on clear dungeon]" => changed.Clear == clause.DungeonId,
                "[on change dungeon state]" => changed.Dungeon == clause.DungeonId,
                "[check dungeon state]" => state.Dungeons[clause.DungeonId!.Value] == clause.State,
                "[check timer end]" or "[check raid timer end]" => changed.Timer == Timer(clause),
                _ => throw new InvalidDataException("未支持普雷引导触发器：" + clause.Tag)
            };
        }
        if (!TerminalRequested(state) && state.Symbols["[prepare raid]"] == 1 && !state.Timers.ContainsKey(new(9001, 0)))
            state = SetPreyTimer(state, new(9001, 0), PreyHeader("[hp decrease]"));
        return state;

        void Write(string key, int value)
        {
            if (state.Symbols[key] == value) return;
            state = state with { Symbols = state.Symbols.SetItem(key, value) };
            events.Enqueue((null, key, null, null));
        }
        void SetDungeon(int id, RaidDungeonState next)
        {
            if (state.Dungeons[id] == next) return;
            state = state with { Dungeons = state.Dungeons.SetItem(id, next) };
            if (next == RaidDungeonState.Open)
            {
                var definition = script.Phases[state.Phase].Dungeons.Single(row => row.Id == id);
                state = state with { DungeonGenerations = state.DungeonGenerations.SetItem(id,
                    checked(state.DungeonGenerations.GetValueOrDefault(id) + 1)) };
                Write(definition.HpSymbol, definition.HpRaw);
            }
            events.Enqueue((id, null, null, null));
        }
        // 两套同名计时器各有独立命名空间，避免raid timer(1,150)覆盖地图timer。
        static RaidTimer Timer(RaidClause clause) => clause.Tag.Contains("raid timer", StringComparison.Ordinal)
            ? new(checked(clause.Timer!.Kind + 10000), clause.Timer.DungeonId) : clause.Timer!;
    }

    AntonRaidBattleView AdvancePreyShip(AntonRaidBattleView state)
    {
        int from = state.Symbols["[slenikon to area]"];
        int first = state.Phase == 0 ? 301 : 317, last = state.Phase == 0 ? 316 : 320;
        int to = from < first || from >= last ? first : from + 1;
        if (state.Phase == 0 && from >= first && from <= last && state.Symbols[$"[area_{from - 300}_dgn_type]"] == 5 &&
            !dungeonRuns.Values.Any(run => run.Installed && !run.Cleared && run.Active.Count != 0 &&
                run.Dungeon == state.Symbols[$"[area_{from - 300}]"]))
        {
            int dungeon = state.Symbols[$"[area_{from - 300}]"];
            state = DamagePreyShip(state, PreyHeader("[single defence fail]"));
            state = RemovePreyArea(state, dungeon);
        }
        // 015BB8F0读取205后减029107D0；必须发截止秒，不能一直发5/10使剩余时间立即归零。
        return state with { Symbols = state.Symbols.SetItem("[slenikon from area]", from)
            .SetItem("[slenikon to area]", to).SetItem("[slenikon arrival time]",
                checked((int)lukeClock.GetUtcNow().ToUnixTimeSeconds() + state.Symbols["[slenikon speed]"])) };
    }

    AntonRaidBattleView SetPreyTimer(AntonRaidBattleView state, RaidTimer key, int seconds)
    {
        long generation = checked(state.TimerSequence + 1);
        return state with { Timers = state.Timers.SetItem(key, seconds),
            TimerGenerations = state.TimerGenerations.SetItem(key, generation),
            TimerAnchors = state.TimerAnchors.SetItem(key, lukeClock.GetTimestamp()), TimerSequence = generation };
    }

    AntonRaidBattleView RemovePreyArea(AntonRaidBattleView state, int dungeon)
    {
        for (int slot = 1; slot <= (state.Phase == 0 ? 16 : 20); slot++)
            if (state.Symbols[$"[area_{slot}]"] == dungeon)
                return state with { Symbols = state.Symbols.SetItem($"[area_{slot}]", 0)
                    .SetItem($"[area_{slot}_dgn_type]", PreyGuide ? 0 : 7) };
        return state;
    }

    AntonRaidBattleView DamagePreyShip(AntonRaidBattleView state, int damage)
    {
        if (state.Symbols["[slenikon invincibility]"] != 0) return state;
        int hp = Math.Max(0, state.Symbols["[slenikon hp]"] - damage);
        state = state with { Symbols = state.Symbols.SetItem("[slenikon hp]", hp).SetItem("[slenikon armor level]",
            hp > PreyHeader("[armor level hp boundary]") ? 0 : hp > PreyHeader("[armor level hp boundary]", 1) ? 1 : 2) };
        return hp != 0 ? state : state with { PhaseExpired = true,
            Dungeons = state.Dungeons.ToImmutableDictionary(row => row.Key, _ => RaidDungeonState.Hide),
            Timers = state.Timers.Clear(), TimerAnchors = state.TimerAnchors.Clear(), TimerGenerations = state.TimerGenerations.Clear() };
    }

    AntonRaidBattleView SpawnPreyDefence(AntonRaidBattleView state, int tower)
    {
        if (state.Phase != 0 || state.Dungeons.GetValueOrDefault(tower) != RaidDungeonState.Open) return state;
        int slot = Enumerable.Range(1, 16).FirstOrDefault(value => state.Symbols[$"[area_{value}]"] == 0);
        if (slot == 0) return state;
        var candidates = script.Phases[0].Dungeons.Where(row => PreyDungeonType(row) == 5).ToArray();
        int Weight(RaidDungeon row) => row.Declarations.Single(field => field.Tag == "[rate]").Values.Single().Number;
        int roll = preyRandom.Next(candidates.Sum(Weight));
        var selected = candidates.First(row => (roll -= Weight(row)) < 0);
        return state with { Symbols = state.Symbols.SetItem($"[area_{slot}]", selected.Id).SetItem($"[area_{slot}_dgn_type]", 5) };
    }

    internal uint? PreyTimerSeconds(AntonRaidBattleView view, RaidTimer timer)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Prey || !ReferenceEquals(current, view) || TerminalRequested(current) ||
                timer.Kind is not (1 or 2) || !view.Timers.TryGetValue(timer, out int seconds) ||
                !view.TimerAnchors.TryGetValue(timer, out long anchor)) return null;
            double remaining = seconds - lukeClock.GetElapsedTime(anchor).TotalSeconds;
            return remaining > 0 ? checked((uint)Math.Ceiling(remaining)) : null;
        }
    }
}
