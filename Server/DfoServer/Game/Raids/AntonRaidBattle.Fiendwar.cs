using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    readonly FiendwarScript? fiendwarDefinition;
    readonly int fiendwarGuideRoute;
    internal int FiendwarBaseArea => fiendwarDefinition?.Script.WaitingRoom?.Area ?? 2;
    readonly FiendwarBattlefield? fiendwarEngine;
    readonly FiendwarPacing fiendwarPolicy;
    readonly Random fiendwarMazeRandom;
    readonly FiendwarFacilityCatalog? fiendwarFacilityCatalog;
    readonly Dictionary<Guid, (int Area, FiendwarCombatScale Scale, DateTimeOffset Started, DateTimeOffset Deadline)> fiendwarEncounters = [];
    readonly Dictionary<int, (long Generation, long Enemy, long Monster, int Ordinal)> fiendwarMazes = [];
    readonly Dictionary<ushort, FiendwarMovement> fiendwarMovements = [];
    readonly HashSet<int> fiendwarAreaTravels = [];

    internal FiendwarScript? FiendwarDefinition => fiendwarDefinition;
    internal DateTimeOffset FiendwarNow => lukeClock.GetUtcNow();

    internal bool AllowsFiendwarMerchant(RaidActor actor, int area)
    {
        lock (gate)
        {
            var now = lukeClock.GetUtcNow();
            return Kind == RaidKind.Fiendwar && fiendwarDefinition is { Guide: false } && lukeClockActive &&
                actor.IsCurrent && members.ContainsKey(actor) && pendingTransition is null && !TerminalRequested(current) &&
                current.Fiendwar is { Failed: false, Cleared: false, Merchant: { } merchant } field &&
                now < field.Deadline && now < merchant.End && merchant.Area == area &&
                current.FiendwarPositions.GetValueOrDefault(actor.Character, FiendwarBaseArea) == area &&
                !fiendwarMovements.ContainsKey(actor.Character) && !dungeonRuns.Values.Any(run => run.Active.Contains(actor));
        }
    }

    void RemoveFiendwarPosition(ushort character)
    {
        if (!current.FiendwarPositions.TryGetValue(character, out int area) || current.Fiendwar is not { } field) return;
        var positions = current.FiendwarPositions.Remove(character);
        current = ProjectFiendwar(current with { FiendwarPositions = positions, Revision = current.Revision + 1 },
            fiendwarEngine!.Occupy(field, area, positions.Values.Contains(area), lukeClock.GetUtcNow()).State);
    }

    // 初次激活只平移全部已冻结事件，不再次抽取怪物、商人或分配。
    void AnchorFiendwarClockLocked()
    {
        if (current.Fiendwar is not { } field) return;
        var now = lukeClock.GetUtcNow();
        lukePhaseAnchor = lukeClock.GetTimestamp();
        var shift = now - field.Started;
        current = current with { Fiendwar = field with { Started = now, Deadline = field.Deadline + shift,
            Timers = field.Timers.ToImmutableDictionary(row => row.Key,
                row => row.Value with { Started = row.Value.Started + shift, End = row.Value.End + shift }),
            Merchant = field.Merchant is { } merchant ? merchant with { End = merchant.End + shift } : null } };
    }

    AntonRaidBattleView InitializeFiendwar()
    {
        var field = fiendwarEngine!.Create(Instance, lukeClock.GetUtcNow(),
            unchecked((ulong)Random.Shared.NextInt64(1, long.MaxValue)), fiendwarGuideRoute).State;
        return ProjectFiendwar(new(Instance, 0, 0, AntonRaidBattleState.Fighting, field.Symbols,
            ImmutableDictionary<int, RaidDungeonState>.Empty, ImmutableDictionary<RaidTimer, int>.Empty), field);
    }

    AntonRaidBattleView ProjectFiendwar(AntonRaidBattleView before, FiendwarFieldState field)
    {
        // 原脚本只恢复设施符号；把已证0→1与DGN重新开放放进同一候选。
        if (!field.Failed && !field.Cleared && fiendwarFacilityCatalog is { } catalog)
            foreach (var facility in catalog.Areas.Values)
                if (facility.RestoreSymbol is { } symbol && before.Fiendwar is not null &&
                    before.Symbols.GetValueOrDefault(symbol) == 0 && field.Symbols.GetValueOrDefault(symbol) == 1)
                    field = field with { Dungeons = field.Dungeons.SetItem(facility.Dungeon, "open") };
        var dungeons = field.Dungeons.ToImmutableDictionary(row => row.Key, row => row.Value switch
        { "open" => RaidDungeonState.Open, "clear" => RaidDungeonState.Clear, "close" => RaidDungeonState.Close, _ => RaidDungeonState.Hide });
        // 原漩涡以区域及Boss符号控制本体门，不会把初始hide改成普通选图open。
        // 正式Run准入仍使用统一目录，因此只在三兵营成果锁存后投影这两处真实入口。
        foreach (int area in fiendwarDefinition!.Guide ? Array.Empty<int>() : new[] { 30, 31 })
        {
            int dungeon = fiendwarDefinition!.Areas[area].DungeonId;
            if (field.Symbols.GetValueOrDefault("[movie command center on]") == 1 &&
                dungeons.GetValueOrDefault(dungeon) != RaidDungeonState.Clear)
                dungeons = dungeons.SetItem(dungeon, RaidDungeonState.Open);
        }
        var generations = before.DungeonGenerations;
        foreach (var row in dungeons)
            if (row.Value == RaidDungeonState.Open && before.Dungeons.GetValueOrDefault(row.Key, RaidDungeonState.Hide) != RaidDungeonState.Open)
                generations = generations.SetItem(row.Key, checked(generations.GetValueOrDefault(row.Key) + 1));
        var facilities = before.FiendwarFacilities;
        if (!field.Failed && !field.Cleared && fiendwarFacilityCatalog is { } resource)
            foreach (var definition in resource.Areas.Values.OrderBy(row => row.Area))
            {
                if (dungeons.GetValueOrDefault(definition.Dungeon) != RaidDungeonState.Open)
                { facilities = facilities.Remove(definition.Area); continue; }
                long generation = generations.GetValueOrDefault(definition.Dungeon);
                if (facilities.TryGetValue(definition.Area, out var existing) && existing.Target.Generation == generation) continue;
                // 本服沿现有每代均匀独立抽取语义；重发/选图/重进不再消耗随机数。
                var maze = definition.Mazes[fiendwarMazeRandom.Next(definition.Mazes.Length)];
                long serial = checked((uint)(field.NextUnit + 1));
                field = field with { NextUnit = serial };
                facilities = facilities.SetItem(definition.Area, new(new(Instance, definition.Area, definition.Dungeon,
                    generation, serial, serial, 0, maze.Ordinal), maze.Boss, maze.Kind));
            }
        var expired = before.FiendwarExpiredRuns;
        var bridges = before.FiendwarBridgeReturns;
        var encounterTimers = before.FiendwarDungeonTimers;
        if (!field.Failed && !field.Cleared)
            foreach (var run in dungeonRuns.Values.Where(run => !run.Cleared && run.FiendwarTarget is not null))
            {
                var target = run.FiendwarTarget!;
                var presence = field.Areas[target.Area];
                bool matches = generations.GetValueOrDefault(target.Dungeon) == target.Generation &&
                    dungeons.GetValueOrDefault(target.Dungeon) == RaidDungeonState.Open &&
                    (target.FacilitySerial != 0 ? facilities.TryGetValue(target.Area, out var facility) && facility.Target == target :
                        target.EnemySerial == (presence.Enemy?.Serial ?? 0) && target.MonsterSerial == (presence.Monster?.Serial ?? 0));
                if (matches) continue;
                // 已预留但未SQL提交者由最终guard回滚；已提交者完整安装后沿原Run安全退出。
                expired = expired.Add(run.Id);
                encounterTimers = encounterTimers.Remove(target.Area);
            }
        return before with { Fiendwar = field, Symbols = field.Symbols, Dungeons = dungeons,
            FiendwarFacilities = facilities,
            FiendwarExpiredRuns = expired, FiendwarBridgeReturns = bridges,
            FiendwarDungeonTimers = field.Failed || field.Cleared ? encounterTimers.Clear() : encounterTimers,
            DungeonGenerations = generations, PhaseExpired = field.Failed,
            State = field.Cleared ? AntonRaidBattleState.Reward : before.State };
    }

    internal int FiendwarPosition(ushort character)
    { lock (gate) return current.FiendwarPositions.GetValueOrDefault(character, FiendwarBaseArea); }

    internal bool AllowsFiendwarEntry(int dungeon)
    {
        lock (gate) return Kind == RaidKind.Fiendwar && current.Fiendwar is { Failed: false, Cleared: false } field &&
            lukeClock.GetUtcNow() < field.Deadline && pendingTransition is null && !TerminalRequested(current) &&
            current.Dungeons.GetValueOrDefault(dungeon, RaidDungeonState.Hide) == RaidDungeonState.Open &&
            FiendwarAreaForDungeon(dungeon) is { } area &&
            FiendwarBossEntryOpen(area) &&
            (field.Areas[area].Enemy is not null || field.Areas[area].Monster is not null ||
                fiendwarDefinition!.Areas[area].Roles.Any(role => role != "[ally area]"));
    }

    internal sealed class FiendwarMovement : IDisposable
    {
        readonly AntonRaidBattle battle;
        readonly RaidActor actor;
        internal int Destination { get; }
        bool finished;
        internal FiendwarMovement(AntonRaidBattle battle, RaidActor actor, int destination)
        { this.battle = battle; this.actor = actor; Destination = destination; }
        internal AntonRaidBattleChange Install()
        {
            lock (battle.gate)
            {
                if (finished || battle.fiendwarMovements.GetValueOrDefault(actor.Character) != this)
                    throw new InvalidOperationException("漩涡移动预留不再属于本次提交");
                var before = battle.current;
                var positions = before.FiendwarPositions.SetItem(actor.Character, Destination);
                int origin = before.FiendwarPositions.GetValueOrDefault(actor.Character, battle.FiendwarBaseArea);
                var field = battle.fiendwarEngine!.Occupy(before.Fiendwar!, origin, positions.Values.Contains(origin), battle.lukeClock.GetUtcNow()).State;
                field = battle.fiendwarEngine.Occupy(field, Destination, true, battle.lukeClock.GetUtcNow()).State;
                battle.current = battle.ProjectFiendwar(before with { FiendwarPositions = positions, Revision = before.Revision + 1 }, field);
                battle.fiendwarMovements.Remove(actor.Character); finished = true;
                battle.ScheduleLukeClockLocked();
                return new(before, battle.current);
            }
        }
        public void Dispose()
        {
            lock (battle.gate)
                if (!finished && battle.fiendwarMovements.GetValueOrDefault(actor.Character) == this)
                { battle.fiendwarMovements.Remove(actor.Character); finished = true; }
        }
    }

    internal sealed class FiendwarAreaMovement(AntonRaidBattle battle, int source, int destination,
        FiendwarMovement[] movements) : IDisposable
    {
        bool installed;
        bool disposed;
        internal bool IsCurrent
        {
            get
            {
                lock (battle.gate) return !installed && !disposed && battle.fiendwarAreaTravels.Contains(source) &&
                    battle.fiendwarAreaTravels.Contains(destination) && movements.All(move =>
                        battle.fiendwarMovements.Values.Contains(move)) &&
                    battle.current.Fiendwar is { Failed: false, Cleared: false } battlefield &&
                    battle.lukeClock.GetUtcNow() < battlefield.Deadline && !TerminalRequested(battle.current);
            }
        }
        internal void Install()
        {
            lock (battle.gate)
            {
                // SQL已提交之后只核对预留身份；截止计时不能使半队停留在旧内存位置。
                foreach (var movement in movements) movement.Install();
                installed = true;
            }
        }
        public void Dispose()
        {
            lock (battle.gate)
            {
                if (disposed) return;
                disposed = true;
                foreach (var movement in movements) movement.Dispose();
                battle.fiendwarAreaTravels.Remove(source);
                battle.fiendwarAreaTravels.Remove(destination);
            }
        }
    }

    internal FiendwarAreaMovement? ReserveFiendwarAreaMovement(RaidActor[] actors, int source, int destination)
    {
        lock (gate)
        {
            if (actors.Length is < 1 or > 4 || actors.Select(actor => actor.Character).Distinct().Count() != actors.Length ||
                source == destination || fiendwarAreaTravels.Contains(source) || fiendwarAreaTravels.Contains(destination) ||
                fiendwarMovements.Values.Any(move => move.Destination == source || move.Destination == destination) ||
                destination is not (30 or 31) && current.FiendwarPositions.Any(row => row.Value == destination) ||
                actors.Any(actor => current.FiendwarPositions.GetValueOrDefault(actor.Character, -1) != source))
                return null;
            var held = new List<FiendwarMovement>();
            foreach (var actor in actors)
            {
                if (ReserveFiendwarMovement(actor, destination) is not { } movement)
                {
                    foreach (var previous in held) previous.Dispose();
                    return null;
                }
                held.Add(movement);
            }
            // 预留覆盖出发地与目的地，SQL期间其它请求不能改变本次自动组队名单。
            fiendwarAreaTravels.Add(source);
            fiendwarAreaTravels.Add(destination);
            return new(this, source, destination, held.ToArray());
        }
    }

    // 位置SQL之前预留名额；领域gate不跨SQL持有。失败Dispose不改位置或重新抽样。
    internal FiendwarMovement? ReserveFiendwarMovement(RaidActor actor, int destination)
    {
        lock (gate)
        {
            if (!actor.IsCurrent || !members.ContainsKey(actor) || Kind != RaidKind.Fiendwar ||
                current.Fiendwar is not { Failed: false, Cleared: false } field || TerminalRequested(current) ||
                lukeClock.GetUtcNow() >= field.Deadline || !fiendwarDefinition!.Areas.TryGetValue(destination, out var target) ||
                dungeonRuns.Values.Any(run => run.Active.Contains(actor)) || fiendwarMovements.ContainsKey(actor.Character)) return null;
            int origin = current.FiendwarPositions.GetValueOrDefault(actor.Character, FiendwarBaseArea);
            if (fiendwarAreaTravels.Contains(origin) || fiendwarAreaTravels.Contains(destination)) return null;
            if (origin != destination && !fiendwarDefinition.Areas[origin].Neighbours.Contains(destination)) return null;
            if (fiendwarDefinition.Guide)
            {
                if (destination == (fiendwarGuideRoute == 0 ? 40 : 38)) return null;
                var source = fiendwarDefinition.Areas[origin];
                var presence = field.Areas[origin];
                if (destination > origin && source.DungeonId > 0 &&
                    (presence.Enemy is not null || presence.Monster is not null ||
                     source.Roles.Any(role => role is "[radar area]" or "[battery area]") &&
                     field.Dungeons.GetValueOrDefault(source.DungeonId) is not ("clear" or "hide"))) return null;
            }
            if (destination is 30 or 31 && field.Symbols.GetValueOrDefault("[movie command center on]") != 1) return null;
            // 黑白桥允许两队交接，副本容量仍由遭遇预留和原分队的四人上限约束。
            int maximum = destination is 30 or 31 ? 8 : Math.Min(4, target.MemberMax);
            if (destination != FiendwarBaseArea && current.FiendwarPositions.Count(row => row.Key != actor.Character &&
                (fiendwarMovements.GetValueOrDefault(row.Key)?.Destination ?? row.Value) == destination) >= maximum)
                return null;
            if (fiendwarEncounters.Values.Any(row => row.Area == destination)) return null;
            var movement = new FiendwarMovement(this, actor, destination);
            fiendwarMovements.Add(actor.Character, movement);
            return movement;
        }
    }

    internal AntonRaidBattleChange? MoveFiendwar(RaidActor actor, int destination)
    {
        using var move = ReserveFiendwarMovement(actor, destination);
        return move?.Install();
    }

    internal int? FiendwarAreaForDungeon(int dungeon) => fiendwarDefinition?.Areas.Values
        .Where(row => row.DungeonId == dungeon).Select(row => (int?)row.Id).SingleOrDefault();

    internal int? FiendwarMaze(int dungeon, IReadOnlyList<int> candidates)
    {
        lock (gate)
        {
            if (!AllowsFiendwarEntry(dungeon) || candidates.Count == 0) return null;
            int area = FiendwarAreaForDungeon(dungeon)!.Value;
            if (current.FiendwarFacilities.TryGetValue(area, out var facility))
                return candidates.Contains(facility.Target.MazeOrdinal) ? facility.Target.MazeOrdinal : null;
            var presence = current.Fiendwar!.Areas[area];
            long generation = current.DungeonGenerations.GetValueOrDefault(dungeon), enemy = presence.Enemy?.Serial ?? 0,
                monster = presence.Monster?.Serial ?? 0;
            if (fiendwarMazes.TryGetValue(dungeon, out var existing) && existing.Generation == generation && existing.Enemy == enemy && existing.Monster == monster)
                return candidates.Contains(existing.Ordinal) ? existing.Ordinal : null;
            int chosen;
            if (fiendwarDefinition!.Guide)
            {
                // 原引导DGN两套固定迷宫按周内路线排列，设施仅一套；不套团队随机选择。
                chosen = candidates.Count == 1 ? candidates[0] : candidates.Contains(fiendwarGuideRoute) ? fiendwarGuideRoute : -1;
                if (chosen < 0) return null;
            }
            else if (candidates.Count == 6)
            {
                // normal DGN前3迷宫为三类军团，随后两类中立怪，最后为精英军团。
                int ordinal = presence.Enemy is { } unit ? unit.Kind switch { 1 => 0, 2 => 1, 3 => 2, 4 => 5, _ => -1 } :
                    presence.Monster is { } neutral ? neutral.Kind switch { 1 => 3, 2 => 4, _ => -1 } : -1;
                if (!candidates.Contains(ordinal)) return null;
                chosen = ordinal;
            }
            else chosen = candidates[fiendwarMazeRandom.Next(candidates.Count)];
            fiendwarMazes[dungeon] = (generation, enemy, monster, chosen);
            return chosen;
        }
    }

    internal FiendwarDungeonTarget? FiendwarTarget(int dungeon, IReadOnlyList<int> candidates)
    {
        lock (gate)
        {
            if (FiendwarMaze(dungeon, candidates) is not { } ordinal || FiendwarAreaForDungeon(dungeon) is not { } area) return null;
            if (current.FiendwarFacilities.TryGetValue(area, out var facility)) return facility.Target;
            var presence = current.Fiendwar!.Areas[area];
            return new(Instance, area, dungeon, current.DungeonGenerations.GetValueOrDefault(dungeon), 0,
                presence.Enemy?.Serial ?? 0, presence.Monster?.Serial ?? 0, ordinal);
        }
    }

    internal bool IsFiendwarTargetCurrent(FiendwarDungeonTarget? target, int dungeon, int? ordinal)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Fiendwar) return target is null;
            return AllowsFiendwarEntry(dungeon) && MatchesFiendwarTarget(target, dungeon, ordinal);
        }
    }

    // 已入场Run继续核对原目标代次；本体结算关门不能撤销当前场景的死亡成果。
    bool MatchesFiendwarTarget(FiendwarDungeonTarget? target, int dungeon, int? ordinal)
    {
        if (target is null || target.Instance != Instance || target.Dungeon != dungeon || target.MazeOrdinal != ordinal ||
            current.Fiendwar is null || FiendwarAreaForDungeon(dungeon) != target.Area ||
            current.Dungeons.GetValueOrDefault(dungeon) != RaidDungeonState.Open ||
            current.DungeonGenerations.GetValueOrDefault(dungeon) != target.Generation) return false;
        if (current.FiendwarFacilities.TryGetValue(target.Area, out var facility)) return facility.Target == target;
        var presence = current.Fiendwar!.Areas[target.Area];
        return target.FacilitySerial == 0 && target.EnemySerial == (presence.Enemy?.Serial ?? 0) &&
            target.MonsterSerial == (presence.Monster?.Serial ?? 0) &&
            fiendwarMazes.TryGetValue(dungeon, out var choice) && choice.Generation == target.Generation &&
            choice.Enemy == target.EnemySerial && choice.Monster == target.MonsterSerial && choice.Ordinal == target.MazeOrdinal;
    }

    internal bool IsFiendwarRunCurrent(Guid runId)
    {
        lock (gate) return dungeonRuns.TryGetValue(runId, out var run) &&
            !current.FiendwarExpiredRuns.Contains(runId) && fiendwarEncounters.TryGetValue(runId, out var encounter) &&
            lukeClock.GetUtcNow() < encounter.Deadline &&
            MatchesFiendwarTarget(run.FiendwarTarget, checked((int)run.Dungeon), run.MazeOrdinal);
    }

    bool ReserveFiendwarEncounter(RaidDungeonRun run)
    {
        if (Kind != RaidKind.Fiendwar) return true;
        if (FiendwarAreaForDungeon(checked((int)run.Dungeon)) is not { } area || !AllowsFiendwarEntry(checked((int)run.Dungeon)) ||
            fiendwarEncounters.Values.Any(row => row.Area == area) ||
            run.Actors.Any(row => current.FiendwarPositions.GetValueOrDefault(row.Actor.Character, FiendwarBaseArea) != area)) return false;
        var now = lukeClock.GetUtcNow();
        var deadline = now.AddSeconds(checked(fiendwarDefinition!.Areas[area].LimitSeconds * fiendwarPolicy.DungeonLimit));
        if (deadline > current.Fiendwar!.Deadline) deadline = current.Fiendwar.Deadline;
        fiendwarEncounters.Add(run.Id, (area, area is 30 or 31 ? fiendwarBossScale! : new(run.Actors.Select(row => row.Actor.Character), original: fiendwarDefinition.Guide), now, deadline));
        ScheduleLukeClockLocked();
        return true;
    }

    internal FiendwarCombatScale? FiendwarScale(Guid run)
    { lock (gate) return fiendwarEncounters.TryGetValue(run, out var encounter) ? encounter.Scale : null; }

    internal AntonRaidBattleChange? CompleteFiendwarDungeon((RaidActor Actor, DungeonClearFact Fact)[] facts)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Fiendwar || pendingTransition is not null || TerminalRequested(current) || facts.Length is < 1 or > 4 ||
                !dungeonRuns.TryGetValue(facts[0].Fact.Identity.InstanceId, out var run) || !run.Installed || run.Cleared ||
                current.FiendwarExpiredRuns.Contains(run.Id) || !MatchesFiendwarTarget(run.FiendwarTarget, checked((int)run.Dungeon), run.MazeOrdinal) ||
                run.Active.Count != facts.Length || facts.Select(row => row.Actor).Distinct().Count() != facts.Length ||
                !fiendwarEncounters.TryGetValue(run.Id, out var encounter) || lukeClock.GetUtcNow() >= encounter.Deadline) return null;
            foreach (var row in facts)
                if (!row.Actor.IsCurrent || !run.Active.Contains(row.Actor) || row.Fact.DungeonId != run.Dungeon ||
                    row.Fact.ClearMap is null || row.Fact.ConfirmedDeathEvents.Count == 0 ||
                    !entries.TryGetValue((run.Id, row.Actor.Character), out var binding) || !ReferenceEquals(binding.Run, run) ||
                    row.Fact.Identity.CharacterId != binding.Identity.CharacterId || row.Fact.Identity.InstanceId != binding.Identity.InstanceId ||
                    row.Fact.Identity.QuestId != binding.Identity.QuestId || row.Fact.Identity.TaskAttempt != binding.Identity.TaskAttempt ||
                    row.Fact.Identity.Epoch < binding.Identity.Epoch) return null;
            bool bossClear = encounter.Area is 30 or 31;
            var before = current;
            if (bossClear && current.FiendwarBoss is not { Joined: true, RemainingHp: 0 })
            {
                // 39的权威死亡可能先于1554到达。仅输出窗口中的死亡保存本侧零血量；
                // 提前击杀只退回战场供重试，绝不能跳过四元素或另一世界而生成整团通关。
                var side = BossSide(encounter.Area);
                if (current.FiendwarBoss is { Joined: false } boss &&
                    boss.Side(side) is { Stage: FiendwarWorldStage.DamageWindow, WindowEnd: { } end } progress &&
                    lukeClock.GetUtcNow() <= end + FiendwarBossReportGrace)
                {
                    progress = progress with { Stage = FiendwarWorldStage.Saved, Damage = boss.RemainingHp };
                    boss = side == FiendwarWorld.Black ? boss with { Black = progress } : boss with { White = progress };
                    boss = boss.Join() ?? boss;
                    current = FiendwarBossSnapshot(current, boss, lukeClock.GetUtcNow());
                }
                if (current.FiendwarBoss is not { Joined: true, RemainingHp: 0 })
                {
                    CommitBattleCandidate(before, current with
                    {
                        FiendwarExpiredRuns = current.FiendwarExpiredRuns.Add(run.Id),
                        FiendwarBridgeReturns = current.FiendwarBoss?.Side(side).Stage == FiendwarWorldStage.Saved
                            ? current.FiendwarBridgeReturns.SetItem(run.Id, encounter.Area == 30 ? 31 : 30)
                            : current.FiendwarBridgeReturns,
                        FiendwarDungeonTimers = current.FiendwarDungeonTimers.Remove(encounter.Area)
                    });
                    return new(before, current);
                }
            }
            var field = current.Fiendwar!;
            if (bossClear)
            {
                field = fiendwarEngine!.CompleteArea(field, 30, lukeClock.GetUtcNow()).State;
                field = fiendwarEngine.CompleteArea(field, 31, lukeClock.GetUtcNow()).State;
            }
            else field = fiendwarEngine!.CompleteArea(field, encounter.Area, lukeClock.GetUtcNow()).State;
            RecordGuildClearParty(facts.Select(row => row.Actor.Character));
            foreach (var row in facts)
            {
                run.ClearFacts.Add(row.Actor, row.Fact);
                var key = (0, row.Actor.Character);
                lukeParticipation[key] = checked(lukeParticipation.GetValueOrDefault(key) + 1);
            }
            run.FreezeClearedMembers();
            var candidate = ProjectFiendwar(current with { FiendwarDungeonTimers = current.FiendwarDungeonTimers.Remove(encounter.Area) }, field);
            if (bossClear) candidate = candidate with { FiendwarExpiredRuns = candidate.FiendwarExpiredRuns.Add(run.Id) };
            CommitBattleCandidate(before, candidate);
            return new(before, current);
        }
    }

    bool TickFiendwarLocked()
    {
        if (current.Fiendwar is not { } field || TerminalRequested(current)) return false;
        var before = current;
        ExpireFiendwarBossWindowsLocked(lukeClock.GetUtcNow());
        field = current.Fiendwar!;
        var next = fiendwarEngine!.Tick(field, lukeClock.GetUtcNow()).State;
        var expired = current.FiendwarExpiredRuns.Union(fiendwarEncounters.Where(row => row.Value.Deadline <= lukeClock.GetUtcNow())
            .Select(row => row.Key));
        if (!ReferenceEquals(field, next) || !expired.SetEquals(before.FiendwarExpiredRuns))
            CommitBattleCandidate(current, ProjectFiendwar(current with { FiendwarExpiredRuns = expired,
                FiendwarDungeonTimers = current.FiendwarDungeonTimers.RemoveRange(fiendwarEncounters
                    .Where(row => expired.Contains(row.Key)).Select(row => row.Value.Area)) }, next));
        return !ReferenceEquals(before, current);
    }

    TimeSpan? FiendwarClockRemainingLocked()
    {
        if (current.Fiendwar is not { Failed: false, Cleared: false } field) return null;
        var end = field.Timers.Values.Select(row => row.End).Append(field.Deadline)
            .Concat(field.Merchant is { } merchant ? [merchant.End] : [])
            .Concat(current.FiendwarBoss is { Joined: false } boss ? new[] { boss.Black, boss.White }
                .Where(side => side.Stage == FiendwarWorldStage.DamageWindow).Select(side => side.WindowEnd!.Value + FiendwarBossReportGrace) : [])
            .Concat(fiendwarEncounters.Where(row => !current.FiendwarExpiredRuns.Contains(row.Key)).Select(row => row.Value.Deadline)).Min();
        var remaining = end - lukeClock.GetUtcNow();
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
