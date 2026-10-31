using System.Collections.Immutable;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Raids;

// 参与者在遭遇创建时冻结；离线、换边与重连都不重新计算这一生命池的倍率。
internal sealed record FiendwarCombatScale
{
    public ImmutableArray<ushort> Participants { get; }
    public int HpPercent { get; }
    public int AttackPercent { get; }
    public FiendwarCombatScale(IEnumerable<ushort> participants, bool sharedBoss = false, bool original = false)
    {
        Participants = participants.Order().ToImmutableArray();
        if (Participants.Length < 1 || Participants.Length > (sharedBoss ? 8 : 4) ||
            Participants.Distinct().Count() != Participants.Length)
            throw new ArgumentException("漩涡遭遇参与者无效", nameof(participants));
        int count = Math.Min(4, Participants.Length);
        HpPercent = original ? 100 : count * 25;
        AttackPercent = original ? 100 : 40 + count * 15;
    }
    public ulong ScaleHp(ulong original) => original == 0 ? 0 : Math.Max(1UL, (ulong)((UInt128)original * (uint)HpPercent / 100));
    public ulong ScaleAttack(ulong original) => original == 0 ? 0 : Math.Max(1UL, (ulong)((UInt128)original * (uint)AttackPercent / 100));
}

internal sealed record FiendwarEncounter(Guid Instance, int Area, int Dungeon, int MazeOrdinal,
    FiendwarCombatScale Scale, DateTimeOffset Deadline, ImmutableDictionary<ushort, DungeonRunIdentity> Runs,
    ImmutableHashSet<int> TerminalMaps, ImmutableHashSet<ushort> Completed)
{
    internal static FiendwarEncounter Create(int area, int dungeon, int mazeOrdinal, DateTimeOffset deadline,
        IReadOnlyList<DungeonRunIdentity> runs, IReadOnlySet<int> terminalMaps, FiendwarCombatScale? sharedScale = null)
    {
        if (area < 0 || dungeon <= 0 || mazeOrdinal < 0 || runs.Count is < 1 or > 4 ||
            runs.Select(r => r.CharacterId).Distinct().Count() != runs.Count ||
            runs.Select(r => r.InstanceId).Distinct().Count() != 1 || runs[0].InstanceId == Guid.Empty ||
            runs.Any(r => r.Epoch < 0) || terminalMaps.Count == 0 || terminalMaps.Any(id => id < 0))
            throw new ArgumentException("漩涡正式遭遇身份无效", nameof(runs));
        var scale = sharedScale ?? new FiendwarCombatScale(runs.Select(r => r.CharacterId));
        if (runs.Any(r => !scale.Participants.Contains(r.CharacterId)))
            throw new ArgumentException("正式副本成员不属于冻结遭遇", nameof(runs));
        return new(runs[0].InstanceId, area, dungeon, mazeOrdinal, scale, deadline,
            runs.ToImmutableDictionary(r => r.CharacterId), terminalMaps.ToImmutableHashSet(), ImmutableHashSet<ushort>.Empty);
    }
    internal bool Accepts(DungeonClearFact fact, DateTimeOffset now) => now < Deadline &&
        fact.DungeonId == Dungeon && Runs.TryGetValue(fact.Identity.CharacterId, out var original) &&
        original.CharacterId == fact.Identity.CharacterId && original.InstanceId == fact.Identity.InstanceId &&
        original.QuestId == fact.Identity.QuestId && original.TaskAttempt == fact.Identity.TaskAttempt && fact.Identity.Epoch >= original.Epoch &&
        fact.ClearMap is { } map && TerminalMaps.Contains(map.Id) && fact.ConfirmedDeathEvents.Count > 0 &&
        fact.ConfirmedDeathEvents.All(id => id != Guid.Empty);
    internal FiendwarEncounter? Complete(DungeonClearFact fact, DateTimeOffset now) =>
        Accepts(fact, now) ? this with { Completed = Completed.Add(fact.Identity.CharacterId) } : null;
    internal bool AllCompleted => Completed.Count == Runs.Count;
}

internal enum FiendwarWorld : byte { Black, White }
internal enum FiendwarElement : byte { Fire, Ice, Wind, Attribute }
internal enum FiendwarWorldStage { Mechanism, DamageWindow, Saved }
internal sealed record FiendwarWorldProgress(FiendwarWorld World, FiendwarElement? Element,
    FiendwarWorldStage Stage, DateTimeOffset? WindowEnd = null, ulong Damage = 0);

// 双世界共享一个生命池及倍率，各侧窗口独立计时；结果保存不要求另一侧同时有人。
internal sealed record FiendwarBossRound(Guid Id, int Ordinal, FiendwarCombatScale Scale, ulong MaxHp, ulong RemainingHp,
    FiendwarWorldProgress Black, FiendwarWorldProgress White, bool Joined = false)
{
    public ImmutableHashSet<FiendwarElement> RemainingElements { get; init; } = Enum.GetValues<FiendwarElement>().ToImmutableHashSet();
    internal static FiendwarBossRound Create(int ordinal, FiendwarCombatScale scale, ulong maxHp, ulong remainingHp,
        IReadOnlyList<FiendwarElement> order)
    {
        if (ordinal < 0 || maxHp == 0 || remainingHp > maxHp || order.Count != 4 || order.Distinct().Count() != 4 ||
            order.Any(element => !Enum.IsDefined(element))) throw new ArgumentException("罗什轮次定义无效", nameof(order));
        return new(Guid.NewGuid(), ordinal, scale, maxHp, remainingHp,
            new(FiendwarWorld.Black, order[0], FiendwarWorldStage.Mechanism),
            new(FiendwarWorld.White, order[1], FiendwarWorldStage.Mechanism));
    }
    internal FiendwarWorldProgress Side(FiendwarWorld side) => side == FiendwarWorld.Black ? Black : White;
    internal FiendwarBossRound? CompleteMechanism(FiendwarWorld side, FiendwarElement element, DateTimeOffset now,
        TimeSpan originalWindow, int nextChoice)
    {
        var progress = Side(side);
        if (Joined || progress.Stage != FiendwarWorldStage.Mechanism || progress.Element != element ||
            !RemainingElements.Contains(element) || originalWindow <= TimeSpan.Zero) return null;
        var remaining = RemainingElements.Remove(element);
        var other = Side(side == FiendwarWorld.Black ? FiendwarWorld.White : FiendwarWorld.Black);
        var available = remaining.Where(e => other.Stage != FiendwarWorldStage.Mechanism || e != other.Element).Order().ToArray();
        if (nextChoice < 0 || available.Length > 0 && nextChoice >= available.Length) return null;
        progress = progress with { Element = available.Length == 0 ? null : available[nextChoice] };
        var result = side == FiendwarWorld.Black ? this with { Black = progress, RemainingElements = remaining } :
            this with { White = progress, RemainingElements = remaining };
        // 本侧没有可领取的分身时，即可开始自己的原长度输出窗口；另一侧继续完成共享池剩余分身。
        return available.Length == 0 ? result.OpenWindow(side, now, originalWindow) : result;
    }
    internal FiendwarBossRound OpenWindow(FiendwarWorld side, DateTimeOffset now, TimeSpan originalWindow)
    {
        if (Joined || originalWindow <= TimeSpan.Zero || Side(side) is not { Stage: FiendwarWorldStage.Mechanism, Element: null })
            throw new InvalidOperationException("罗什本侧输出窗口不可开启");
        var progress = Side(side) with { Stage = FiendwarWorldStage.DamageWindow, WindowEnd = now + originalWindow };
        return side == FiendwarWorld.Black ? this with { Black = progress } : this with { White = progress };
    }
    internal FiendwarBossRound? AddDamage(FiendwarWorld side, ulong damage, DateTimeOffset now)
    {
        var progress = Side(side);
        if (Joined || progress.Stage != FiendwarWorldStage.DamageWindow || progress.WindowEnd is not { } end || now >= end) return null;
        ulong available = RemainingHp - Math.Min(RemainingHp, progress.Damage);
        progress = progress with { Damage = progress.Damage + Math.Min(available, damage) };
        return side == FiendwarWorld.Black ? this with { Black = progress } : this with { White = progress };
    }
    internal FiendwarBossRound? Save(FiendwarWorld side, DateTimeOffset now)
    {
        var progress = Side(side);
        if (Joined || progress.Stage != FiendwarWorldStage.DamageWindow || progress.WindowEnd is not { } end || now < end) return null;
        progress = progress with { Stage = FiendwarWorldStage.Saved };
        return side == FiendwarWorld.Black ? this with { Black = progress } : this with { White = progress };
    }
    internal FiendwarBossRound? Join()
    {
        if (Joined) return this;
        if (Black.Stage != FiendwarWorldStage.Saved || White.Stage != FiendwarWorldStage.Saved) return null;
        if (!RemainingElements.IsEmpty) return null;
        // 原双侧剩余血量均摊并向下取整，不能把两边伤害直接相加而将难度减半。
        ulong black = RemainingHp - Math.Min(RemainingHp, Black.Damage), white = RemainingHp - Math.Min(RemainingHp, White.Damage);
        return this with { RemainingHp = (ulong)(((UInt128)black + white) / 2), Joined = true };
    }
}
