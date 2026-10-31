using System.Collections.Immutable;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    FiendwarCombatScale? fiendwarBossScale;
    readonly Dictionary<Guid, (uint Kind, uint Value)> fiendwarBossReports = [];
    static readonly TimeSpan FiendwarBossWindow = TimeSpan.FromSeconds(15);
    static readonly TimeSpan FiendwarBossReportGrace = TimeSpan.FromSeconds(5);

    internal FiendwarCombatScale FiendwarEntryScale(int dungeon, IEnumerable<ushort> actors)
    {
        lock (gate)
            return FiendwarAreaForDungeon(dungeon) is 30 or 31
                ? fiendwarBossScale ??= new(members.Keys.Select(actor => actor.Character), sharedBoss: true)
                : new(actors, original: fiendwarDefinition!.Guide);
    }

    static FiendwarWorld BossSide(int area) => area == 30 ? FiendwarWorld.Black : FiendwarWorld.White;
    static string BossStateKey(FiendwarWorld side) => side == FiendwarWorld.Black ? "[top boss state]" : "[bot boss state]";
    static string BossHpKey(FiendwarWorld side) => side == FiendwarWorld.Black ? "[top boss hp]" : "[bot boss hp]";
    static string BossViewKey(FiendwarWorld side) => side == FiendwarWorld.Black ? "[top boss view hp]" : "[bot boss view hp]";
    // 00D716C0：5水晶、6火、7水、8风；对应monster.lst 107013165–168。
    static int BossElementState(FiendwarElement element) => element switch
    { FiendwarElement.Attribute => 5, FiendwarElement.Fire => 6, FiendwarElement.Ice => 7, FiendwarElement.Wind => 8, _ => throw new ArgumentOutOfRangeException(nameof(element)) };

    bool FiendwarBossEntryOpen(int area)
    {
        if (area is not (30 or 31) || current.FiendwarBoss is not { } boss) return true;
        return boss.Joined ? boss.RemainingHp > 0 && !fiendwarEncounters.Values.Any(row => row.Area is 30 or 31)
            : boss.Side(BossSide(area)).Stage != FiendwarWorldStage.Saved;
    }

    FiendwarBossRound NewFiendwarBossRound(int ordinal, ulong remaining)
    {
        var order = Enum.GetValues<FiendwarElement>();
        Random.Shared.Shuffle(order);
        return FiendwarBossRound.Create(ordinal, fiendwarBossScale!, 100, remaining, order);
    }

    AntonRaidBattleView FiendwarBossSnapshot(AntonRaidBattleView before, FiendwarBossRound boss, DateTimeOffset now)
    {
        var values = new Dictionary<string, int> { ["[boss pattern state]"] = 1 };
        foreach (var side in Enum.GetValues<FiendwarWorld>())
        {
            var progress = boss.Side(side);
            values[BossStateKey(side)] = progress.Element is { } element ? BossElementState(element) : 9;
            values[BossHpKey(side)] = checked((int)(boss.Joined ? boss.RemainingHp : boss.RemainingHp - Math.Min(boss.RemainingHp, progress.Damage)));
            values[BossViewKey(side)] = values[BossHpKey(side)];
        }
        var field = fiendwarEngine!.BossSymbols(before.Fiendwar!, values, now);
        return ProjectFiendwar(before with { FiendwarBoss = boss }, field);
    }

    internal AntonRaidBattleChange? ObserveFiendwarRun(Guid instance, RaidActor[] actors, int map, bool loaded)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Fiendwar || TerminalRequested(current) || pendingTransition is not null ||
                !dungeonRuns.TryGetValue(instance, out var run) || !run.Installed ||
                !run.Active.SetEquals(actors) || actors.Any(actor => !actor.IsCurrent) ||
                !fiendwarEncounters.TryGetValue(instance, out var encounter)) return null;
            var before = current;
            var now = lukeClock.GetUtcNow();
            if (!run.EnterConsumed)
            {
                run.EnterConsumed = true;
                current = current with { FiendwarDungeonTimers = current.FiendwarDungeonTimers.SetItem(encounter.Area,
                    new(encounter.Started, encounter.Deadline)) };
                current = ProjectFiendwar(current, fiendwarEngine!.Apply(current.Fiendwar!,
                    new(GameWorld.Raids.FiendwarOp.EnterDungeon, checked((int)run.Dungeon)), now).State);
            }
            if (loaded)
            {
                current = ProjectFiendwar(current, fiendwarEngine!.Apply(current.Fiendwar!,
                    new(GameWorld.Raids.FiendwarOp.EnterMap, map), now).State);
                if (encounter.Area is 30 or 31 && map == (encounter.Area == 30 ? 292105963 : 292105960))
                {
                    var boss = current.FiendwarBoss;
                    if (boss is null || boss.Joined && boss.RemainingHp > 0)
                        boss = NewFiendwarBossRound((boss?.Ordinal ?? -1) + 1, boss?.RemainingHp ?? 100);
                    current = FiendwarBossSnapshot(current, boss!, now);
                }
            }
            if (ReferenceEquals(before, current)) return null;
            current = current with { Revision = before.Revision + 1 };
            ScheduleLukeClockLocked();
            return new(before, current);
        }
    }

    // 1554来自当前正式场景主机：两个u32为报告种类及当前属性/剩余HP百分比。
    internal AntonRaidBattleChange? ReportFiendwarBoss(RaidActor actor, DungeonRunIdentity identity, int map, uint kind, uint value)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Fiendwar || !actor.IsCurrent || !members.ContainsKey(actor) || TerminalRequested(current) ||
                pendingTransition is not null || current.FiendwarBoss is not { Joined: false } boss ||
                !dungeonRuns.TryGetValue(identity.InstanceId, out var run) || !run.Installed || !run.Active.Contains(actor) ||
                !entries.TryGetValue((run.Id, actor.Character), out var binding) || identity.InstanceId != binding.Identity.InstanceId ||
                identity.CharacterId != actor.Character || identity.Epoch < binding.Identity.Epoch ||
                !fiendwarEncounters.TryGetValue(run.Id, out var encounter) || encounter.Area is not (30 or 31) ||
                current.FiendwarExpiredRuns.Contains(run.Id) || lukeClock.GetUtcNow() >= encounter.Deadline ||
                map != (encounter.Area == 30 ? 292105963 : 292105960)) return null;
            var before = current;
            if (fiendwarBossReports.GetValueOrDefault(run.Id) == (kind, value) && fiendwarBossReports.ContainsKey(run.Id))
                return new(before, before);
            var now = lukeClock.GetUtcNow();
            var side = BossSide(encounter.Area);
            var progress = boss.Side(side);
            if (kind == 1)
            {
                if (progress.Element is not { } element || value != BossElementState(element)) return null;
                var available = boss.RemainingElements.Remove(element).Where(candidate =>
                    boss.Side(side == FiendwarWorld.Black ? FiendwarWorld.White : FiendwarWorld.Black) is not
                        { Stage: FiendwarWorldStage.Mechanism, Element: { } other } || candidate != other).ToArray();
                var changed = boss.CompleteMechanism(side, element, now, FiendwarBossWindow,
                    available.Length == 0 ? 0 : Random.Shared.Next(available.Length));
                if (changed is null) return null;
                boss = changed;
                foreach (var member in run.Active)
                    lukeParticipation[(0, member.Character)] = checked(lukeParticipation.GetValueOrDefault((0, member.Character)) + 1);
            }
            else if (kind is 0 or 2)
            {
                if (value > boss.RemainingHp || progress.Stage != FiendwarWorldStage.DamageWindow ||
                    progress.WindowEnd is not { } end || kind == 0 && now >= end ||
                    kind == 2 && (now < end || now > end + FiendwarBossReportGrace) ||
                    value > boss.RemainingHp - progress.Damage) return null;
                progress = progress with { Damage = boss.RemainingHp - value };
                boss = side == FiendwarWorld.Black ? boss with { Black = progress } : boss with { White = progress };
                if (kind == 2) boss = boss.Save(side, now)!;
            }
            else return null;
            fiendwarBossReports[run.Id] = (kind, value);
            InstallFiendwarBossProgress(before, boss, now);
            return new(before, current);
        }
    }

    void InstallFiendwarBossProgress(AntonRaidBattleView before, FiendwarBossRound boss, DateTimeOffset now)
    {
        boss = boss.Join() ?? boss;
        var candidate = FiendwarBossSnapshot(before, boss, now);
        foreach (var encounter in fiendwarEncounters)
            if (encounter.Value.Area is 30 or 31 && boss.Side(BossSide(encounter.Value.Area)).Stage == FiendwarWorldStage.Saved &&
                !(boss.Joined && boss.RemainingHp == 0))
                candidate = candidate with { FiendwarExpiredRuns = candidate.FiendwarExpiredRuns.Add(encounter.Key) };
        CommitBattleCandidate(before, candidate);
    }

    void ExpireFiendwarBossWindowsLocked(DateTimeOffset now)
    {
        if (current.FiendwarBoss is not { Joined: false } boss) return;
        bool changed = false;
        foreach (var side in Enum.GetValues<FiendwarWorld>())
            if (boss.Side(side) is { Stage: FiendwarWorldStage.DamageWindow, WindowEnd: { } end } && now >= end + FiendwarBossReportGrace)
            { boss = boss.Save(side, now)!; changed = true; }
        if (changed) InstallFiendwarBossProgress(current, boss, now);
    }
}
