using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal enum SinglePlayerDungeonPhase { Loading, Dungeon, Settlement, Returned }
internal enum DungeonRunOutcome { Accepted, Duplicate, Rejected, Unsupported }
internal sealed record DungeonRunIdentity(ushort CharacterId, uint QuestId, long TaskAttempt, Guid InstanceId, long Epoch);
internal sealed record DungeonRouteConnection(DungeonPoint First, DungeonPoint Second);
internal sealed record DungeonRunSnapshot(DungeonRunIdentity Identity, SinglePlayerDungeonPhase Phase, DungeonMap CurrentMap,
    IReadOnlyList<ushort> LivingMonsters);
internal sealed record DungeonClearFact(DungeonRunIdentity Identity, uint DungeonId, IReadOnlyList<Guid> ConfirmedDeathEvents);
internal sealed record DungeonRunResult(DungeonRunOutcome Outcome, DungeonRunSnapshot Snapshot, DungeonClearFact? ClearFact = null);
internal sealed record DungeonDeathPreparation(DungeonRunOutcome Outcome, PreparedDungeonDeath? Pending = null);

// 只代表已核验的待提交候选。数据库结果确定之前，死亡集和清图状态保持原样。
internal sealed class PreparedDungeonDeath(ConfirmedDungeonMonsterDeath death, DungeonPlannedMonster monster, DungeonRunResult result)
{
    public ConfirmedDungeonMonsterDeath Death { get; } = death;
    public DungeonPlannedMonster Monster { get; } = monster;
    public DungeonRunResult Result { get; } = result;
}

// 正式战斗组件持有的能力对象。协议 handler 不创建权威，也不把客户端死亡字段变成此事件。
// 只有实际战斗解析已确认死亡之后，派生组件才调用签发方法；本类没有 bool died 成功入口。
internal abstract class DungeonCombatAuthority
{
    protected ConfirmedDungeonMonsterDeath ConfirmDeath(DungeonRunIdentity identity, Guid eventId, ushort monsterKey, long combatSequence)
        => ConfirmedDungeonMonsterDeath.Issue(this, identity, eventId, monsterKey, combatSequence);
}

internal sealed class ConfirmedDungeonMonsterDeath
{
    private ConfirmedDungeonMonsterDeath(DungeonCombatAuthority authority, DungeonRunIdentity identity, Guid eventId, ushort monsterKey, long sequence)
        => (Authority, Identity, EventId, MonsterKey, CombatSequence) = (authority, identity, eventId, monsterKey, sequence);
    internal DungeonCombatAuthority Authority { get; }
    public DungeonRunIdentity Identity { get; }
    public Guid EventId { get; }
    public ushort MonsterKey { get; }
    public long CombatSequence { get; }
    internal static ConfirmedDungeonMonsterDeath Issue(DungeonCombatAuthority authority, DungeonRunIdentity identity, Guid eventId,
        ushort monsterKey, long sequence) => new(authority, identity, eventId, monsterKey, sequence);
}

// 显式连接必须由匹配资源和门/墙规则的资格检查提供；相邻坐标本身不是连接证据。
internal sealed record DungeonFixedRoute(IReadOnlyList<DungeonMonsterPlan> Rooms, IReadOnlyList<DungeonRouteConnection> Connections);

internal sealed class SinglePlayerDungeonRun
{
    private sealed class Room(DungeonMonsterPlan plan)
    {
        public DungeonMonsterPlan Plan { get; } = plan;
        public HashSet<ushort> Dead { get; } = [];
        public bool Cleared => Dead.Count == Plan.Monsters.Count;
    }
    private sealed record ActionFact(DungeonRunIdentity Identity, string Kind, DungeonPoint? Target);
    private readonly DungeonEntryPlan entry;
    private readonly DungeonCombatAuthority authority;
    private readonly Dictionary<DungeonPoint, Room> rooms;
    private readonly HashSet<(DungeonPoint, DungeonPoint)> connections;
    private readonly Dictionary<Guid, ActionFact> acceptedActions = [];
    private readonly Dictionary<Guid, ConfirmedDungeonMonsterDeath> deaths = [];
    private readonly HashSet<PreparedDungeonDeath> committedPreparations = [];
    private readonly HashSet<long> combatSequences = [];
    private PreparedDungeonDeath? pendingDeath;
    private Room current;
    private DungeonRunIdentity identity;
    public long StartCharacterRevision => entry.CharacterRevision;
    internal uint DungeonId => entry.DungeonId;
    internal bool UsesCombatAuthority(DungeonCombatAuthority candidate) => ReferenceEquals(authority, candidate);
    internal DungeonPlannedMonster? CurrentLivingMonster(ushort key) =>
        Phase == SinglePlayerDungeonPhase.Dungeon && !current.Dead.Contains(key)
            ? current.Plan.Monsters.SingleOrDefault(monster => monster.ObjectKey == key) : null;
    public SinglePlayerDungeonPhase Phase { get; private set; } = SinglePlayerDungeonPhase.Loading;
    public DungeonRunSnapshot Snapshot => new(identity, Phase, current.Plan.Room.Map,
        Array.AsReadOnly(current.Plan.Monsters.Where(monster => !current.Dead.Contains(monster.ObjectKey)).Select(monster => monster.ObjectKey).ToArray()));

    private SinglePlayerDungeonRun(DungeonEntryPlan entry, long attempt, long epoch, DungeonFixedRoute route, DungeonCombatAuthority authority)
    {
        this.entry = entry;
        this.authority = authority;
        rooms = route.Rooms.ToDictionary(plan => plan.Room.Map.Position, plan => new Room(plan with { Monsters = Array.AsReadOnly(plan.Monsters.ToArray()) }));
        connections = route.Connections.SelectMany(connection => new[] { (connection.First, connection.Second), (connection.Second, connection.First) }).ToHashSet();
        current = rooms[entry.StartMap.Position];
        identity = new(entry.CharacterId, entry.QuestId, attempt, Guid.NewGuid(), epoch);
    }

    public static bool TryCreate(DungeonEntryPlan entry, long taskAttempt, long initialEpoch, DungeonFixedRoute? approvedRoute,
        DungeonCombatAuthority? combatAuthority, out SinglePlayerDungeonRun? run, out string reason)
    {
        run = null;
        reason = "unsupported: 需要正式战斗权威及已确认门墙连接";
        if (approvedRoute is null || combatAuthority is null)
            return false;
        if (entry.CharacterId == 0 || entry.CharacterRevision < 0 || entry.QuestId == 0 || taskAttempt <= 0 || initialEpoch <= 0 || initialEpoch >= long.MaxValue - 2)
        {
            reason = "invalid: 实例身份或任务尝试无效";
            return false;
        }
        if (entry.Maze.Width <= 0 || entry.Maze.Height <= 0 || entry.BossMap.Kind != "boss" ||
            entry.Maze.MapSpecification is not { Count: > 0 } maps ||
            maps.Any(map => map.Position.X < 0 || map.Position.Y < 0 || map.Position.X >= entry.Maze.Width || map.Position.Y >= entry.Maze.Height ||
                map.Kind is not ("map" or "boss")) || approvedRoute.Rooms.Count == 0 ||
            approvedRoute.Rooms.Count != maps.Count || approvedRoute.Rooms.Select(plan => plan.Room.Map.Position).Distinct().Count() != maps.Count ||
            approvedRoute.Rooms.Any(plan => !maps.Contains(plan.Room.Map)) || !maps.Contains(entry.StartMap) || !maps.Contains(entry.BossMap))
            return false;
        var keys = new HashSet<ushort>();
        foreach (DungeonMonsterPlan plan in approvedRoute.Rooms)
        {
            // 不把未支持生成规则或缺失资源行替换成空房间。
            DungeonMonsterPlan resourcePlan;
            try
            {
                resourcePlan = DungeonMonsterPlan.FromRoom(plan.Room, entry.CharacterId, 1);
            }
            catch (Exception error) when (error is NotSupportedException or InvalidDataException)
            {
                reason = "unsupported: 房间怪物生成规则未闭合";
                return false;
            }
            if (plan.Monsters.Count != plan.Room.Monsters.Count || plan.Monsters.Any(monster => monster.SourceOrdinal < 0 ||
                monster.SourceOrdinal >= plan.Room.Monsters.Count || monster.Source != plan.Room.Monsters[monster.SourceOrdinal] ||
                monster.MonsterId != monster.Source.MonsterId ||
                monster.CandidateLevel != resourcePlan.Monsters[monster.SourceOrdinal].CandidateLevel ||
                monster.SpawnType != resourcePlan.Monsters[monster.SourceOrdinal].SpawnType ||
                monster.ObjectKey == entry.CharacterId || !keys.Add(monster.ObjectKey)) ||
                plan.Monsters.Select(monster => monster.SourceOrdinal).Distinct().Count() != plan.Monsters.Count)
            {
                reason = "invalid: 房间怪物身份不完整或重复";
                return false;
            }
        }
        var points = maps.Select(map => map.Position).ToHashSet();
        var edges = new HashSet<(DungeonPoint, DungeonPoint)>();
        foreach (DungeonRouteConnection connection in approvedRoute.Connections)
        {
            if (!points.Contains(connection.First) || !points.Contains(connection.Second) ||
                Math.Abs((long)connection.First.X - connection.Second.X) + Math.Abs((long)connection.First.Y - connection.Second.Y) != 1 ||
                edges.Contains((connection.First, connection.Second)))
            {
                reason = "invalid: 固定路线连接越格、重复或缺房间";
                return false;
            }
            edges.Add((connection.First, connection.Second));
            edges.Add((connection.Second, connection.First));
        }
        var reachable = new HashSet<DungeonPoint> { entry.StartMap.Position };
        var pending = new Queue<DungeonPoint>();
        pending.Enqueue(entry.StartMap.Position);
        while (pending.TryDequeue(out DungeonPoint point))
        {
            foreach (var edge in edges.Where(edge => edge.Item1 == point))
            {
                if (reachable.Add(edge.Item2))
                    pending.Enqueue(edge.Item2);
            }
        }
        if (reachable.Count != points.Count || keys.Count == 0)
            return false;
        run = new(entry, taskAttempt, initialEpoch, approvedRoute, combatAuthority);
        reason = "domain-run-created";
        return true;
    }

    public DungeonRunResult CompleteLoading(DungeonRunIdentity expected, Guid actionId)
    {
        if (Replay(expected, actionId, "loaded", null) is { } replay)
            return replay;
        if (pendingDeath is not null || !Matches(expected) || Phase != SinglePlayerDungeonPhase.Loading || actionId == Guid.Empty || !CanAdvance())
            return Result(DungeonRunOutcome.Rejected);
        acceptedActions.Add(actionId, new(expected, "loaded", null));
        Phase = SinglePlayerDungeonPhase.Dungeon;
        Advance();
        return SettleIfCleared();
    }

    public DungeonRunResult Move(DungeonRunIdentity expected, Guid actionId, DungeonPoint target)
    {
        if (Replay(expected, actionId, "move", target) is { } replay)
            return replay;
        if (pendingDeath is not null)
            return Result(DungeonRunOutcome.Rejected);
        if (!Matches(expected) || Phase != SinglePlayerDungeonPhase.Dungeon || actionId == Guid.Empty || !CanAdvance() ||
            !current.Cleared || !connections.Contains((current.Plan.Room.Map.Position, target)) || !rooms.TryGetValue(target, out Room? next))
            return Result(DungeonRunOutcome.Rejected);
        acceptedActions.Add(actionId, new(expected, "move", target));
        current = next;
        Phase = SinglePlayerDungeonPhase.Loading;
        Advance();
        return Result(DungeonRunOutcome.Accepted);
    }

    public DungeonRunResult Return(DungeonRunIdentity expected, Guid actionId)
    {
        if (Replay(expected, actionId, "return", null) is { } replay)
            return replay;
        if (pendingDeath is not null || !Matches(expected) || Phase == SinglePlayerDungeonPhase.Returned || actionId == Guid.Empty || identity.Epoch == long.MaxValue)
            return Result(DungeonRunOutcome.Rejected);
        acceptedActions.Add(actionId, new(expected, "return", null));
        Phase = SinglePlayerDungeonPhase.Returned;
        Advance();
        return Result(DungeonRunOutcome.Accepted);
    }

    public DungeonRunResult AcceptConfirmedDeath(ConfirmedDungeonMonsterDeath confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        // 旧的纯内存入口不能抢先确认正在等待数据库结果的候选，既有死亡重放仍可识别。
        bool awaitingCommit = pendingDeath is not null;
        DungeonDeathPreparation preparation = PrepareConfirmedDeath(confirmed);
        if (preparation.Outcome != DungeonRunOutcome.Accepted)
            return Result(preparation.Outcome);
        return awaitingCommit ? Result(DungeonRunOutcome.Rejected) : ConfirmCommittedDeath(preparation.Pending!);
    }

    // 在原实例owner内准备；调用方随后同事务保存事件身份与奖励，提交后才确认内存。
    public DungeonDeathPreparation PrepareConfirmedDeath(ConfirmedDungeonMonsterDeath confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        if (!ReferenceEquals(confirmed.Authority, authority))
            return new(DungeonRunOutcome.Rejected);
        if (deaths.TryGetValue(confirmed.EventId, out ConfirmedDungeonMonsterDeath? old))
            return new(SameDeath(old, confirmed)
                ? DungeonRunOutcome.Duplicate : DungeonRunOutcome.Rejected);
        if (pendingDeath is not null)
            return SameDeath(pendingDeath.Death, confirmed)
                ? new(DungeonRunOutcome.Accepted, pendingDeath) : new(DungeonRunOutcome.Rejected);
        if (!Matches(confirmed.Identity) || Phase != SinglePlayerDungeonPhase.Dungeon || confirmed.EventId == Guid.Empty ||
            !CanAdvance() || confirmed.CombatSequence <= 0 || combatSequences.Contains(confirmed.CombatSequence) ||
            !current.Plan.Monsters.Any(monster => monster.ObjectKey == confirmed.MonsterKey) || current.Dead.Contains(confirmed.MonsterKey))
            return new(DungeonRunOutcome.Rejected);
        bool settles = current.Plan.Room.Map == entry.BossMap && rooms.Values.All(room =>
            room == current ? room.Dead.Count + 1 == room.Plan.Monsters.Count : room.Cleared);
        DungeonRunIdentity nextIdentity = settles ? identity with { Epoch = checked(identity.Epoch + 1) } : identity;
        var nextSnapshot = new DungeonRunSnapshot(nextIdentity,
            settles ? SinglePlayerDungeonPhase.Settlement : Phase, current.Plan.Room.Map,
            Array.AsReadOnly(current.Plan.Monsters.Where(monster => !current.Dead.Contains(monster.ObjectKey) &&
                monster.ObjectKey != confirmed.MonsterKey).Select(monster => monster.ObjectKey).ToArray()));
        DungeonClearFact? clear = settles
            ? new(nextIdentity, entry.DungeonId, Array.AsReadOnly(deaths.Keys.Append(confirmed.EventId).ToArray())) : null;
        pendingDeath = new(confirmed, current.Plan.Monsters.Single(monster => monster.ObjectKey == confirmed.MonsterKey),
            new(DungeonRunOutcome.Accepted, nextSnapshot, clear));
        return new(DungeonRunOutcome.Accepted, pendingDeath);
    }

    // 只接受本实例当前准备对象，不能用另一实例或重新构造的候选确认提交。
    public bool IsPendingDeath(PreparedDungeonDeath prepared) => prepared is not null && ReferenceEquals(pendingDeath, prepared);
    internal bool IsConfirmedDeath(PreparedDungeonDeath prepared) => prepared is not null && committedPreparations.Contains(prepared) &&
        deaths.TryGetValue(prepared.Death.EventId, out var accepted) && ReferenceEquals(accepted, prepared.Death);

    public DungeonRunResult ConfirmCommittedDeath(PreparedDungeonDeath prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(pendingDeath, prepared))
        {
            ConfirmedDungeonMonsterDeath death = prepared.Death;
            return deaths.TryGetValue(death.EventId, out var accepted) && ReferenceEquals(accepted, death)
                ? Result(DungeonRunOutcome.Duplicate) : Result(DungeonRunOutcome.Rejected);
        }
        ConfirmedDungeonMonsterDeath confirmed = prepared.Death;
        current.Dead.Add(confirmed.MonsterKey);
        combatSequences.Add(confirmed.CombatSequence);
        deaths.Add(confirmed.EventId, confirmed);
        committedPreparations.Add(prepared);
        identity = prepared.Result.Snapshot.Identity;
        Phase = prepared.Result.Snapshot.Phase;
        pendingDeath = null;
        return prepared.Result;
    }

    // 仅在存储明确未提交或已回滚时撤销准备；结果不明时保留候选并查询持久receipt。
    public bool CancelUncommittedDeath(PreparedDungeonDeath prepared)
    {
        if (prepared is null || !ReferenceEquals(pendingDeath, prepared))
            return false;
        pendingDeath = null;
        return true;
    }

    private static bool SameDeath(ConfirmedDungeonMonsterDeath first, ConfirmedDungeonMonsterDeath second) =>
        ReferenceEquals(first.Authority, second.Authority) && first.EventId == second.EventId && first.Identity == second.Identity &&
        first.MonsterKey == second.MonsterKey && first.CombatSequence == second.CombatSequence;

    private DungeonRunResult SettleIfCleared()
    {
        if (current.Plan.Room.Map == entry.BossMap && rooms.Values.All(room => room.Cleared) && deaths.Count > 0)
        {
            Phase = SinglePlayerDungeonPhase.Settlement;
            Advance();
            return new(DungeonRunOutcome.Accepted, Snapshot, new(identity, entry.DungeonId, Array.AsReadOnly(deaths.Keys.ToArray())));
        }
        return Result(DungeonRunOutcome.Accepted);
    }
    private bool Matches(DungeonRunIdentity expected) => identity == expected && Phase != SinglePlayerDungeonPhase.Returned;
    private bool CanAdvance() => identity.Epoch < long.MaxValue - 2;
    private void Advance() => identity = identity with { Epoch = checked(identity.Epoch + 1) };
    private DungeonRunResult Result(DungeonRunOutcome outcome) => new(outcome, Snapshot);
    private DungeonRunResult? Replay(DungeonRunIdentity expected, Guid actionId, string kind, DungeonPoint? target)
        => acceptedActions.TryGetValue(actionId, out ActionFact? fact)
            ? Result(fact == new ActionFact(expected, kind, target) ? DungeonRunOutcome.Duplicate : DungeonRunOutcome.Rejected)
            : null;
}
