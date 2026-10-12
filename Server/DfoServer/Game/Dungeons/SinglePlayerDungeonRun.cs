using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal enum SinglePlayerDungeonPhase { Loading, Dungeon, Settlement, Returned }
internal enum DungeonRunOutcome { Accepted, Duplicate, Rejected, Unsupported }
internal enum DungeonMapOperation : byte { Current, AdvanceLayer, RestoreBase }
internal enum DungeonMapPayload : byte { Cached, Build, Refresh }
internal sealed record DungeonRunIdentity(ushort CharacterId, uint QuestId, long TaskAttempt, Guid InstanceId, long Epoch);
internal sealed record DungeonRouteConnection(DungeonPoint First, DungeonPoint Second);
internal sealed record DungeonRunSnapshot(DungeonRunIdentity Identity, SinglePlayerDungeonPhase Phase, DungeonMap CurrentMap,
    IReadOnlyList<ushort> LivingMonsters)
{
    public DungeonMapOperation MapOperation { get; init; }
    public DungeonMapPayload MapPayload { get; init; } = DungeonMapPayload.Build;
}
internal sealed record DungeonLoadingFact(DungeonRunIdentity Identity, Guid ActionId, uint DungeonId, DungeonMap Map,
    int MazeSourceOrdinal, bool IsInitialLoading, bool DidInitializeMap, uint ConfirmedInitializationCount,
    uint? MazeInitializationThreshold)
{
    public bool InitializationCounterSupported { get; init; }
}
internal sealed record DungeonClearFact(DungeonRunIdentity Identity, uint DungeonId, IReadOnlyList<Guid> ConfirmedDeathEvents)
{
    public DungeonMap? ClearMap { get; init; }
}
internal sealed record DungeonRunResult(DungeonRunOutcome Outcome, DungeonRunSnapshot Snapshot, DungeonClearFact? ClearFact = null)
{
    public IReadOnlyList<ushort> CulledMonsterKeys { get; init; } = [];
}
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
        public DungeonElevatorState? Elevator { get; } = DungeonElevatorState.Create(plan);
        public HashSet<ushort> Dead { get; } = [];
        public HashSet<ushort> Culled { get; } = [];
        public bool Cleared => Plan.Monsters.All(monster => !monster.IsBlocking || Dead.Contains(monster.ObjectKey) || Culled.Contains(monster.ObjectKey));
    }
    private sealed record ActionFact(DungeonRunIdentity Identity, string Kind, DungeonPoint? Target);
    private DungeonEntryPlan entry;
    private readonly DungeonCombatAuthority authority;
    private readonly Dictionary<DungeonPoint, Room> rooms;
    private readonly Dictionary<DungeonMap, Room> layeredRooms;
    private readonly Room[] storyRooms;
    private readonly Room[] allRooms;
    private readonly Dictionary<DungeonPoint, Room> conditionalRooms;
    private int storyStageIndex;
    private readonly HashSet<DungeonMap> enteredLayers = [];
    private readonly Dictionary<DungeonPoint, Room> layerSources = [];
    private DungeonPoint? pendingLayerBaseAck;
    private DungeonMapOperation mapOperation;
    private DungeonMapPayload mapPayload = DungeonMapPayload.Build;
    private readonly HashSet<int> initializedMapSlots = [];
    private readonly HashSet<(DungeonPoint, DungeonPoint)> connections;
    private readonly Dictionary<Guid, ActionFact> acceptedActions = [];
    private readonly Dictionary<Guid, ConfirmedDungeonMonsterDeath> deaths = [];
    private readonly HashSet<PreparedDungeonDeath> committedPreparations = [];
    private readonly HashSet<long> combatSequences = [];
    private PreparedDungeonDeath? pendingDeath;
    private PreparedDungeonSettlement? pendingSettlement;
    private PreparedDungeonSettlement? committedSettlement;
    private Room current;
    private DungeonRunIdentity identity;
    public bool BossEntranceOpen { get; private set; }
    public bool BossEntranceReady => entry.Maze.BossEntrance is { Targets.Count: > 0 } condition &&
        condition.Targets.All(target => allRooms.Sum(room => room.Plan.Monsters.Count(monster =>
            monster.MonsterId == target.TargetId && monster.AiCharacter is null && monster.CanReportDeath && !monster.IsSpecialChild &&
            room.Dead.Contains(monster.ObjectKey))) >= target.Count);
    internal bool ConfirmBossEntranceOpen()
    {
        if (!BossEntranceReady || Phase == SinglePlayerDungeonPhase.Returned) return false;
        BossEntranceOpen = true;
        return true;
    }
    public long StartCharacterRevision => entry.CharacterRevision;
    public uint ConfirmedInitializationCount { get; private set; }
    public uint? MazeInitializationThreshold { get; }
    public bool InitializationCounterSupported { get; }
    public DungeonLoadingFact? LastLoadingFact { get; private set; }
    internal DungeonEntryPlan Entry => entry;
    internal bool HasPendingDeath => pendingDeath is not null;
    internal uint DungeonId => entry.DungeonId;
    internal DungeonElevatorState? CurrentElevator => current.Elevator;
    internal bool UsesCombatAuthority(DungeonCombatAuthority candidate) => ReferenceEquals(authority, candidate);
    internal void RebasePreparedRevision(long revision)
    {
        if (Phase != SinglePlayerDungeonPhase.Loading || acceptedActions.Count != 0 || revision < entry.CharacterRevision)
            throw new InvalidOperationException("只能同步尚未安装的副本入场规划");
        entry = entry with { CharacterRevision = revision };
    }
    // 原生顺序入口可在同一父剧情实例中接取下一子任务，仅绑定此后发生的事件。
    internal void BindQuestAcceptance(DfoServer.Game.Characters.ActiveMainQuest active)
    {
        if (active.AcceptanceId == Guid.Empty || pendingDeath is not null || pendingSettlement is not null ||
            entry.QuestAcceptances.Any(row => row.QuestId == active.QuestId))
            throw new InvalidOperationException("子任务不能复用本实例已绑定的旧接取");
        entry = entry with { QuestAcceptances = entry.QuestAcceptances.Add(active) };
    }
    internal DungeonPlannedMonster? CurrentLivingMonster(ushort key) =>
        Phase is SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement &&
        !current.Dead.Contains(key) && !current.Culled.Contains(key)
            ? current.Plan.Monsters.SingleOrDefault(monster => monster.ObjectKey == key && (monster.CanReportDeath || monster.IsSpecialChild ||
                entry.IsTutorial && (monster.IsScriptedRetirement || monster.AiCharacter?.Faction is "[character]" or "[neutral]"))) : null;
    internal bool IsCulledMonster(ushort key) => Phase == SinglePlayerDungeonPhase.Settlement && ClearFact is not null && current.Culled.Contains(key);
    // 友方、非敌对对象退场只更新显示集合，不加入持久击杀或清图事实；未知key不改变实例。
    internal void RetireNonCombatObject(ushort key)
    {
        if (current.Plan.Monsters.Any(monster => monster.ObjectKey == key && !monster.CanReportDeath))
            current.Dead.Add(key);
    }
    public SinglePlayerDungeonPhase Phase { get; private set; } = SinglePlayerDungeonPhase.Loading;
    public DungeonClearFact? ClearFact { get; private set; }
    public bool HasPendingSettlement => pendingSettlement is not null;
    public DungeonRunSnapshot Snapshot => new(identity, Phase, current.Plan.Room.Map,
        Array.AsReadOnly(current.Plan.Monsters.Where(monster => !current.Dead.Contains(monster.ObjectKey) &&
            !current.Culled.Contains(monster.ObjectKey)).Select(monster => monster.ObjectKey).ToArray()))
        { MapOperation = mapOperation, MapPayload = mapPayload };

    // 父脚本子actor的收尾不进入普通击杀集合，不能借此触发清图、经验或任务事实。
    public DungeonRunOutcome RetireSpecialChild(ConfirmedDungeonMonsterDeath death)
    {
        if (!ReferenceEquals(death.Authority, authority) || death.Identity != identity || Phase != SinglePlayerDungeonPhase.Dungeon ||
            death.EventId == Guid.Empty || death.CombatSequence <= 0 || pendingDeath is not null || pendingSettlement is not null ||
            !current.Plan.Monsters.Any(monster => !monster.IsBlocking && monster.ObjectKey == death.MonsterKey &&
                (monster.IsSpecialChild || entry.IsTutorial && (monster.IsScriptedRetirement || monster.AiCharacter?.Faction is "[character]" or "[neutral]"))))
            return DungeonRunOutcome.Rejected;
        return current.Dead.Add(death.MonsterKey) ? DungeonRunOutcome.Accepted : DungeonRunOutcome.Duplicate;
    }

    private SinglePlayerDungeonRun(DungeonEntryPlan entry, long attempt, long epoch, DungeonFixedRoute route, DungeonCombatAuthority authority)
    {
        this.entry = entry;
        this.authority = authority;
        var baseRooms = route.Rooms.Where(plan => plan.Room.Map.Kind != "layered" && !plan.Room.Map.IsConditional).Select(plan => new Room(
            plan with { Monsters = Array.AsReadOnly(plan.Monsters.ToArray()) })).ToArray();
        rooms = baseRooms.GroupBy(room => room.Plan.Room.Map.Position).ToDictionary(group => group.Key,
            group => group.FirstOrDefault(room => room.Plan.Room.Map.StoryStageIndex < 0) ?? group.First());
        storyRooms = baseRooms.Where(room => room.Plan.Room.Map.StoryStageIndex >= 0).OrderBy(room => room.Plan.Room.Map.StoryStageIndex).ToArray();
        layeredRooms = route.Rooms.Where(plan => plan.Room.Map.Kind == "layered").ToDictionary(plan => plan.Room.Map,plan => new Room(plan));
        conditionalRooms = route.Rooms.Where(plan => plan.Room.Map.IsConditional).ToDictionary(plan => plan.Room.Map.Position,plan => new Room(plan));
        allRooms = [.. baseRooms, .. layeredRooms.Values, .. conditionalRooms.Values];
        connections = route.Connections.SelectMany(connection => new[] { (connection.First, connection.Second), (connection.Second, connection.First) }).ToHashSet();
        current = baseRooms.Single(room => room.Plan.Room.Map == entry.StartMap);
        storyStageIndex = entry.StartMap.StoryStageIndex;
        identity = new(entry.CharacterId, entry.QuestId, attempt, Guid.NewGuid(), epoch);
        var grid = entry.Maze.GreedGrid;
        InitializationCounterSupported = entry.InitializationCounterSupported && entry.Maze.LayeredMaps.Count == 0 && storyRooms.Length == 0 && grid.Supported;
        int positiveCells = grid.Cells?.Count(cell => cell.Index > 0) ?? 0;
        MazeInitializationThreshold = InitializationCounterSupported && positiveCells > 1 ? checked((uint)(positiveCells - 1)) : null;
    }

    public static bool TryCreate(DungeonEntryPlan entry, long taskAttempt, long initialEpoch, DungeonFixedRoute? approvedRoute,
        DungeonCombatAuthority? combatAuthority, out SinglePlayerDungeonRun? run, out string reason)
    {
        run = null;
        reason = "unsupported: 需要正式战斗权威及已确认门墙连接";
        if (approvedRoute is null || combatAuthority is null)
            return false;
        if (entry.Maze.StoryStages.Count > 0 && (entry.BossMap != entry.Maze.StoryStages[^1] ||
            entry.Maze.StoryStages.Where((map,index) => map.StoryStageIndex != index).Any())) return false;
        if (entry.CharacterId == 0 || entry.CharacterRevision < 0 || entry.QuestId >= 30000 ||
            entry.QuestId == 0 && (entry.ActiveQuestSlot != byte.MaxValue || entry.QuestAcceptanceId != Guid.Empty) ||
            taskAttempt <= 0 || initialEpoch <= 0 || initialEpoch >= long.MaxValue - 2)
        {
            reason = "invalid: 实例身份或任务尝试无效";
            return false;
        }
        if (entry.Maze.Width <= 0 || entry.Maze.Height <= 0 || entry.BaseBossMap.Kind != "boss" && !(entry.IsTutorial && entry.BaseBossMap.Kind == "map") ||
            entry.Maze.MapSpecification is not { Count: > 0 } maps ||
            entry.Maze.ConditionalMaps.Any(map => !map.IsConditional || entry.Maze.BossEntrance is null ||
                !maps.Any(source => source.Position == map.Position && source.Kind == map.Kind)) ||
            maps.Any(map => map.Position.X < 0 || map.Position.Y < 0 || map.Position.X >= entry.Maze.Width || map.Position.Y >= entry.Maze.Height ||
                map.Kind is not ("map" or "boss")) || approvedRoute.Rooms.Count == 0 ||
            approvedRoute.Rooms.Count != maps.Count + entry.Maze.LayeredMaps.Count + entry.Maze.ConditionalMaps.Count ||
            approvedRoute.Rooms.Select(plan => plan.Room.Map).Distinct().Count() != approvedRoute.Rooms.Count ||
            approvedRoute.Rooms.Any(plan => !maps.Contains(plan.Room.Map) && !entry.Maze.LayeredMaps.Contains(plan.Room.Map) && !entry.Maze.ConditionalMaps.Contains(plan.Room.Map)) ||
            !maps.Contains(entry.StartMap) || !maps.Contains(entry.BaseBossMap) ||
            !maps.Contains(entry.BossMap) && !entry.Maze.LayeredMaps.Contains(entry.BossMap) && !entry.Maze.ConditionalMaps.Contains(entry.BossMap) ||
            entry.Maze.LayeredMaps.Any(map => map.Kind != "layered" || !maps.Any(baseMap => baseMap.Position == map.Position)))
            return false;
        var keys = new HashSet<ushort>();
        foreach (DungeonMonsterPlan plan in approvedRoute.Rooms)
        {
            // 不把未支持生成规则或缺失资源行替换成空房间。
            DungeonMonsterPlan resourcePlan;
            try
            {
                // 入场与复验采用同一训练策略；普通副本继续要求完整特殊父子演员。
                resourcePlan = DungeonMonsterPlan.FromRoom(plan.Room, entry.CharacterId, 1, entry.BasisLevel, includeSpecial: !entry.IsTutorial);
            }
            catch (Exception error) when (error is NotSupportedException or InvalidDataException)
            {
                reason = "unsupported: 房间怪物生成规则未闭合";
                return false;
            }
            if (plan.Monsters.Count != resourcePlan.Monsters.Count || plan.Monsters.Zip(resourcePlan.Monsters).Any(pair =>
                pair.First.SourceOrdinal != pair.Second.SourceOrdinal || pair.First.Source != pair.Second.Source ||
                pair.First.AiCharacter != pair.Second.AiCharacter || pair.First.MonsterId != pair.Second.MonsterId ||
                pair.First.CandidateLevel != pair.Second.CandidateLevel || pair.First.SpawnType != pair.Second.SpawnType ||
                pair.First.IsBlocking != pair.Second.IsBlocking || pair.First.ObjectKey == entry.CharacterId || !keys.Add(pair.First.ObjectKey)))
            {
                reason = "invalid: 房间怪物身份不完整或重复";
                return false;
            }
            if (plan.SpecialObjects.Count != resourcePlan.SpecialObjects.Count || plan.SpecialObjects.Any(actor => actor.ObjectKey == entry.CharacterId ||
                !keys.Add(actor.ObjectKey) || !resourcePlan.SpecialObjects.Any(expected => expected.Source == actor.Source)) ||
                plan.SpecialObjects.Select(actor => actor.Source.SourceOrdinal).Distinct().Count() != plan.SpecialObjects.Count)
                return false;
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

    public DungeonRunResult CompleteLoading(DungeonRunIdentity expected, Guid actionId, bool hasMatchingQuest = false)
    {
        if (Replay(expected, actionId, "loaded", null) is { } replay)
            return replay;
        if (pendingDeath is not null || !Matches(expected) || Phase != SinglePlayerDungeonPhase.Loading || actionId == Guid.Empty || !CanAdvance())
            return Result(DungeonRunOutcome.Rejected);
        int slot = InitializationCounterSupported ? checked(entry.Maze.Width * current.Plan.Room.Map.Position.Y + current.Plan.Room.Map.Position.X) : 0;
        bool first = LastLoadingFact is null;
        bool initialized = InitializationCounterSupported && (!entry.IndividualMapMovement || !initializedMapSlots.Contains(slot));
        uint count = checked(ConfirmedInitializationCount + (!first && initialized && hasMatchingQuest ? 1u : 0u));
        acceptedActions.Add(actionId, new(expected, "loaded", null));
        // NOTI28建立新Run并清统计；接取不重置。缓存安装与活跃任务计数分开，完成仍由真实清图判定。
        if (InitializationCounterSupported)
            initializedMapSlots.Add(slot);
        ConfirmedInitializationCount = count;
        LastLoadingFact = new(expected, actionId, entry.DungeonId, current.Plan.Room.Map, entry.Maze.SourceOrdinal,
            first, initialized, count, MazeInitializationThreshold) { InitializationCounterSupported = InitializationCounterSupported };
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
            current.Elevator is { Completed: false } || !current.Cleared || HasPendingLayer() || !connections.Contains((current.Plan.Room.Map.Position, target)) || !rooms.TryGetValue(target, out Room? next))
            return Result(DungeonRunOutcome.Rejected);
        acceptedActions.Add(actionId, new(expected, "move", target));
        if (storyRooms.Length > 0 && (storyStageIndex < 0 || current.Plan.Room.Map == storyRooms[storyStageIndex].Plan.Room.Map) &&
            storyStageIndex + 1 < storyRooms.Length && storyRooms[storyStageIndex+1].Plan.Room.Map.Position == target)
            next = storyRooms[++storyStageIndex];
        if (BossEntranceOpen && next.Plan.Room.Map.StoryStageIndex < 0 && current.Plan.Room.Map.Kind != "layered" &&
            conditionalRooms.TryGetValue(target,out var replacement))
            next = replacement;
        current = next;
        mapOperation = DungeonMapOperation.Current;
        mapPayload = DungeonMapPayload.Build;
        pendingLayerBaseAck = null;
        Phase = SinglePlayerDungeonPhase.Loading;
        Advance();
        return Result(DungeonRunOutcome.Accepted);
    }

    public DungeonRunResult MoveLayer(DungeonRunIdentity expected, Guid actionId, DungeonPoint target, bool nextLayer)
    {
        string kind = nextLayer ? "layer" : "base";
        if (Replay(expected,actionId,kind,target) is { } replay) return replay;
        if (!Matches(expected) || Phase != SinglePlayerDungeonPhase.Dungeon || actionId == Guid.Empty || !CanAdvance() ||
            pendingDeath is not null || pendingSettlement is not null || current.Elevator is { Completed: false } || current.Plan.Room.Map.Position != target)
            return Result(DungeonRunOutcome.Rejected);
        Room next;
        var layers = entry.Maze.LayeredMaps.Where(map => map.Position == target).ToArray();
        int at = current.Plan.Room.Map.Kind == "layered" ? Array.IndexOf(layers,current.Plan.Room.Map)+1 : 0;
        // 原生kind1同时表示下一层和末层返回；不能把末层结束当作越界请求。
        if (nextLayer && at < layers.Length)
        {
            if (at > 0 && !current.Cleared || enteredLayers.Contains(layers[at]))
                return Result(DungeonRunOutcome.Rejected);
            if (at == 0) layerSources[target] = current;
            enteredLayers.Add(layers[at]); next = layeredRooms[layers[at]];
            mapOperation = DungeonMapOperation.AdvanceLayer;
            mapPayload = at == 0 ? DungeonMapPayload.Build : DungeonMapPayload.Refresh;
        }
        else
        {
            if (current.Plan.Room.Map.Kind != "layered" || !current.Cleared || HasPendingLayer()) return Result(DungeonRunOutcome.Rejected);
            next = layerSources[target];
            mapOperation = DungeonMapOperation.RestoreBase;
            mapPayload = DungeonMapPayload.Cached;
            pendingLayerBaseAck = target;
        }
        acceptedActions.Add(actionId,new(expected,kind,target)); current = next;
        Phase = SinglePlayerDungeonPhase.Loading; Advance(); return Result(DungeonRunOutcome.Accepted);
    }

    internal bool ConfirmLayerBase(DungeonRunIdentity expected, DungeonPoint target)
    {
        if (!Matches(expected) || Phase is not (SinglePlayerDungeonPhase.Loading or SinglePlayerDungeonPhase.Dungeon) ||
            pendingDeath is not null || pendingSettlement is not null || pendingLayerBaseAck != target ||
            current.Plan.Room.Map.Position != target || current.Plan.Room.Map.Kind == "layered") return false;
        pendingLayerBaseAck = null;
        return true;
    }

    private bool HasPendingLayer()
    {
        if (current.Plan.Room.Map.Kind != "layered") return false;
        var layers = entry.Maze.LayeredMaps.Where(map => map.Position == current.Plan.Room.Map.Position).ToArray();
        int index = Array.IndexOf(layers,current.Plan.Room.Map);
        return index < 0 || index + 1 < layers.Length;
    }

    public DungeonRunResult Return(DungeonRunIdentity expected, Guid actionId)
    {
        if (Replay(expected, actionId, "return", null) is { } replay)
            return replay;
        if (pendingDeath is not null || pendingSettlement is not null || !Matches(expected) || Phase == SinglePlayerDungeonPhase.Returned || actionId == Guid.Empty || identity.Epoch == long.MaxValue)
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
        if (!Matches(confirmed.Identity) || Phase is not (SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement) ||
            pendingSettlement is not null || confirmed.EventId == Guid.Empty ||
            !CanAdvance() || confirmed.CombatSequence <= 0 || combatSequences.Contains(confirmed.CombatSequence) ||
            CurrentLivingMonster(confirmed.MonsterKey) is null)
            return new(DungeonRunOutcome.Rejected);
        var target = current.Plan.Monsters.Single(monster => monster.ObjectKey == confirmed.MonsterKey);
        bool conditionClears = Phase == SinglePlayerDungeonPhase.Dungeon && MatchesClearCondition(target) &&
            !HasPendingBossDeaths(confirmed.MonsterKey);
        bool settles = Phase == SinglePlayerDungeonPhase.Dungeon && (TerminalBossCleared(confirmed.MonsterKey) ||
            conditionClears && IsTerminalRoom());
        // 明确的猎杀条件同样清退当前房间；非终点不产生通关事实，清退不产生击杀奖励。
        ushort[] culled = settles || conditionClears ? current.Plan.Monsters.Where(monster => monster.IsBlocking &&
            (conditionClears || !monster.IsBoss) &&
            monster.ObjectKey != confirmed.MonsterKey && !current.Dead.Contains(monster.ObjectKey)).Select(monster => monster.ObjectKey).ToArray() : [];
        DungeonRunIdentity nextIdentity = settles ? identity with { Epoch = checked(identity.Epoch + 1) } : identity;
        var nextSnapshot = new DungeonRunSnapshot(nextIdentity,
            settles ? SinglePlayerDungeonPhase.Settlement : Phase, current.Plan.Room.Map,
            Array.AsReadOnly(current.Plan.Monsters.Where(monster => !current.Dead.Contains(monster.ObjectKey) &&
                !current.Culled.Contains(monster.ObjectKey) && !culled.Contains(monster.ObjectKey) &&
                monster.ObjectKey != confirmed.MonsterKey).Select(monster => monster.ObjectKey).ToArray()))
            { MapOperation = mapOperation,MapPayload = mapPayload };
        DungeonClearFact? clear = settles
            ? new(nextIdentity, entry.DungeonId, Array.AsReadOnly(deaths.Keys.Append(confirmed.EventId).ToArray())) { ClearMap = current.Plan.Room.Map } : null;
        pendingDeath = new(confirmed, target,
            new(DungeonRunOutcome.Accepted, nextSnapshot, clear) { CulledMonsterKeys = Array.AsReadOnly(culled) });
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
        current.Culled.UnionWith(prepared.Result.CulledMonsterKeys);
        combatSequences.Add(confirmed.CombatSequence);
        deaths.Add(confirmed.EventId, confirmed);
        committedPreparations.Add(prepared);
        identity = prepared.Result.Snapshot.Identity;
        Phase = prepared.Result.Snapshot.Phase;
        if (prepared.Result.ClearFact is { } clear)
            ClearFact = clear;
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

    public DungeonSettlementPreparation PrepareSettlement(DungeonClearFact fact, ReadOnlySpan<byte> reportBody,
        DungeonClearExperienceReward reward, DungeonCardReward? card = null, DungeonCardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(reward);
        if (!ReferenceEquals(ClearFact, fact) || fact.Identity != identity || Phase != SinglePlayerDungeonPhase.Settlement ||
            pendingDeath is not null || reportBody.IsEmpty || reportBody.Length > 4096 || reward.TotalExperience == 0 && !entry.IsTutorial ||
            (ulong)reward.BaseExperience + reward.RankExperience != reward.TotalExperience || reward.RankBonusIndex is < -1 or > 4 ||
            string.IsNullOrWhiteSpace(reward.RuleId) || reward.RuleId.Length > 128 ||
            string.IsNullOrWhiteSpace(reward.ResourceSource) || reward.ResourceSource.Length > 1024)
            return new(DungeonRunOutcome.Rejected);
        if ((pendingSettlement ?? committedSettlement) is { } frozen)
            return frozen.ReportBody.SequenceEqual(reportBody) && frozen.Reward == reward && frozen.Card == card && frozen.Options == options
                ? new(committedSettlement is null ? DungeonRunOutcome.Accepted : DungeonRunOutcome.Duplicate, frozen)
                : new(DungeonRunOutcome.Rejected);
        card?.Validate();
        pendingSettlement = new(fact, reportBody, reward, current.Plan.Room.Map.Id, card, options);
        return new(DungeonRunOutcome.Accepted, pendingSettlement);
    }

    public bool IsPendingSettlement(PreparedDungeonSettlement prepared) => prepared is not null &&
        ReferenceEquals(pendingSettlement, prepared) && Phase == SinglePlayerDungeonPhase.Settlement &&
        ReferenceEquals(ClearFact, prepared.Fact) && prepared.Fact.Identity == identity;

    public bool IsCommittedSettlement(PreparedDungeonSettlement prepared) => prepared is not null &&
        ReferenceEquals(committedSettlement, prepared) && Phase == SinglePlayerDungeonPhase.Settlement &&
        ReferenceEquals(ClearFact, prepared.Fact) && prepared.Fact.Identity == identity;

    public DungeonRunOutcome ConfirmCommittedSettlement(PreparedDungeonSettlement prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (IsCommittedSettlement(prepared))
            return DungeonRunOutcome.Duplicate;
        if (!IsPendingSettlement(prepared))
            return DungeonRunOutcome.Rejected;
        committedSettlement = prepared;
        pendingSettlement = null;
        return DungeonRunOutcome.Accepted;
    }

    private static bool SameDeath(ConfirmedDungeonMonsterDeath first, ConfirmedDungeonMonsterDeath second) =>
        ReferenceEquals(first.Authority, second.Authority) && first.EventId == second.EventId && first.Identity == second.Identity &&
        first.MonsterKey == second.MonsterKey && first.CombatSequence == second.CombatSequence;

    private DungeonRunResult SettleIfCleared()
    {
        if (TerminalBossCleared() && deaths.Count > 0)
        {
            Phase = SinglePlayerDungeonPhase.Settlement;
            Advance();
            ClearFact = new(identity, entry.DungeonId, Array.AsReadOnly(deaths.Keys.ToArray())) { ClearMap = current.Plan.Room.Map };
            return new(DungeonRunOutcome.Accepted, Snapshot, ClearFact);
        }
        return Result(DungeonRunOutcome.Accepted);
    }

    private bool MatchesClearCondition(DungeonPlannedMonster monster) => entry.Maze.ClearConditions is [var condition] &&
        condition.Count == 1 && condition.TargetId == monster.MonsterId && monster.AiCharacter is null &&
        (condition.Type == "hunt monster" || condition.Type == "hunt boss" && monster.IsBoss);

    private bool IsTerminalRoom() => entry.IsTerminalMap(current.Plan.Room.Map) &&
        (storyRooms.Length == 0 || storyStageIndex == storyRooms.Length-1);

    private bool HasPendingBossDeaths(ushort? preparedMonsterKey)
    {
        int ordinaryBosses = current.Plan.Monsters.Count(monster => monster.IsBoss && monster.AiCharacter is null);
        return current.Plan.Monsters.Any(monster => monster.IsBoss && (monster.AiCharacter is not null || ordinaryBosses > 1) &&
            !current.Dead.Contains(monster.ObjectKey) && monster.ObjectKey != preparedMonsterKey);
    }

    private bool TerminalBossCleared(ushort? preparedMonsterKey = null)
    {
        // 正常通关由当前终点Boss死亡决定；未走过的支路不属于全图清怪要求。
        if (!IsTerminalRoom() || HasPendingBossDeaths(preparedMonsterKey))
            return false;
        if (entry.IsTutorial && current.Plan.Room.TutorialCinematics.Any(movie => movie.DestroyTargets.Count > 0) &&
            current.Plan.Monsters.Any(monster => monster.IsBlocking && !current.Dead.Contains(monster.ObjectKey) &&
                monster.ObjectKey != preparedMonsterKey)) return false;
        if (current.Plan.Monsters.All(monster => !monster.IsBlocking || current.Dead.Contains(monster.ObjectKey) ||
            monster.ObjectKey == preparedMonsterKey))
            return true;
        bool hasBlockingBoss = false;
        foreach (var monster in current.Plan.Monsters)
        {
            if (!monster.IsBoss)
                continue;
            hasBlockingBoss = true;
            if (!current.Dead.Contains(monster.ObjectKey) && monster.ObjectKey != preparedMonsterKey)
                return false;
        }
        return hasBlockingBoss;
    }
    internal IReadOnlyList<ushort>? TutorialCinematicTargets(ushort targetKey)
    {
        if (!entry.IsTutorial || Phase != SinglePlayerDungeonPhase.Dungeon || !IsTerminalRoom() ||
            pendingDeath is not null || pendingSettlement is not null || !CanAdvance()) return null;
        if (current.Plan.Monsters.Any(monster => monster.AiCharacter?.Faction == "[monster]" &&
            !current.Dead.Contains(monster.ObjectKey) && !current.Culled.Contains(monster.ObjectKey))) return null;
        var target = current.Plan.Monsters.SingleOrDefault(monster => monster.ObjectKey == targetKey &&
            monster.AiCharacter is null && !monster.IsSpecialChild);
        if (target is null) return null;
        var remaining = current.Plan.Monsters.Where(monster => monster.IsBlocking &&
            !current.Dead.Contains(monster.ObjectKey) && !current.Culled.Contains(monster.ObjectKey)).ToArray();
        if (remaining.Any(monster => monster.AiCharacter is not null) || !current.Plan.Room.TutorialCinematics.Any(movie =>
            movie.DestroyTargets.Contains(target.SourceOrdinal) && remaining.All(monster => movie.MonsterActors.Contains(monster.SourceOrdinal))))
            return null;
        return remaining.Select(monster => monster.ObjectKey).Append(targetKey).Distinct().ToArray();
    }

    internal DungeonRunResult CompleteTutorialCinematic(DungeonRunIdentity expected, ushort targetKey)
    {
        if (!Matches(expected) || TutorialCinematicTargets(targetKey) is not { } targets) return Result(DungeonRunOutcome.Rejected);
        current.Dead.UnionWith(targets);
        Phase = SinglePlayerDungeonPhase.Settlement;
        Advance();
        // 剧情销毁不伪造战斗死亡，完成检查点另有自己的持久回执。
        ClearFact = new(identity, entry.DungeonId, Array.AsReadOnly(deaths.Keys.ToArray())) { ClearMap = current.Plan.Room.Map };
        return new(DungeonRunOutcome.Accepted, Snapshot, ClearFact);
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
