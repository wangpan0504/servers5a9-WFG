using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    int preyTeamInitialIsysHp;

    ImmutableDictionary<string, int> InitializePreyTeamSymbols(int phase, ImmutableDictionary<string, int> symbols)
    {
        symbols = symbols.SetItem("[slenikon hp]", PreyHeader("[max hp]"))
            .SetItem("[slenikon armor level]", 0);
        if (phase != 0) return symbols;
        // 原生TYPE 0是普通据点；空区域用7关闭卡片，不能只清DGN或投影Hide。
        for (int slot = 1; slot <= 20; slot++)
            if (symbols.GetValueOrDefault($"[area_{slot}]") == 0)
                symbols = symbols.SetItem($"[area_{slot}_dgn_type]", 7);
        preyTeamInitialIsysHp = script.Phases.SelectMany(row => row.Rules).SelectMany(row => row.Behavior)
            .FirstOrDefault(action => action.Tag == "[set]" && action.Symbol == "[isys hp]" &&
                action.RawIntegers is [{ } value] && value > 0)?.RawIntegers[0] ?? 0;
        // 四圣地固定在1/5/9/13；开局两处阻击按原RATE选不同模板，后续仍由魔塔生成。
        int[] sanctuaries = [1, 5, 9, 13];
        int[] positions = Enumerable.Range(1, 16).Except(sanctuaries).ToArray();
        preyRandom.Shuffle(positions);
        int Weight(RaidDungeon row) => row.Declarations.Single(field => field.Tag == "[rate]").Values.Single().Number;
        var defences = script.Phases[0].Dungeons.Where(row => PreyDungeonType(row) == 5 &&
            row.InitialState == RaidDungeonState.Open && Weight(row) > 0).ToList();
        var initialDefences = new HashSet<int>();
        while (initialDefences.Count < 2 && defences.Count != 0)
        {
            int roll = preyRandom.Next(defences.Sum(Weight));
            var selected = defences.First(row => (roll -= Weight(row)) < 0);
            initialDefences.Add(selected.Id);
            defences.Remove(selected);
        }
        int at = 0, sanctuary = 0;
        foreach (var dungeon in script.Phases[0].Dungeons)
        {
            if (PreyDungeonType(dungeon) == 5 && !initialDefences.Contains(dungeon.Id)) continue;
            bool fixedSanctuary = PreyDungeonType(dungeon) == 4;
            if (fixedSanctuary ? sanctuary == sanctuaries.Length : at == positions.Length)
                throw new InvalidDataException("普雷据点超过战略图区域容量");
            int slot = fixedSanctuary ? sanctuaries[sanctuary++] : positions[at++];
            symbols = symbols.SetItem($"[area_{slot}]", dungeon.Id)
                .SetItem($"[area_{slot}_dgn_type]", PreyDungeonType(dungeon));
        }
        return symbols;
    }

    internal bool AllowsPreyTeamEntry(int dungeon)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Prey || PreyGuide || pendingTransition is not null || TerminalRequested(current) ||
                LukePhaseDueLocked() || current.Symbols.GetValueOrDefault("[prepare raid]") != 1 ||
                current.Dungeons.GetValueOrDefault(dungeon, RaidDungeonState.Hide) != RaidDungeonState.Open) return false;
            var definition = script.Phases[current.Phase].Dungeons.SingleOrDefault(row => row.Id == dungeon);
            return definition is not null && (PreyDungeonType(definition) != 5 ||
                Enumerable.Range(1, 16).Any(slot => current.Symbols.GetValueOrDefault($"[area_{slot}]") == dungeon));
        }
    }

    internal AntonRaidBattleChange? CompletePreyTeamDungeon((RaidActor Actor, DungeonClearFact Fact)[] facts)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Prey || PreyGuide || pendingTransition is not null || TerminalRequested(current) ||
                LukePhaseDueLocked() || facts.Length is < 1 or > 4 ||
                facts.Select(row => row.Actor).Distinct().Count() != facts.Length ||
                !dungeonRuns.TryGetValue(facts[0].Fact.Identity.InstanceId, out var run) || !run.Installed ||
                run.Cleared || facts.Length != run.Active.Count || !CurrentObjective(run) ||
                current.PreyExpelledRuns.Contains(run.Id) || PreyDungeonTimeDueLocked(run)) return null;
            foreach (var row in facts)
                if (!row.Actor.IsCurrent || !run.Active.Contains(row.Actor) || row.Fact.Identity.InstanceId != run.Id || row.Fact.DungeonId != run.Dungeon ||
                    row.Fact.ClearMap is null || row.Fact.ConfirmedDeathEvents.Count == 0 ||
                    !entries.TryGetValue((run.Id, row.Actor.Character), out var entry) || !ReferenceEquals(entry.Run, run) ||
                    row.Fact.Identity.CharacterId != entry.Identity.CharacterId || row.Fact.Identity.QuestId != entry.Identity.QuestId ||
                    row.Fact.Identity.TaskAttempt != entry.Identity.TaskAttempt || row.Fact.Identity.Epoch < entry.Identity.Epoch ||
                    row.Fact.Identity.Epoch < run.MapEpochs.GetValueOrDefault(row.Actor)) return null;
            int dungeon = checked((int)run.Dungeon);
            var definition = script.Phases[run.Phase].Dungeons.Single(row => row.Id == dungeon);
            var before = current;
            // 防御与二阶段的9999是资源占位，真实Run的清图事件只消费一次。
            bool repeatable = run.Phase != 0 || PreyDungeonType(definition) == 5;
            var candidate = repeatable ? before : before with
            {
                Dungeons = before.Dungeons.SetItem(dungeon, RaidDungeonState.Clear),
                Symbols = before.Symbols.SetItem(definition.HpSymbol, 0)
            };
            if (run.Phase == 0 && PreyDungeonType(definition) == 2)
                candidate = candidate with { Symbols = candidate.Symbols.SetItem("[clear garden]",
                    script.Phases[0].Dungeons.Count(row => PreyDungeonType(row) == 2 &&
                        candidate.Dungeons.GetValueOrDefault(row.Id) == RaidDungeonState.Clear)) };
            if (PreyDungeonType(definition) == 5) candidate = RemovePreyArea(candidate, dungeon);
            candidate = EvaluatePreyTeam(candidate, repeatable ? null : dungeon, definition.HpSymbol, null, dungeon);
            RecordGuildClearParty(facts.Select(row => row.Actor.Character));
            foreach (var row in facts)
            {
                run.ClearFacts.Add(row.Actor, row.Fact);
                var key = (run.Phase, row.Actor.Character);
                int count = lukeParticipation.GetValueOrDefault(key);
                lukeParticipation[key] = count == int.MaxValue ? count : count + 1;
            }
            run.FreezeClearedMembers();
            CommitBattleCandidate(before, candidate);
            return new(before, current);
        }
    }

    internal AntonRaidBattleChange? ObservePreyTeamRun(Guid instance,
        (RaidActor Actor, DungeonRunIdentity Identity)[] actors, int? map = null)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Prey || PreyGuide || pendingTransition is not null || TerminalRequested(current) ||
                LukePhaseDueLocked() || !dungeonRuns.TryGetValue(instance, out var run) || !run.Installed ||
                run.Cleared || !CurrentObjective(run) || current.PreyExpelledRuns.Contains(instance) || PreyDungeonTimeDueLocked(run) ||
                actors.Length == 0 || actors.Length != run.Active.Count ||
                actors.Select(row => row.Actor).Distinct().Count() != actors.Length ||
                actors.Any(row => !row.Actor.IsCurrent || !run.Active.Contains(row.Actor) ||
                    !entries.TryGetValue((instance, row.Actor.Character), out var entry) || !ReferenceEquals(entry.Run, run) ||
                    row.Identity.InstanceId != instance || row.Identity.CharacterId != row.Actor.Character ||
                    row.Identity.QuestId != entry.Identity.QuestId || row.Identity.TaskAttempt != entry.Identity.TaskAttempt ||
                    row.Identity.Epoch < entry.Identity.Epoch || row.Identity.Epoch < run.MapEpochs.GetValueOrDefault(row.Actor))) return null;
            bool enter = !run.EnterConsumed;
            bool changedMap = map is not null && actors.Any(row => row.Identity.Epoch > run.MapEpochs.GetValueOrDefault(row.Actor));
            if (!enter && !changedMap) return null;
            var before = current;
            var candidate = before;
            if (enter)
            {
                if (PreyDungeonType(script.Phases[run.Phase].Dungeons.Single(row => row.Id == run.Dungeon)) == 6)
                    candidate = candidate with { Symbols = candidate.Symbols.SetItem("[isys hp in dungeon]", candidate.Symbols["[isys hp]"]) };
                candidate = EvaluatePreyTeam(candidate, null, null, null, stageFact:
                    new(RaidStageFactKind.EnterDungeon, run.Phase, checked((int)run.Dungeon)));
            }
            if (changedMap && !TerminalRequested(candidate))
                candidate = EvaluatePreyTeam(candidate, null, null, null, stageFact:
                    new(RaidStageFactKind.EnterMap, run.Phase, checked((int)run.Dungeon), map!.Value));
            run.EnterConsumed = true;
            if (map is not null) foreach (var row in actors) run.MapEpochs[row.Actor] = row.Identity.Epoch;
            CommitBattleCandidate(before, candidate);
            return ReferenceEquals(before, current) ? null : new(before, current);
        }
    }

    AntonRaidBattleView GiveUpPreyTeamDungeon(AntonRaidBattleView state, int dungeon)
    {
        var definition = script.Phases[state.Phase].Dungeons.Single(row => row.Id == dungeon);
        if (PreyDungeonType(definition) == 5 &&
            Enumerable.Range(1, 16).Any(slot => state.Symbols.GetValueOrDefault($"[area_{slot}]") == dungeon))
            state = DamagePreyShip(state, PreyHeader("[defence fail]"));
        if (state.Phase == 0 && PreyDungeonType(definition) == 1)
            // 失败立即在本塔预告区生成阻击；原资源周期仍由500计时器推进。
            state = SpawnPreyTeamDefence(state, dungeon);
        return EvaluatePreyTeam(state, null, null, null,
            stageFact: new(RaidStageFactKind.GiveUpDungeon, state.Phase, dungeon));
    }

    // 仅接受调用方已确认的原正式Boss房快照与归一化剩余HP；本入口不解释网络字段或签发死亡事实。
    internal AntonRaidBattleChange? ReportPreyTeamBossPercent(RaidActor actor, DungeonRunSnapshot snapshot, int percent)
    {
        if (percent is < 0 or > 100) return null;
        return ReportPreyTeamBossValue(actor, snapshot, percent * 100);
    }

    internal bool PreyPreparationPending(AntonRaidBattleView view)
    {
        lock (gate) return Kind == RaidKind.Prey && ReferenceEquals(current, view) &&
            view.State == AntonRaidBattleState.Fighting && !view.PhaseExpired && !view.ClockFaulted && view.Phase == 0 &&
            view.Symbols.GetValueOrDefault("[prepare raid]") != 1;
    }

    // 原客户端在594倒数/飞鸟就绪后发送36；该事件提前完成准备，不等待导航备用期限。
    internal AntonRaidBattleChange? CompletePreyBoardingPreparation(RaidActor actor)
    {
        lock (gate)
        {
            if (!actor.IsCurrent || !members.ContainsKey(actor) || !PreyPreparationPending(current)) return null;
            return ExpireTimer(Instance, current.Revision, new(9000, 0));
        }
    }

    // 原资源准备期限仅作无登船回调时的备用计时；原生可视倒数由594播放。
    internal uint? PreyPreparationSeconds(AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Prey || !ReferenceEquals(current, view) ||
                TerminalRequested(current) || current.Phase != 0 ||
                current.Symbols.GetValueOrDefault("[prepare raid]") == 1 ||
                !current.Timers.TryGetValue(new RaidTimer(9000, 0), out int seconds) ||
                !current.TimerAnchors.TryGetValue(new RaidTimer(9000, 0), out long anchor)) return null;
            double remaining = seconds - lukeClock.GetElapsedTime(anchor).TotalSeconds;
            return remaining > 0 ? checked((uint)Math.Ceiling(remaining)) : null;
        }
    }

    internal AntonRaidBattleChange? ReportPreyTeamBossValue(RaidActor actor, DungeonRunSnapshot snapshot, int value)
    {
        if (value is < 0 or > 10000 || preyTeamInitialIsysHp <= 0) return null;
        // 客户端百分比取整为0仍可能有存活HP；最终0只能来自原正式清图事实。
        int remaining = checked((int)((long)preyTeamInitialIsysHp * value / 10000));
        return ReportPreyTeamBossHp(actor, snapshot, Math.Max(1, remaining));
    }

    internal AntonRaidBattleChange? ReportPreyTeamBossHp(RaidActor actor, DungeonRunSnapshot snapshot, int remainingHp)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Prey || PreyGuide || !actor.IsCurrent || pendingTransition is not null ||
                TerminalRequested(current) || LukePhaseDueLocked() || snapshot.Phase != SinglePlayerDungeonPhase.Dungeon ||
                snapshot.CurrentMap.Kind != "boss" || !dungeonRuns.TryGetValue(snapshot.Identity.InstanceId, out var run) ||
                !run.Installed || run.Cleared || !run.Active.Contains(actor) || !CurrentObjective(run) ||
                current.PreyExpelledRuns.Contains(run.Id) || PreyDungeonTimeDueLocked(run) || !run.MapEpochs.TryGetValue(actor, out long epoch) ||
                snapshot.Identity.Epoch != epoch || !entries.TryGetValue((run.Id, actor.Character), out var entry) ||
                !ReferenceEquals(entry.Run, run) || snapshot.Identity.CharacterId != actor.Character ||
                snapshot.Identity.QuestId != entry.Identity.QuestId || snapshot.Identity.TaskAttempt != entry.Identity.TaskAttempt ||
                PreyDungeonType(script.Phases[run.Phase].Dungeons.Single(row => row.Id == run.Dungeon)) != 6 ||
                remainingHp <= 0 || remainingHp >= current.Symbols["[isys hp in dungeon]"] ||
                remainingHp > current.Symbols["[isys hp]"]) return null;
            var before = current;
            var candidate = before with { Symbols = before.Symbols.SetItem("[isys hp in dungeon]", remainingHp) };
            CommitBattleCandidate(before, EvaluatePreyTeam(candidate, null, "[isys hp in dungeon]", null));
            return new(before, current);
        }
    }

    AntonRaidBattleView EvaluatePreyTeam(AntonRaidBattleView state, int? dungeon, string? symbol, RaidTimer? timer,
        int? clear = null, RaidStageFact? stageFact = null)
    {
        if (timer == new RaidTimer(9000, 0))
        {
            state = state with { Symbols = state.Symbols.SetItem("[prepare raid]", 1) };
            symbol = "[prepare raid]";
            timer = null;
        }
        if (timer == new RaidTimer(9001, 0))
        {
            state = DamagePreyShip(state, PreyHeader("[hp decrease]", state.Phase));
            if (state.PhaseExpired) return ExpelAll(state);
            state = SetPreyTimer(state, new(9001, 0), 1);
        }
        var events = new Queue<(int? Dungeon, string? Symbol, RaidTimer? Timer, int? Clear, RaidStageFact? Fact, int? Kicked)>();
        events.Enqueue((dungeon, symbol, timer, clear, stageFact, null));
        int budget = 4096;
        while (!TerminalRequested(state) && events.TryDequeue(out var changed))
        {
            if (--budget == 0) throw new InvalidDataException("普雷普通规则超过事件预算");
            foreach (var rule in script.Phases[state.Phase].Rules)
            {
                if (TerminalRequested(state)) break;
                if (!rule.Trigger.All(Matches) || rule.Trigger.All(clause => clause.Tag == "[if]") &&
                    (changed.Dungeon is not null || changed.Timer is not null || changed.Clear is not null ||
                        changed.Fact is not null || changed.Kicked is not null ||
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
                        case "[raid timer]": state = SetPreyTimer(state, Timer(action), action.RawIntegers[2]); break;
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
                        case "[reserve spawn]": state = ReservePreyTeamSpawn(state, action.DungeonId!.Value, action.RawIntegers[1]); break;
                        case "[spawn]": state = SpawnPreyTeamDefence(state, action.DungeonId!.Value); break;
                        case "[slenikon]": state = AdvancePreyTeamShip(state); break;
                        case "[kick out dungeon]":
                            int target = action.DungeonId!.Value;
                            state = state with { PreyExpelledRuns = state.PreyExpelledRuns.Union(dungeonRuns.Values
                                .Where(run => run.Installed && run.Phase == state.Phase && run.Dungeon == target && !run.Cleared)
                                .Select(run => run.Id)) };
                            events.Enqueue((null, null, null, null, null, target));
                            break;
                        default: throw new InvalidDataException("未支持普雷普通行为：" + action.Tag);
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
                "[on enter dungeon]" => changed.Fact is { Kind: RaidStageFactKind.EnterDungeon } entered && entered.Dungeon == clause.DungeonId,
                "[on enter map]" => changed.Fact is { Kind: RaidStageFactKind.EnterMap } enteredMap && enteredMap.Map == clause.MapId,
                "[on giveup dungeon]" => changed.Fact is { Kind: RaidStageFactKind.GiveUpDungeon } givenUp && givenUp.Dungeon == clause.DungeonId,
                "[on kick out dungeon]" => changed.Kicked == clause.DungeonId,
                _ => throw new InvalidDataException("未支持普雷普通触发器：" + clause.Tag)
            };
        }
        if (state.PhaseExpired) return ExpelAll(state);
        if (!TerminalRequested(state) && state.Symbols["[prepare raid]"] == 1 && !state.Timers.ContainsKey(new(9001, 0)))
            state = SetPreyTimer(state, new(9001, 0), 1);
        return state;

        void Write(string key, int value)
        {
            if (state.Symbols[key] == value) return;
            state = state with { Symbols = state.Symbols.SetItem(key, value) };
            for (int slot = 1; slot <= 20; slot++)
                if (key == $"[area_{slot}]")
                {
                    int type = value == 0 ? 7 : script.Phases[state.Phase].Dungeons.FirstOrDefault(row => row.Id == value) is { } row ? PreyDungeonType(row) : 0;
                    state = state with { Symbols = state.Symbols.SetItem($"[area_{slot}_dgn_type]", type) };
                    break;
                }
            events.Enqueue((null, key, null, null, null, null));
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
            events.Enqueue((id, null, null, null, null, null));
        }
        static RaidTimer Timer(RaidClause clause) => clause.Tag.Contains("raid timer", StringComparison.Ordinal)
            ? new(checked(clause.Timer!.Kind + 10000), clause.Timer.DungeonId) : clause.Timer!;
    }

    AntonRaidBattleView ExpelAll(AntonRaidBattleView state) => state with
    {
        PreyExpelledRuns = state.PreyExpelledRuns.Union(dungeonRuns.Values.Where(run => run.Installed).Select(run => run.Id))
    };

    AntonRaidBattleView AdvancePreyTeamShip(AntonRaidBattleView state)
    {
        int departed = state.Symbols["[slenikon from area]"];
        int from = state.Symbols["[slenikon to area]"];
        int first = state.Phase == 0 ? 301 : 317, last = state.Phase == 0 ? 316 : 320;
        int to = state.Phase == 0 ? from < first || from >= last ? first : from + 1 :
            // 二阶段每经过一个外侧据点都返回本体；旧FROM保留上一个外侧据点，无需另存航线游标。
            from == 320 ? departed is >= 317 and < 319 ? departed + 1 : 317 : 320;
        return state with { Symbols = state.Symbols.SetItem("[slenikon from area]", from).SetItem("[slenikon to area]", to)
            .SetItem("[slenikon arrival time]", checked((int)lukeClock.GetUtcNow().ToUnixTimeSeconds() + state.Symbols["[slenikon speed]"])) };
    }

    AntonRaidBattleView ReservePreyTeamSpawn(AntonRaidBattleView state, int tower, int seconds)
    {
        int slot = ChoosePreyTeamSpawnSlot(state, tower);
        return state with { Symbols = state.Symbols.SetItem($"[{tower} magic tower spawn position]", slot == 0 ? 0 : slot + 300)
            .SetItem($"[{tower} magic tower spawn time]", checked((int)lukeClock.GetUtcNow().ToUnixTimeSeconds() + seconds)) };
    }

    AntonRaidBattleView SpawnPreyTeamDefence(AntonRaidBattleView state, int tower)
    {
        if (state.Phase != 0 || state.Dungeons.GetValueOrDefault(tower) != RaidDungeonState.Open) return state;
        int slot = state.Symbols.GetValueOrDefault($"[{tower} magic tower spawn position]") - 300;
        if (!PreyTeamSpawnSlotFree(state, tower, slot))
            slot = ChoosePreyTeamSpawnSlot(state, tower);
        if (slot == 0) return state;
        var candidates = script.Phases[0].Dungeons.Where(row => PreyDungeonType(row) == 5 &&
            !Enumerable.Range(1, 16).Any(value => state.Symbols[$"[area_{value}]"] == row.Id)).ToArray();
        if (candidates.Length == 0) return state;
        int Weight(RaidDungeon row) => row.Declarations.Single(field => field.Tag == "[rate]").Values.Single().Number;
        int roll = preyRandom.Next(candidates.Sum(Weight));
        var selected = candidates.First(row => (roll -= Weight(row)) < 0);
        return state with { Symbols = state.Symbols.SetItem($"[area_{slot}]", selected.Id).SetItem($"[area_{slot}_dgn_type]", 5) };
    }

    int ChoosePreyTeamSpawnSlot(AntonRaidBattleView state, int tower)
    {
        int[] slots = Enumerable.Range(1, 16).Where(value => PreyTeamSpawnSlotFree(state, tower, value)).ToArray();
        return slots.Length == 0 ? 0 : slots[preyRandom.Next(slots.Length)];
    }

    bool PreyTeamSpawnSlotFree(AntonRaidBattleView state, int tower, int slot) =>
        slot is >= 1 and <= 16 && state.Symbols.GetValueOrDefault($"[area_{slot}]") == 0 &&
        // 已公布的下一波位置仍是预留，即使对应AREA尚未实际生成，也不能被另一座塔占用。
        !script.Phases[0].Dungeons.Any(other => other.Id != tower && PreyDungeonType(other) == 1 &&
            state.Symbols.GetValueOrDefault($"[{other.Id} magic tower spawn position]") == slot + 300);
}
