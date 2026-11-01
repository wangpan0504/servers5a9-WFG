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
    readonly Dictionary<Guid, (int Area, FiendwarCombatScale Scale, DateTimeOffset Started, DateTimeOffset Deadline)> fiendwarEncounters = [];
    readonly Dictionary<int, (long Generation, long Enemy, long Monster, int Ordinal)> fiendwarMazes = [];
    readonly Dictionary<ushort, FiendwarMovement> fiendwarMovements = [];

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
        return before with { Fiendwar = field, Symbols = field.Symbols, Dungeons = dungeons,
            FiendwarDungeonTimers = field.Failed || field.Cleared ? before.FiendwarDungeonTimers.Clear() : before.FiendwarDungeonTimers,
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
            int maximum = Math.Min(4, target.MemberMax);
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
                run.Active.Count != facts.Length || facts.Select(row => row.Actor).Distinct().Count() != facts.Length ||
                !fiendwarEncounters.TryGetValue(run.Id, out var encounter) || lukeClock.GetUtcNow() >= encounter.Deadline) return null;
            foreach (var row in facts)
                if (!row.Actor.IsCurrent || !run.Active.Contains(row.Actor) || row.Fact.DungeonId != run.Dungeon ||
                    row.Fact.ClearMap is null || row.Fact.ConfirmedDeathEvents.Count == 0 ||
                    !entries.TryGetValue((run.Id, row.Actor.Character), out var binding) || !ReferenceEquals(binding.Run, run) ||
                    row.Fact.Identity.CharacterId != binding.Identity.CharacterId || row.Fact.Identity.InstanceId != binding.Identity.InstanceId ||
                    row.Fact.Identity.QuestId != binding.Identity.QuestId || row.Fact.Identity.TaskAttempt != binding.Identity.TaskAttempt ||
                    row.Fact.Identity.Epoch < binding.Identity.Epoch) return null;
            // 双侧四元素与窗口结果汇合后仍须当前本体的真实终图死亡；单侧clear不可代替机制。
            bool bossClear = encounter.Area is 30 or 31;
            if (bossClear && current.FiendwarBoss is not { Joined: true, RemainingHp: 0 }) return null;
            var before = current;
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
            var candidate = ProjectFiendwar(before with { FiendwarDungeonTimers = before.FiendwarDungeonTimers.Remove(encounter.Area) }, field);
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
