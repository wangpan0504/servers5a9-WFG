using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal enum SinglePlayerDungeonPhase { Loading, Dungeon, Settlement, Returned, TimedOut }
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
    public uint ConsumedFatigue { get; init; }
    public DungeonPoint BossPosition { get; init; }
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
    public DungeonPassiveClearSource? PassiveSource { get; init; }
}
internal sealed record DungeonPassiveClearSource(Guid OperationId, DungeonRunIdentity Identity, int TemplateId, int SourceOrdinal);
internal sealed record DungeonRoomClearFact(DungeonRunIdentity Identity, uint DungeonId, DungeonMap Map,
    IReadOnlyList<Guid> ConfirmedDeathEvents);
internal sealed class PreparedDungeonPassiveClear(DungeonPassiveClearSource source, DungeonRunResult result)
{
    public DungeonPassiveClearSource Source { get; } = source;
    public DungeonRunResult Result { get; } = result;
}
internal sealed record DungeonRunResult(DungeonRunOutcome Outcome, DungeonRunSnapshot Snapshot, DungeonClearFact? ClearFact = null)
{
    public IReadOnlyList<ushort> CulledMonsterKeys { get; init; } = [];
    public DungeonRoomClearFact? RoomClearFact { get; init; }
    public DungeonPoint? BossPositionUpdate { get; init; }
}
internal sealed record DungeonDeathPreparation(DungeonRunOutcome Outcome, PreparedDungeonDeath? Pending = null);

// 只代表已核验的待提交候选。数据库结果确定之前，死亡集和清图状态保持原样。
internal sealed class PreparedDungeonDeath(ConfirmedDungeonMonsterDeath death, DungeonPlannedMonster monster, DungeonRunResult result,
    int dummyStageIndex = -1)
{
    public ConfirmedDungeonMonsterDeath Death { get; } = death;
    public DungeonPlannedMonster Monster { get; } = monster;
    public DungeonRunResult Result { get; } = result;
    internal int DummyStageIndex { get; } = dummyStageIndex;
    internal DfoServer.Game.TagEvildom.TagEvildomClearFact? TagEvildomClear { get; init; }
    internal int? ImperialArenaRound { get; init; }
    internal DfoServer.Game.BattlePass.BattlePassDungeonActivity? BattlePassActivity { get; private set; }
    internal DfoServer.Game.BattlePass.BattlePassHellCosmoCompletion? BattlePassHellCosmoCompletion { get; private set; }
    bool battlePassHellCosmoCompletionFrozen;
    internal void FreezeBattlePassHellCosmoCompletion(SinglePlayerDungeonRun run, TimeProvider clock)
    {
        if (battlePassHellCosmoCompletionFrozen) return;
        BattlePassHellCosmoCompletion = DfoServer.Game.BattlePass.BattlePassHellCosmoCompletion.Prepare(run, this, clock);
        battlePassHellCosmoCompletionFrozen = true;
    }
    internal void FreezeBattlePassAltarQuestTarget(SinglePlayerDungeonRun run,
        DfoServer.Game.Characters.Character expected, DfoServer.GameWorld.Quests.QuestCatalog quests, TimeProvider clock)
    {
        if(battlePassActivityFrozen)return;
        BattlePassActivity=DfoServer.Game.BattlePass.BattlePassDungeonActivity.PrepareAltarQuestTarget(run,this,expected,quests,clock);
        battlePassActivityFrozen=true;
    }
    internal void FreezeBattlePassAltarNonTarget(SinglePlayerDungeonRun run,
        DfoServer.Game.Characters.Character expected, DfoServer.GameWorld.Quests.QuestCatalog quests, TimeProvider clock)
    {
        if(battlePassActivityFrozen)return;
        BattlePassActivity=DfoServer.Game.BattlePass.BattlePassDungeonActivity.PrepareAltarNonTarget(run,this,expected,quests,clock);
        battlePassActivityFrozen=true;
    }
    bool battlePassActivityFrozen;
    internal void FreezeBattlePassActivity(SinglePlayerDungeonRun run, TimeProvider clock,
        DfoServer.Game.Raids.AntonRaidBattle? battle = null, DfoServer.Game.Raids.RaidActor? actor = null)
    {
        if(battlePassActivityFrozen)return;
        BattlePassActivity=DfoServer.Game.BattlePass.BattlePassDungeonActivity.PrepareDeath(run,this,clock,battle,actor);
        battlePassActivityFrozen=true;
    }

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
internal sealed record DungeonFixedRoute(IReadOnlyList<DungeonMonsterPlan> Rooms, IReadOnlyList<DungeonRouteConnection> Connections)
{
    public int? PassiveClearTarget { get; init; }
    public IReadOnlyList<DungeonMonsterPlan> AllRooms => Rooms;
}

internal sealed partial class SinglePlayerDungeonRun
{
    internal DungeonQuestFacts QuestFacts { get; }
    internal DfoServer.Game.Quests.QuestMarriagePartyClear? MarriagePartyClear { get; set; }
    private sealed class Room(DungeonMonsterPlan plan)
    {
        public DungeonMonsterPlan Plan { get; set; } = plan;
        public bool BattleLevelsFrozen { get; set; }
        public MonsterAiProfile? MonsterAi { get; set; }
        public DungeonElevatorState? Elevator { get; } = DungeonElevatorState.Create(plan);
        public DungeonAltarState? Altar { get; set; } = plan.Altar is { } altar ? new(altar) : null;
        public HashSet<ushort> Dead { get; } = [];
        public HashSet<ushort> Culled { get; } = [];
        public bool Loaded { get; set; }
        public bool Cleared => Altar is { } altar ? altar.Completed :
            Plan.Monsters.All(monster => !monster.IsBlocking || Dead.Contains(monster.ObjectKey) || Culled.Contains(monster.ObjectKey));
    }
    private sealed record ActionFact(DungeonRunIdentity Identity, string Kind, DungeonPoint? Target);
    private DungeonEntryPlan entry;
    private readonly DungeonCombatAuthority authority;
    internal DungeonBattleLevelRooms BattleLevels { get; }
    internal DungeonMonsterAiRooms MonsterAiRooms { get; }
    private readonly Dictionary<DungeonPoint, Room> rooms;
    private readonly Dictionary<DungeonMap, Room> layeredRooms;
    private readonly Room[] storyRooms;
    private readonly Room[] dummyRooms;
    private int activeDummyStage;
    private readonly Room[] allRooms;
    private readonly Dictionary<DungeonPoint, Room> conditionalRooms;
    private int storyStageIndex;
    private DungeonPoint storyBossPosition;
    private readonly bool storyDummyBosses;
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
    private PreparedDungeonPassiveClear? pendingPassiveClear;
    private PreparedDungeonPassiveClear? committedPassiveClear;
    private PreparedDungeonSettlement? pendingSettlement;
    private PreparedDungeonSettlement? committedSettlement;
    private Room current;
    private DungeonRunIdentity identity;
    private uint consumedFatigue;
    internal bool HasCommittedPassiveClearCondition { get; private set; }
    private readonly int? passiveClearTarget;
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
    internal int FatigueAccelerationPercent { get; private set; }
    internal void FreezeFatigueAcceleration(int percent)
    {
        if (percent < 0 || Phase != SinglePlayerDungeonPhase.Loading || acceptedActions.Count != 0)
            throw new InvalidOperationException("疲劳燃烧只能在正式入场提交后冻结");
        FatigueAccelerationPercent = percent;
    }
    internal DungeonMonsterPlan CurrentMonsterPlan => current.Plan;
    // 友方APC在原客户端跨房随行；只读取本实例真正加载过的资源演员，不把未来房间算作存活。
    internal DungeonPlannedMonster? PublishedFriendlyActor(ushort key) => carriedCompanions.GetValueOrDefault(key) ?? allRooms.Where(room => room.Loaded)
        .SelectMany(room => room.Plan.Monsters).SingleOrDefault(monster => monster.ObjectKey == key &&
            monster.AiCharacter is not null && monster.IsFriendly && !monster.CanReportDeath);

    internal bool HasLivingFriendlyActor(int id, DungeonRunSnapshot snapshot) =>
        carriedCompanions.Values.Any(monster => monster.MonsterId == id && !retiredCarriedCompanions.Contains(monster.ObjectKey)) ||
        allRooms.Any(room => room.Loaded &&
        room.Plan.Monsters.Any(monster => monster.MonsterId == id && monster.AiCharacter is not null && monster.IsFriendly &&
            !room.Dead.Contains(monster.ObjectKey) && !room.Culled.Contains(monster.ObjectKey) &&
            (!ReferenceEquals(room,current) || snapshot.LivingMonsters.Contains(monster.ObjectKey))));
    internal bool HasPendingDeath => pendingDeath is not null || pendingPassiveClear is not null;
    internal bool HasPendingPassiveClear => pendingPassiveClear is not null;
    internal uint DungeonId => entry.DungeonId;
    internal DungeonElevatorState? CurrentElevator => current.Elevator;
    internal DungeonAltarState? CurrentAltar => current.Altar;
    internal int CompletedAltarRounds => allRooms.Where(room => !ReferenceEquals(room, current) && room.Altar?.Completed == true)
        .Sum(room => room.Altar!.Plan.Definition.RoundCount);
    internal bool AdvanceAltar(long now) => !HasPendingDeath && current.Altar?.Advance(current.Dead, now) == true;
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
        if (active.AcceptanceId == Guid.Empty || HasPendingDeath || pendingSettlement is not null ||
            entry.QuestAcceptances.Any(row => row.QuestId == active.QuestId))
            throw new InvalidOperationException("子任务不能复用本实例已绑定的旧接取");
        entry = entry with { QuestAcceptances = entry.QuestAcceptances.Add(active) };
    }
    internal DungeonPlannedMonster? CurrentLivingMonster(ushort key) =>
        Phase is SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement &&
        !current.Dead.Contains(key) && !current.Culled.Contains(key) && (current.Altar is null || current.Altar.WasPublished(key))
            ? current.Plan.Monsters.SingleOrDefault(monster => monster.ObjectKey == key && (monster.CanReportDeath || monster.IsSpecialChild ||
                entry.IsTutorial && (monster.IsScriptedRetirement || monster.AiCharacter?.Faction is "[character]" or "[neutral]"))) : null;
    // 实验性（未在95客户端实证）：深渊派对波次怪不在 Monsters 里，客户端上报其死亡时只登记，不参与清房/经验/掉落。
    internal DungeonRunOutcome RetireHellWave(ushort key)
    {
        if (Phase != SinglePlayerDungeonPhase.Dungeon || pendingSettlement is not null || HasPendingDeath || pendingPassiveClear is not null ||
            !current.Plan.HellWaves.Any(wave => wave.ObjectKey == key)) return DungeonRunOutcome.Rejected;
        return current.Dead.Add(key) ? DungeonRunOutcome.Accepted : DungeonRunOutcome.Duplicate;
    }
    // 最后一波里最后一只尚存的波次怪。前面的恶魔波次不掉深渊装备。
    internal bool IsLastLivingHellWaveMember(ushort key)
    {
        var waves = current.Plan.Monsters.Where(monster => monster.HellWave is not null).ToArray();
        // 时空裂缝没有柱子波次：奖励骰只绑在终点 Boss 这一只身上，不做波次存活比较。
        if (waves.Length == 0)
            return current.Plan.Monsters.Any(monster => monster.ObjectKey == key && monster.HellWave is null && monster.HellRewardRolls > 0);
        var target = waves.SingleOrDefault(monster => monster.ObjectKey == key);
        if (target?.HellWave is not { } wave || waves.Max(monster => monster.HellWave!.Order) != wave.Order) return false;
        return waves.Where(monster => monster.HellWave!.Order == wave.Order).All(monster =>
            monster.ObjectKey == key || current.Dead.Contains(monster.ObjectKey) || current.Culled.Contains(monster.ObjectKey));
    }
    internal bool IsCulledMonster(ushort key) => Phase == SinglePlayerDungeonPhase.Settlement && ClearFact is not null && current.Culled.Contains(key);
    // 友方、非敌对对象退场只更新显示集合，不加入持久击杀或清图事实；未知key不改变实例。
    internal void RetireNonCombatObject(ushort key)
    {
        if (HasPendingDeath || pendingSettlement is not null) return;
        if (carriedCompanions.ContainsKey(key))
        {
            retiredCarriedCompanions.Add(key);
            return;
        }
        var room = current.Plan.Monsters.Any(monster => monster.ObjectKey == key && !monster.CanReportDeath) ? current :
            allRooms.SingleOrDefault(candidate => candidate.Loaded && candidate.Plan.Monsters.Any(monster =>
                monster.ObjectKey == key && monster.AiCharacter is not null && monster.IsFriendly && !monster.CanReportDeath));
        room?.Dead.Add(key);
    }
    SinglePlayerDungeonPhase phase = SinglePlayerDungeonPhase.Loading;
    // 截止瞬间停止新战斗与移动，不依赖后台任务何时取得命令门；已准备的事务仍由原回执确认。
    public SinglePlayerDungeonPhase Phase
    {
        get => phase != SinglePlayerDungeonPhase.Returned && (LostLandClock?.Elapsed == true || Strongway2019TimeElapsed)
            ? SinglePlayerDungeonPhase.TimedOut : phase;
        private set => phase = value;
    }
    internal LostLandRunClock? LostLandClock { get; }
    readonly TimeProvider strongway2019Clock;
    long? strongway2019Started;
    bool Strongway2019TimeElapsed => phase is SinglePlayerDungeonPhase.Loading or SinglePlayerDungeonPhase.Dungeon &&
        entry.Strongway2019TimeLimitSeconds is { } seconds && strongway2019Started is { } started &&
        strongway2019Clock.GetElapsedTime(started) >= TimeSpan.FromSeconds(seconds);
    DungeonClearFact? clearFact;
    public DungeonClearFact? ClearFact
    {
        get => clearFact;
        private set
        {
            clearFact = value;
            if (value is not null) FreezeStrongway2019Elapsed();
        }
    }
    internal void BindPartyInstance(Guid instance)
    {
        if (tagEvildom is not null || instance == Guid.Empty || Phase != SinglePlayerDungeonPhase.Loading || allRooms.Any(room => room.Loaded))
            throw new InvalidOperationException("共享实例身份只能在入场准备阶段绑定");
        identity = identity with { InstanceId = instance };
    }
    public bool HasPendingSettlement => pendingSettlement is not null || pendingAltarSettlement is not null || pendingDespairClear is not null;
    // 剧情入场不扣持久疲劳；宠物仍按真实首次加载的房间成长，回访不重复计点。
    public int CreatureExperiencePoints => entry.IsTutorial || entry.IsTrainingRoom || entry.IsClassExperience || entry.IsQuestReplay || entry.IsPractice
        ? 0 : allRooms.Count(room => room.Loaded);
    public DungeonRunSnapshot Snapshot => new(identity, Phase, current.Plan.Room.Map,
        Array.AsReadOnly(current.Plan.Monsters.Where(monster => !current.Dead.Contains(monster.ObjectKey) &&
            !current.Culled.Contains(monster.ObjectKey) && (current.Altar is null || current.Altar.WasPublished(monster.ObjectKey)))
            .Select(monster => monster.ObjectKey).ToArray()))
        { MapOperation = mapOperation, MapPayload = mapPayload, ConsumedFatigue = consumedFatigue, BossPosition = BossPosition(activeDummyStage) };

    // 跨会话展示守卫只读不可变身份及房间，不能构造会枚举可变怪物集合的完整Snapshot。
    internal (DungeonRunIdentity Identity, DungeonMap Map) Scene => (identity, current.Plan.Room.Map);

    private DungeonPoint BossPosition(int stage) => dummyRooms.Length > 0 ? dummyRooms[stage].Plan.Room.Map.Position : storyDummyBosses ? storyBossPosition : entry.BossMap.Position;

    // 父脚本子actor的收尾不进入普通击杀集合，不能借此触发清图、经验或任务事实。
    public DungeonRunOutcome RetireSpecialChild(ConfirmedDungeonMonsterDeath death)
    {
        if (!ReferenceEquals(death.Authority, authority) || death.Identity != identity || Phase != SinglePlayerDungeonPhase.Dungeon ||
            death.EventId == Guid.Empty || death.CombatSequence <= 0 || HasPendingDeath || pendingSettlement is not null ||
            !current.Plan.Monsters.Any(monster => !monster.IsBlocking && monster.ObjectKey == death.MonsterKey &&
                (monster.IsSpecialChild || entry.IsTutorial && (monster.IsScriptedRetirement || monster.AiCharacter?.Faction is "[character]" or "[neutral]"))))
            return DungeonRunOutcome.Rejected;
        return current.Dead.Add(death.MonsterKey) ? DungeonRunOutcome.Accepted : DungeonRunOutcome.Duplicate;
    }

    private SinglePlayerDungeonRun(DungeonEntryPlan entry, long attempt, long epoch, DungeonFixedRoute route, DungeonCombatAuthority authority, TimeProvider? time,
        DungeonBattleLevelSettings? battleLevels, DungeonBattleLevelRooms? sharedBattleLevels,
        DungeonMonsterAiSettings? monsterAi, DungeonMonsterAiRooms? sharedMonsterAi)
    {
        BattleLevels = sharedBattleLevels ?? new(battleLevels);
        MonsterAiRooms = sharedMonsterAi ?? new(monsterAi, (entry.FiendwarScale?.HpPercent ?? 100) / 100f,
            (entry.FiendwarScale?.AttackPercent ?? 100) / 100f);
        scriptedTransitLayer = ResolveScriptedTransitLayer(entry.PartyRoute ?? entry, route);
        if (scriptedTransitLayer is not null)
            entry = entry with { TerminalLayerMap = null, TerminalLayerQuestId = 0,
                TerminalLayerQuestSlot = byte.MaxValue, TerminalLayerAcceptanceId = Guid.Empty };
        this.entry = entry;
        QuestFacts = new(time ?? TimeProvider.System);
        strongway2019Clock = time ?? TimeProvider.System;
        LostLandClock = entry.LostLand is { } admission ? new(admission, time ?? TimeProvider.System) : null;
        this.authority = authority;
        var baseRooms = route.Rooms.Where(plan => entry.Maze.MapSpecification!.Contains(plan.Room.Map)).Select(plan => new Room(
            plan with { Monsters = Array.AsReadOnly(plan.Monsters.ToArray()) })).ToArray();
        rooms = baseRooms.GroupBy(room => room.Plan.Room.Map.Position).ToDictionary(group => group.Key,
            group => group.FirstOrDefault(room => room.Plan.Room.Map.StoryStageIndex < 0) ?? group.First());
        storyRooms = baseRooms.Where(room => room.Plan.Room.Map.StoryStageIndex >= 0).OrderBy(room => room.Plan.Room.Map.StoryStageIndex).ToArray();
        layeredRooms = route.Rooms.Where(plan => plan.Room.Map.Kind == "layered").ToDictionary(plan => plan.Room.Map,plan => new Room(plan));
        conditionalRooms = route.Rooms.Where(plan => plan.Room.Map.IsConditional).ToDictionary(plan => plan.Room.Map.Position,plan => new Room(plan));
        dummyRooms = entry.Maze.DummyStages.Select(map => baseRooms.FirstOrDefault(room => room.Plan.Room.Map == map) ??
            new Room(route.Rooms.Single(plan => plan.Room.Map == map))).ToArray();
        allRooms = baseRooms.Concat(dummyRooms).Concat(layeredRooms.Values).Concat(conditionalRooms.Values).Distinct().ToArray();
        passiveClearTarget = route.PassiveClearTarget;
        if (ResolvePassiveLayerTerminal() is { } passiveTerminal)
            this.entry = entry with { PassiveClearTerminalMap = passiveTerminal.Plan.Room.Map };
        else if (ResolveCinematicLayerTerminal() is { } cinematicTerminal)
            this.entry = entry with { CinematicClearTerminalMap = cinematicTerminal.Plan.Room.Map };
        else if (ResolveLayeredBossTerminal() is { } terminal)
            this.entry = entry with { BossMap = terminal.Plan.Room.Map };
        else if (ResolveCinematicBossTerminal() is { } cinematicBoss)
            this.entry = entry with { BossMap = cinematicBoss.Plan.Room.Map };
        connections = route.Connections.SelectMany(connection => new[] { (connection.First, connection.Second), (connection.Second, connection.First) }).ToHashSet();
        current = baseRooms.Single(room => room.Plan.Room.Map == entry.StartMap);
        storyStageIndex = entry.StartMap.StoryStageIndex;
        // 只有完整MAP级dummy链拥有动态标记，普通剧情分段保留原终点坐标。
        storyDummyBosses = storyRooms.Length >= 2 && storyRooms.SkipLast(1).All(room =>
            room.Plan.Room.DeclaredType == "[dummy]" && room.Plan.Monsters.Count(monster => monster.IsDummyMapBoss && monster.IsBlocking) == 1) &&
            storyRooms[^1].Plan.Monsters.Any(monster => monster.IsBoss);
        storyBossPosition = storyDummyBosses ? storyRooms[0].Plan.Room.Map.Position : entry.BossMap.Position;
        identity = new(entry.CharacterId, entry.QuestId, attempt, Guid.NewGuid(), epoch);
        var grid = entry.Maze.GreedGrid;
        InitializationCounterSupported = entry.InitializationCounterSupported && entry.Maze.LayeredMaps.Count == 0 && storyRooms.Length == 0 && grid.Supported;
        int positiveCells = grid.Cells?.Count(cell => cell.Index > 0) ?? 0;
        MazeInitializationThreshold = InitializationCounterSupported && positiveCells > 1 ? checked((uint)(positiveCells - 1)) : null;
        FreezeCurrentRoom();
    }

    public static bool TryCreate(DungeonEntryPlan entry, long taskAttempt, long initialEpoch, DungeonFixedRoute? approvedRoute,
        DungeonCombatAuthority? combatAuthority, out SinglePlayerDungeonRun? run, out string reason, TimeProvider? time = null,
        DungeonBattleLevelSettings? battleLevels = null, DungeonBattleLevelRooms? sharedBattleLevels = null,
        DungeonMonsterAiSettings? monsterAi = null, DungeonMonsterAiRooms? sharedMonsterAi = null)
    {
        run = null;
        reason = "unsupported: 需要正式战斗权威及已确认门墙连接";
        if (approvedRoute is null || combatAuthority is null)
            return false;
        if ((entry.DungeonId == 4000 || entry.Maze.IsLostLand || entry.LostLand is not null) &&
            (entry.DungeonId != 4000 || !entry.Maze.IsLostLand || entry.LostLand is null || entry.QuestId != 0 ||
                entry.Difficulty != 0 || entry.IsPractice || entry.IsQuestReplay || entry.HellPartyMode != 0 || entry.PartyRoute is not null ||
                !entry.DisableFatigueConsumption || !entry.DisableGiveupPenalty || !entry.DisableClearReward ||
                entry.AllowsCompanionAi || !entry.Maze.StaticTopology.Supported ||
                entry.Maze.ClearConditions is not [{ Type: "limit time", TargetId: 600, Count: > 0 } timeLimit] ||
                entry.LostLand.OpaqueValue != timeLimit.Count))
        { reason = "invalid: 遗忘之地专用准入不完整"; return false; }
        // 此字段只由本Run依据完整批准路线派生，调用方不能预先声明一个任意末层。
        if (entry.PassiveClearTerminalMap is not null || entry.CinematicClearTerminalMap is not null)
        {
            reason = "invalid: 物件或演出末层终点只能由原实例批准路线冻结";
            return false;
        }
        if (!ValidExplicitTerminalLayer(entry, approvedRoute))
        {
            reason = "invalid: 显式末层终点的资源或原接取身份不完整";
            return false;
        }
        if (approvedRoute.PassiveClearTarget is { } passiveTarget &&
            (entry.Maze.ClearConditions is not [{ Type: "destroy object", Count: 1 } passiveCondition] ||
                passiveCondition.TargetId != passiveTarget ||
                !approvedRoute.Rooms.Any(room => room.Room.PassiveObjects.Any(source => source.ObjectId == passiveTarget))))
            return false;
        if (entry.Maze.StoryStages.Count > 0 && (entry.BossMap != entry.Maze.StoryStages[^1] ||
            entry.Maze.StoryStages.Where((map,index) => map.StoryStageIndex != index).Any())) return false;
        if (entry.Maze.DummyStages.Count > 0 && (entry.Maze.MapSpecification is null || entry.Maze.StoryStages.Count != 0 || entry.Maze.QuestConnectionTokens.Count != 0 ||
            entry.Maze.DummyStages.Count < 2 || entry.BossMap != entry.Maze.DummyStages[0] ||
            entry.Maze.DummyStages.Where((map,index) => map.DummyStageIndex != index || map.Kind != "boss").Any() ||
            entry.Maze.DummyStages.Any(map => !entry.Maze.MapSpecification!.Any(source => source.Position == map.Position)))) return false;
        if (entry.CharacterId == 0 || entry.CharacterRevision < 0 || entry.QuestId >= 30000 ||
            entry.QuestId == 0 && (entry.ActiveQuestSlot != byte.MaxValue || entry.QuestAcceptanceId != Guid.Empty) ||
            taskAttempt <= 0 || initialEpoch <= 0 || initialEpoch >= long.MaxValue - 2)
        {
            reason = "invalid: 实例身份或任务尝试无效";
            return false;
        }
        // 非零请求借普通路线时，模式与原接取三元组必须共同成立，不能删改标志规避疲劳。
        if ((entry.IsOrdinaryHunt || entry.QuestId != 0 && entry.RouteQuestId == 0) &&
            (!entry.IsOrdinaryHunt || entry.IsTutorial || entry.IsQuestReplay || entry.IsTrainingRoom || entry.IsClassExperience ||
                entry.QuestId == 0 || entry.RouteQuestId != 0 || entry.Maze.QuestConnectionTokens.Count != 0 ||
                entry.ActiveQuestSlot > 29 || entry.QuestAcceptanceId == Guid.Empty ||
                entry.QuestAcceptances.Count(row => row.QuestId == entry.QuestId) != 1 ||
                entry.QuestAcceptances.Count(row => row.Slot == entry.ActiveQuestSlot) != 1 ||
                entry.QuestAcceptances.Count(row => row.AcceptanceId == entry.QuestAcceptanceId) != 1 ||
                !entry.QuestAcceptances.Any(row => row.QuestId == entry.QuestId && row.Slot == entry.ActiveQuestSlot &&
                    row.AcceptanceId == entry.QuestAcceptanceId)))
        {
            reason = "invalid: 普通猎杀路线与原请求接取身份不一致";
            return false;
        }
        if (entry.Maze.Width <= 0 || entry.Maze.Height <= 0 || entry.BaseBossMap.Kind != "boss" &&
                !(entry.IsTutorial && entry.BaseBossMap.Kind == "map") ||
            entry.Maze.MapSpecification is not { Count: > 0 } maps ||
            entry.Maze.ConditionalMaps.Any(map => !map.IsConditional || entry.Maze.BossEntrance is null ||
                !maps.Any(source => source.Position == map.Position && source.Kind == map.Kind)) ||
            maps.Any(map => map.Position.X < 0 || map.Position.Y < 0 || map.Position.X >= entry.Maze.Width || map.Position.Y >= entry.Maze.Height ||
                map.Kind is not ("map" or "boss")) || approvedRoute.Rooms.Count == 0 ||
            approvedRoute.Rooms.Count != maps.Concat(entry.Maze.DummyStages).Concat(entry.Maze.LayeredMaps).Concat(entry.Maze.ConditionalMaps).Distinct().Count() ||
            approvedRoute.Rooms.Select(plan => plan.Room.Map).Distinct().Count() != approvedRoute.Rooms.Count ||
            approvedRoute.Rooms.Any(plan => !maps.Contains(plan.Room.Map) && !entry.Maze.DummyStages.Contains(plan.Room.Map) &&
                !entry.Maze.LayeredMaps.Contains(plan.Room.Map) && !entry.Maze.ConditionalMaps.Contains(plan.Room.Map)) ||
            !maps.Contains(entry.StartMap) || !maps.Contains(entry.BaseBossMap) ||
            !maps.Contains(entry.BossMap) && !entry.Maze.LayeredMaps.Contains(entry.BossMap) && !entry.Maze.ConditionalMaps.Contains(entry.BossMap) ||
            entry.QuestClearMap is { } questMap && !entry.IsTerminalMap(questMap) && (!approvedRoute.Rooms.Any(room => room.Room.Map == questMap) ||
                approvedRoute.Rooms.Count(room => room.Room.Map.Id == questMap.Id) != 1) ||
            entry.Maze.LayeredMaps.Any(map => map.Kind != "layered" || !maps.Any(baseMap => baseMap.Position == map.Position)))
        {
            reason = "invalid: 路线地图、起终点或叠层声明不完整";
            return false;
        }
        var keys = new HashSet<ushort>();
        foreach (DungeonMonsterPlan plan in approvedRoute.AllRooms)
        {
            if (plan.Altar is { } altar)
            {
                try
                {
                    if (plan.Monsters.Count == 0 || entry.BasisLevel is not { } altarLevel ||
                        plan.SpecialObjects.Count != 0 || plan.HellWaves.Count != 0 || plan.Tournament is not null)
                        throw new InvalidDataException("祭坛计划包含其他演员合同");
                    var checkedPlan = DungeonMonsterPlan.FromAltarRoom(plan.Room, entry.CharacterId,
                        plan.Monsters[0].ObjectKey, altarLevel, _ => 0);
                    var expected = checkedPlan.Altar!;
                    if (altar.Definition.Ultimate != expected.Definition.Ultimate ||
                        !altar.Definition.Phases.SequenceEqual(expected.Definition.Phases) ||
                        !altar.Actors.Select(actor => actor with { Kind = 19 }).SequenceEqual(expected.Actors.Select(actor => actor with { Kind = 19 })) ||
                        altar.Actors.Any(actor => actor.Kind > 19) || altar.Actors.Count(actor => actor.Kind < 19) > (altar.Definition.Ultimate ? 0 : 1) ||
                        altar.Rounds.Length != expected.Rounds.Length || altar.Rounds.Zip(expected.Rounds).Any(pair =>
                            pair.First.Round != pair.Second.Round || pair.First.Difficulty != pair.Second.Difficulty ||
                            pair.First.Batches.Length != pair.Second.Batches.Length || pair.First.Batches.Zip(pair.Second.Batches).Any(batch =>
                                batch.First.DelayMilliseconds != batch.Second.DelayMilliseconds || batch.First.Interval != batch.Second.Interval ||
                                !batch.First.Actors.Select(actor => actor with { Kind = 19 }).SequenceEqual(batch.Second.Actors.Select(actor => actor with { Kind = 19 })))) ||
                        plan.Monsters.Count != altar.Actors.Length || plan.Monsters.Zip(altar.Actors).Any(pair =>
                            pair.First.AltarActor != pair.Second || pair.First.Source is not null || pair.First.AiCharacter is not null ||
                            pair.First.MonsterId != pair.Second.Phase.MonsterId || pair.First.ObjectKey != pair.Second.ObjectKey ||
                            pair.First.CandidateLevel != altarLevel || pair.First.SpawnType != pair.Second.Rank ||
                            !pair.First.IsBlocking || pair.First.IsFriendly || pair.First.Team != 100 || !keys.Add(pair.First.ObjectKey)))
                        throw new InvalidDataException("祭坛对象或时间线与资源不一致");
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or OverflowException)
                {
                    reason = "invalid: 祭坛房间规划不完整";
                    return false;
                }
                continue;
            }
            if (entry.PlayerTeam is { } playerTeam && plan.PlayerTeam is { } plannedTeam && plannedTeam != playerTeam)
            {
                reason = "invalid: 房间阵营与已确认入场上下文不匹配";
                return false;
            }
            // 不把未支持生成规则或缺失资源行替换成空房间。
            DungeonMonsterPlan resourcePlan;
            try
            {
                // 入场与复验采用同一训练策略；普通副本继续要求完整特殊父子演员。伴侣行按同一放行集合复验。
                resourcePlan = DungeonMonsterPlan.FromRoom(plan.Room, entry.CharacterId, 1,
                    entry.BasisLevel,
                    includeSpecial: !entry.IsTutorial, playerTeam: entry.PlayerTeam ?? plan.PlayerTeam,
                    // 与入场规划同一判据：只有起始房间带伴侣行；放行集合已在入场规划时排除训练/练习等类型。
                    companionRows: plan.Room.Map == entry.StartMap ? entry.CompanionMapRows : [], companionProfiles: entry.CompanionProfiles);
            }
            catch (Exception error) when (error is NotSupportedException or InvalidDataException or DungeonObjectKeyOverflowException)
            {
                reason = "unsupported: 房间怪物生成规则未闭合";
                return false;
            }
            // 实验性：深渊波次怪是入场时追加的真实怪（HellWave 非空），不在 FromRoom 的资源规划里；单独校验它们与 plan.HellWaves 一一对应、阻挡、key 唯一。
            var baseMonsters = plan.Monsters.Where(monster => monster.HellWave is null).ToArray();
            var waveMonsters = plan.Monsters.Where(monster => monster.HellWave is not null).ToArray();
            if (baseMonsters.Length != resourcePlan.Monsters.Count || waveMonsters.Length != plan.HellWaves.Count ||
                baseMonsters.Zip(resourcePlan.Monsters).Count(pair => pair.First.SpawnType != pair.Second.SpawnType) > 1 ||
                waveMonsters.Any(wave => !wave.IsBlocking || wave.ObjectKey == entry.CharacterId || !keys.Add(wave.ObjectKey) ||
                    !plan.HellWaves.Any(hell => hell.ObjectKey == wave.ObjectKey && hell.MonsterId == wave.MonsterId)) ||
                baseMonsters.Zip(resourcePlan.Monsters).Any(pair =>
                pair.First.SourceOrdinal != pair.Second.SourceOrdinal || pair.First.Source != pair.Second.Source ||
                pair.First.AiCharacter != pair.Second.AiCharacter || pair.First.MonsterId != pair.Second.MonsterId ||
                pair.First.CandidateLevel != pair.Second.CandidateLevel ||
                pair.First.SpawnType != pair.Second.SpawnType && !AllowedChampionPromotion(pair.First, pair.Second, entry) ||
                pair.First.IsDummyMapBoss != pair.Second.IsDummyMapBoss ||
                pair.First.IsBlocking != pair.Second.IsBlocking || pair.First.Team != pair.Second.Team ||
                pair.First.IsFriendly != pair.Second.IsFriendly || pair.First.ObjectKey == entry.CharacterId || !keys.Add(pair.First.ObjectKey)))
            {
                reason = "invalid: 房间怪物身份不完整或重复";
                return false;
            }
            if (plan.SpecialObjects.Count != resourcePlan.SpecialObjects.Count || plan.SpecialObjects.Any(actor => actor.ObjectKey == entry.CharacterId ||
                !keys.Add(actor.ObjectKey) || !resourcePlan.SpecialObjects.Any(expected => expected.Source == actor.Source)) ||
                plan.SpecialObjects.Select(actor => actor.Source.SourceOrdinal).Distinct().Count() != plan.SpecialObjects.Count)
            {
                reason = "invalid: 特殊对象身份不完整或重复";
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
        // 遗忘之地有独立传送房；只接受原静态普通连接，不补边以伪造全图可达。
        bool lostLandLinksMatch = entry.LostLand is not null && entry.Maze.StaticTopology.Links is { } links &&
            links.Count == approvedRoute.Connections.Count && links.All(link => edges.Contains((link.First, link.Second)));
        // 能源图可仅有客户端原生装置；精确登记的物件本身是演员来源，不能伪造一只普通怪绕过空图保护。
        bool energyActor = entry.RaidEntry is { Kind: DfoServer.Game.Raids.RaidKind.Luke, Phase: 1 } &&
            entry.LukeEnergyObject is { } energy && approvedRoute.PassiveClearTarget == energy.ObjectId &&
            approvedRoute.Rooms.Any(room => energy.MatchesEnergy(entry.DungeonId, entry.Maze, room.Room.Map,
                energy.ObjectId, energy.Ordinal) && energy.Ordinal < room.Room.PassiveObjects.Count &&
                room.Room.PassiveObjects[energy.Ordinal].ObjectId == energy.ObjectId &&
                room.Room.PassiveObjects.Count(source => source.ObjectId == energy.ObjectId) == 1);
        if (entry.LostLand is not null ? !lostLandLinksMatch || keys.Count == 0 : reachable.Count != points.Count ||
            keys.Count == 0 && !entry.IsTrainingRoom && !energyActor)
        {
            reason = "invalid: 路线不可达或无可生成演员";
            return false;
        }
        run = new(entry, taskAttempt, initialEpoch, approvedRoute, combatAuthority, time, battleLevels, sharedBattleLevels,
            monsterAi, sharedMonsterAi);
        reason = "domain-run-created";
        return true;
    }

    // 入场已按资源[champion]抽出精英；复验不能把这一步合法的0→1变化当成伪造演员。
    static bool AllowedChampionPromotion(DungeonPlannedMonster planned, DungeonPlannedMonster resource, DungeonEntryPlan entry) =>
        DungeonMonsterPlan.CanBecomeChampion(resource) && !entry.NamedMonsters.Contains(resource.MonsterId) &&
        planned.SpawnType == 1 && entry.ChampionPercents.Percent(entry.Difficulty) > 0;

    // 等级只在房间首次安装时冻结；AI参数每次安装都重新解析，回访不沿用上一次访问的配置。
    // 组队各角色共享同一个冻结控制器，同房安装仍取同一份快照。
    void FreezeCurrentRoom()
    {
        current.MonsterAi = MonsterAiRooms.Freeze(entry.DungeonId, current.Plan.Room.Map);
        if (current.BattleLevelsFrozen) return;
        current.Plan = DungeonBattleLevels.Freeze(current.Plan, entry.HelperRotation is { } rotation
            ? rotation.BattleRule : BattleLevels.Freeze(entry.DungeonId, current.Plan.Room.Map));
        if (current.Plan.Altar is { } altar) current.Altar = new(altar);
        current.BattleLevelsFrozen = true;
    }

    // 本次房间访问冻结的AI参数；null 表示冻结时功能关闭，不发送私有包。
    internal MonsterAiProfile? CurrentMonsterAi => current.MonsterAi;

    public DungeonRunResult CompleteLoading(DungeonRunIdentity expected, Guid actionId, bool hasMatchingQuest = false)
    {
        if (Replay(expected, actionId, "loaded", null) is { } replay)
            return replay;
        if (HasPendingDeath || !Matches(expected) || Phase != SinglePlayerDungeonPhase.Loading || actionId == Guid.Empty || !CanAdvance())
            return Result(DungeonRunOutcome.Rejected);
        int slot = InitializationCounterSupported ? checked(entry.Maze.Width * current.Plan.Room.Map.Position.Y + current.Plan.Room.Map.Position.X) : 0;
        bool first = LastLoadingFact is null;
        bool initialized = InitializationCounterSupported && (!entry.IndividualMapMovement || !initializedMapSlots.Contains(slot));
        uint count = checked(ConfirmedInitializationCount + (!first && initialized && hasMatchingQuest ? 1u : 0u));
        // 逐房费用按实例/MapID去重；入场一次性疲劳只在首次加载计入N，不随dummy阶段刷新再计。
        if (entry.UsesFatigue)
        {
            if (HopeCatalog is { } hope && HopeAreaId is int area)
            {
                if (!HasLoadedHopeArea(area)) consumedFatigue = checked(consumedFatigue + (uint)hope.FatigueValue);
            }
            else if (entry.StartFatigue > 0 ? first : !allRooms.Any(room => room.Loaded && room.Plan.Room.Map.Id == current.Plan.Room.Map.Id))
                consumedFatigue = checked(consumedFatigue + (entry.StartFatigue == 0 ? 1u : entry.StartFatigue));
        }
        acceptedActions.Add(actionId, new(expected, "loaded", null));
        current.Loaded = true;
        // NOTI28建立新Run并清统计；接取不重置。缓存安装与活跃任务计数分开，完成仍由真实清图判定。
        if (InitializationCounterSupported)
            initializedMapSlots.Add(slot);
        ConfirmedInitializationCount = count;
        if (first) QuestFacts.Start();
        LastLoadingFact = new(expected, actionId, entry.DungeonId, current.Plan.Room.Map, entry.Maze.SourceOrdinal,
            first, initialized, count, MazeInitializationThreshold) { InitializationCounterSupported = InitializationCounterSupported };
        Phase = SinglePlayerDungeonPhase.Dungeon;
        // 服务端普通首期策略：首次真实加载后计时，过房不重置，终点结算冻结成功。
        if (entry.Strongway2019TimeLimitSeconds is not null)
            strongway2019Started ??= strongway2019Clock.GetTimestamp();
        Advance();
        return SettleIfCleared();
    }

    public DungeonRunResult Move(DungeonRunIdentity expected, Guid actionId, DungeonPoint target)
    {
        if (scriptedReturn is not null) return Result(DungeonRunOutcome.Rejected);
        if (Replay(expected, actionId, "move", target) is { } replay)
            return replay;
        if (actionId == Guid.Empty || !CanMove(expected, target))
            return Result(DungeonRunOutcome.Rejected);
        var next = rooms[target];
        acceptedActions.Add(actionId, new(expected, "move", target));
        bool replaceVisited = false;
        if (activeDummyStage < dummyRooms.Length && dummyRooms[activeDummyStage].Plan.Room.Map.Position == target)
        {
            // 只有下一阶段会显式替换缓存；普通回访保持已清房间，不重生假Boss或物件。
            replaceVisited = next.Loaded;
            next = dummyRooms[activeDummyStage];
            rooms[target] = next;
        }
        if (storyRooms.Length > 0 && (storyStageIndex < 0 || current.Plan.Room.Map == storyRooms[storyStageIndex].Plan.Room.Map) &&
            storyStageIndex + 1 < storyRooms.Length && storyRooms[storyStageIndex+1].Plan.Room.Map.Position == target)
        {
            next = storyRooms[++storyStageIndex];
            // 同坐标旧地图已装载时显式刷新，不能叠加两套门及场景缓存。
            replaceVisited = allRooms.Any(room => room.Loaded && room.Plan.Room.Map.Position == target && room != next);
            rooms[target] = next;
        }
        if (BossEntranceOpen && next.Plan.Room.Map.DummyStageIndex < 0 && next.Plan.Room.Map.StoryStageIndex < 0 && current.Plan.Room.Map.Kind != "layered" &&
            conditionalRooms.TryGetValue(target,out var replacement))
            next = replacement;
        current = next;
        FreezeCurrentRoom();
        mapOperation = DungeonMapOperation.Current;
        mapPayload = replaceVisited ? DungeonMapPayload.Refresh : current.Loaded ? DungeonMapPayload.Cached : DungeonMapPayload.Build;
        pendingLayerBaseAck = null;
        Phase = SinglePlayerDungeonPhase.Loading;
        Advance();
        return Result(DungeonRunOutcome.Accepted);
    }

    // 轮换入场先准备完整新实例；检查真实门时不能提前移动或改变旧房间身份。
    internal bool CanMove(DungeonRunIdentity expected, DungeonPoint target) =>
        scriptedReturn is null && !HasPendingDeath && Matches(expected) &&
        Phase == SinglePlayerDungeonPhase.Dungeon && CanAdvance() &&
        current.Elevator is not { Completed: false } && (entry.LostLand is not null || current.Cleared) &&
        !HasPendingLayer() && CanMoveHopeOrOrdinary(target) && rooms.ContainsKey(target);

    // 亡命杀镇普通迷宫的出口直接上报kind0有向目标；不能借此放行条件Boss替换或任务骑乘。
    private bool CanBorderTownWarp(DungeonPoint target) => entry.DungeonId == 291100268 && entry.IsOrdinary &&
        entry.Maze.QuestConnectionTokens.Count == 0 && entry.Maze.BossEntrance is null &&
        entry.Maze.ConditionalMaps.Count == 0 && entry.Maze.LayeredMaps.Count == 0 &&
        entry.Maze.StoryStages.Count == 0 && entry.Maze.DummyStages.Count == 0 &&
        entry.Maze.WarpMaps.Any(warp => warp.Source == current.Plan.Room.Map.Position && warp.Destination == target);

    public DungeonRunResult MoveLayer(DungeonRunIdentity expected, Guid actionId, DungeonPoint target, bool nextLayer,
        bool allowSettlementEntry = false)
    {
        if (scriptedReturn is not null) return Result(DungeonRunOutcome.Rejected);
        string kind = nextLayer ? "layer" : "base";
        if (Replay(expected,actionId,kind,target) is { } replay) return replay;
        bool settlementLayer = allowSettlementEntry && nextLayer && Phase == SinglePlayerDungeonPhase.Settlement && ClearFact is not null;
        if (!Matches(expected) || actionId == Guid.Empty || !CanAdvance() ||
            HasPendingDeath || pendingSettlement is not null || current.Elevator is { Completed: false } || current.Plan.Room.Map.Position != target ||
            Phase != SinglePlayerDungeonPhase.Dungeon && !settlementLayer)
            return Result(DungeonRunOutcome.Rejected);
        Room next;
        var layers = entry.Maze.LayeredMaps.Where(map => map.Position == target).ToArray();
        int at = current.Plan.Room.Map.Kind == "layered" ? Array.IndexOf(layers,current.Plan.Room.Map)+1 : 0;
        // 原生kind1同时表示下一层和末层返回；不能把末层结束当作越界请求。
        if (nextLayer && at < layers.Length)
        {
            if ((at > 0 && !current.Cleared && !CanAdvanceFromCinematicLayer()) || enteredLayers.Contains(layers[at]))
                return Result(DungeonRunOutcome.Rejected);
            if (at == 0) layerSources[target] = current;
            enteredLayers.Add(layers[at]); next = layeredRooms[layers[at]];
            mapOperation = DungeonMapOperation.AdvanceLayer;
            mapPayload = at == 0 ? DungeonMapPayload.Build : DungeonMapPayload.Refresh;
        }
        else
        {
            if (current.Plan.Room.Map.Kind != "layered" || HasPendingLayer() ||
                !current.Cleared && !(nextLayer && CanReturnFromCinematic(target)) &&
                !(!nextLayer && CanReturnFromScriptedTransit(target))) return Result(DungeonRunOutcome.Rejected);
            next = layerSources[target];
            mapOperation = DungeonMapOperation.RestoreBase;
            mapPayload = DungeonMapPayload.Cached;
            pendingLayerBaseAck = target;
        }
        acceptedActions.Add(actionId,new(expected,kind,target)); current = next;
        FreezeCurrentRoom();
        Phase = SinglePlayerDungeonPhase.Loading; Advance(); return Result(DungeonRunOutcome.Accepted);
    }

    internal bool ConfirmLayerBase(DungeonRunIdentity expected, DungeonPoint target)
    {
        if (!Matches(expected) || Phase is not (SinglePlayerDungeonPhase.Loading or SinglePlayerDungeonPhase.Dungeon) ||
            HasPendingDeath || pendingSettlement is not null || pendingLayerBaseAck != target ||
            current.Plan.Room.Map.Position != target || current.Plan.Room.Map.Kind == "layered") return false;
        pendingLayerBaseAck = null;
        return true;
    }

    private bool CanAdvanceFromCinematicLayer()
    {
        // 只把已核对的非战斗 CHANGE MAP 叠层当成换下层，不清当前层阻挡；返回基础格仍走 CanReturnFromCinematic。
        if (entry.IsTutorial || entry.IsClassExperience || entry.IsQuestReplay) return false;
        return current.Plan.Room.CinematicLayerReturn is { CinematicId: > 0, ActorId: null } movie &&
            movie.MapId == current.Plan.Room.Map.Id && movie.MapPath == current.Plan.Room.Map.Path &&
            movie.ActionPath.Length > 0 && movie.CinematicPath.Length > 0;
    }

    private bool CanReturnFromCinematic(DungeonPoint target)
    {
        var entry = this.entry.PartyRoute ?? this.entry;
        if (entry.IsTutorial || entry.IsClassExperience || entry.RouteQuestId is 0 or >= 30000 ||
            entry.BossMap.Kind == "layered" || entry.BaseBossMap != entry.BossMap ||
            !layerSources.TryGetValue(target,out var source) || source.Plan.Room.Map != entry.BossMap ||
            current.Plan.Room.CinematicLayerReturn is not { CinematicId: > 0, IsOnStartMap: false } movie || movie.MapId != current.Plan.Room.Map.Id ||
            movie.MapPath != current.Plan.Room.Map.Path || movie.ActionPath.Length == 0 || movie.CinematicPath.Length == 0) return false;
        // 演出任务不是clear-map任务；APC来源改由原迷宫任务连接限定，不能伪造HasExplicitClearMap。
        if (movie.ActorId is { } actor)
        {
            if (actor <= 0 || current.Plan.Room.AiCharacters.Count(row => row.Id == actor) != 1 ||
                !entry.Maze.ConnectsQuest(entry.RouteQuestId,entry.Difficulty)) return false;
        }
        else if (!entry.HasExplicitClearMap) return false;
        // 回放使用正式入场已冻结的原路线，不伪造接取；只复用绑定APC的演出返回，原非战斗资格保持不变。
        if (entry.IsQuestReplay) return movie.ActorId is > 0 && entry.QuestId == 0 && entry.ActiveQuestSlot == byte.MaxValue &&
            entry.QuestAcceptanceId == Guid.Empty;
        var acceptances = entry.QuestAcceptances.Where(row => row.QuestId == entry.RouteQuestId).ToArray();
        return acceptances is [{ Slot: <= 29 } accepted] && accepted.AcceptanceId != Guid.Empty &&
            entry.QuestAcceptances.Count(row => row.Slot == accepted.Slot) == 1 &&
            entry.QuestAcceptances.Count(row => row.AcceptanceId == accepted.AcceptanceId) == 1 &&
            (entry.QuestId == 0 || entry.QuestAcceptances.Any(row => row.QuestId == entry.QuestId &&
                row.Slot == entry.ActiveQuestSlot && row.AcceptanceId == entry.QuestAcceptanceId && row.AcceptanceId != Guid.Empty));
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
        if (HasPendingDeath || pendingSettlement is not null || !Matches(expected) || Phase == SinglePlayerDungeonPhase.Returned || actionId == Guid.Empty || identity.Epoch == long.MaxValue)
            return Result(DungeonRunOutcome.Rejected);
        acceptedActions.Add(actionId, new(expected, "return", null));
        CancelTagEvildom();
        Phase = SinglePlayerDungeonPhase.Returned;
        Advance();
        return Result(DungeonRunOutcome.Accepted);
    }

    public DungeonRunResult AcceptConfirmedDeath(ConfirmedDungeonMonsterDeath confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        // 旧的纯内存入口不能抢先确认正在等待数据库结果的候选，既有死亡重放仍可识别。
        bool awaitingCommit = HasPendingDeath;
        DungeonDeathPreparation preparation = PrepareConfirmedDeath(confirmed);
        if (preparation.Outcome != DungeonRunOutcome.Accepted)
            return Result(preparation.Outcome);
        return awaitingCommit ? Result(DungeonRunOutcome.Rejected) : ConfirmCommittedDeath(preparation.Pending!);
    }

    // 在原实例owner内准备；调用方随后同事务保存事件身份与奖励，提交后才确认内存。
    public DungeonDeathPreparation PrepareConfirmedDeath(ConfirmedDungeonMonsterDeath confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        if (!ReferenceEquals(confirmed.Authority, authority) || pendingPassiveClear is not null)
            return new(DungeonRunOutcome.Rejected);
        if (deaths.TryGetValue(confirmed.EventId, out ConfirmedDungeonMonsterDeath? old))
            return new(SameDeath(old, confirmed)
                ? DungeonRunOutcome.Duplicate : DungeonRunOutcome.Rejected);
        if (pendingDeath is not null)
            return SameDeath(pendingDeath.Death, confirmed)
                ? new(DungeonRunOutcome.Accepted, pendingDeath) : new(DungeonRunOutcome.Rejected);
        if (!Matches(confirmed.Identity) || Phase is not (SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement) ||
            HasPendingSettlement || committedAltarSettlement is not null || committedDespairClear is not null || confirmed.EventId == Guid.Empty ||
            !CanAdvance() || confirmed.CombatSequence <= 0 || combatSequences.Contains(confirmed.CombatSequence) ||
            CurrentLivingMonster(confirmed.MonsterKey) is null)
            return new(DungeonRunOutcome.Rejected);
        var target = current.Plan.Monsters.Single(monster => monster.ObjectKey == confirmed.MonsterKey);
        bool conditionClears = !entry.IsClassExperience && Phase == SinglePlayerDungeonPhase.Dungeon && MatchesClearCondition(target) &&
            !HasPendingBossDeaths(confirmed.MonsterKey);
        // TAG终末尚无自己的到账合同；真实怪物死亡可提交，但不能产生普通翻牌/任务通关许可。
        bool settles = tagEvildom is null && !entry.IsClassExperience && Phase == SinglePlayerDungeonPhase.Dungeon && (TerminalBossCleared(confirmed.MonsterKey) ||
            conditionClears && IsTerminalRoom());
        bool tagSettles = TagEvildomAdmission is not null && Phase == SinglePlayerDungeonPhase.Dungeon &&
            (TerminalBossCleared(confirmed.MonsterKey) || conditionClears && IsTerminalRoom());
        int? imperialRound = null;
        if (ImperialArenaAdmission is { } imperial && Phase == SinglePlayerDungeonPhase.Dungeon)
        {
            int x = current.Plan.Room.Map.Position.X;
            var opponents = imperial.Route.Opponents(imperial.Mode);
            if (x > 0 && x <= opponents.Length && opponents[x - 1].MapId == current.Plan.Room.Map.Id &&
                opponents[x - 1].MonsterId == target.MonsterId) imperialRound = x;
            tagSettles |= imperialRound == opponents.Length;
        }
        // 明确的猎杀条件同样清退当前房间；非终点不产生通关事实，清退不产生击杀奖励。
        ushort[] culled = current.Altar is null && (settles || tagSettles || conditionClears) ? current.Plan.Monsters.Where(monster => monster.IsBlocking &&
            (conditionClears || !monster.IsBoss) &&
            monster.ObjectKey != confirmed.MonsterKey && !current.Dead.Contains(monster.ObjectKey)).Select(monster => monster.ObjectKey).ToArray() : [];
        // 中途任务目标保留原Boss与结算；仅最后可信阻挡死亡推进清房epoch。
        bool clearsQuestRoom = tagEvildom is null && !settles && !entry.IsTutorial && !entry.IsClassExperience && !entry.IsQuestReplay &&
            Phase == SinglePlayerDungeonPhase.Dungeon && entry.QuestClearMap == current.Plan.Room.Map && target.IsBlocking &&
            current.Plan.Monsters.All(monster => !monster.IsBlocking || monster.ObjectKey == confirmed.MonsterKey ||
                current.Dead.Contains(monster.ObjectKey) || current.Culled.Contains(monster.ObjectKey));
        bool advancesDummy = target.IsDummyMapBoss && current.Plan.Room.Map.DummyStageIndex == activeDummyStage &&
            activeDummyStage + 1 < dummyRooms.Length;
        int nextStage = activeDummyStage + (advancesDummy ? 1 : 0);
        bool advancesStoryBoss = storyDummyBosses && target.IsDummyMapBoss && current.Plan.Room.Map.StoryStageIndex == storyStageIndex &&
            storyStageIndex >= 0 && storyStageIndex + 1 < storyRooms.Length;
        DungeonPoint nextBossPosition = advancesStoryBoss ? storyRooms[storyStageIndex + 1].Plan.Room.Map.Position : BossPosition(nextStage);
        DungeonPoint? bossUpdate = target.IsDummyMapBoss ? nextBossPosition : null;
        DungeonRunIdentity nextIdentity = settles || tagSettles || clearsQuestRoom || advancesDummy || advancesStoryBoss ? identity with { Epoch = checked(identity.Epoch + 1) } : identity;
        var nextSnapshot = new DungeonRunSnapshot(nextIdentity,
            settles || tagSettles ? SinglePlayerDungeonPhase.Settlement : Phase, current.Plan.Room.Map,
            Array.AsReadOnly(current.Plan.Monsters.Where(monster => !current.Dead.Contains(monster.ObjectKey) &&
                !current.Culled.Contains(monster.ObjectKey) && !culled.Contains(monster.ObjectKey) &&
                monster.ObjectKey != confirmed.MonsterKey && (current.Altar is null || current.Altar.WasPublished(monster.ObjectKey)))
                .Select(monster => monster.ObjectKey).ToArray()))
            { MapOperation = mapOperation,MapPayload = mapPayload, ConsumedFatigue = consumedFatigue, BossPosition = nextBossPosition };
        DungeonClearFact? clear = settles
            ? new(nextIdentity, entry.DungeonId, Array.AsReadOnly(deaths.Keys.Append(confirmed.EventId).ToArray())) { ClearMap = current.Plan.Room.Map } : null;
        if (clear is not null) QuestFacts.FreezeClear();
        pendingDeath = new(confirmed, target,
            new(DungeonRunOutcome.Accepted, nextSnapshot, clear)
            {
                CulledMonsterKeys = Array.AsReadOnly(culled),
                BossPositionUpdate = bossUpdate,
                RoomClearFact = clearsQuestRoom ? new(nextIdentity,entry.DungeonId,current.Plan.Room.Map,
                    Array.AsReadOnly(deaths.Keys.Append(confirmed.EventId).ToArray())) : null
            }, nextStage)
            {
                TagEvildomClear = tagSettles && TagEvildomAdmission is not null ? new(nextIdentity, confirmed.EventId, TagEvildomAdmission) : null,
                ImperialArenaRound = imperialRound
            };
        return new(DungeonRunOutcome.Accepted, pendingDeath);
    }

    // 只接受本实例当前准备对象，不能用另一实例或重新构造的候选确认提交。
    public bool IsPendingDeath(PreparedDungeonDeath prepared) => prepared is not null && ReferenceEquals(pendingDeath, prepared);
    internal bool IsConfirmedDeath(PreparedDungeonDeath prepared) => prepared is not null && committedPreparations.Contains(prepared) &&
        deaths.TryGetValue(prepared.Death.EventId, out var accepted) && ReferenceEquals(accepted, prepared.Death);

    // 只查询本Run实际提交的普通演员；合并监控目标须所有已加载实体都死亡，未生成的分支不计存活。
    internal bool CommittedMonitorDeath(ushort key, out int template, out bool exhausted)
    {
        template = 0; exhausted = false;
        var source = committedPreparations.FirstOrDefault(prepared => prepared.Death.MonsterKey == key && IsConfirmedDeath(prepared) &&
            prepared.Monster.AiCharacter is null && !prepared.Monster.IsSpecialChild && prepared.Monster.CanReportDeath);
        if (source is null) return false;
        template = source.Monster.MonsterId;
        int target = template;
        var spawned = allRooms.Where(room => room.Loaded).SelectMany(room => room.Plan.Monsters)
            .Where(monster => monster.MonsterId == target && monster.AiCharacter is null && !monster.IsSpecialChild && monster.CanReportDeath).ToArray();
        exhausted = spawned.Length != 0 && spawned.All(monster => committedPreparations.Any(prepared =>
            prepared.Death.MonsterKey == monster.ObjectKey && prepared.Monster.MonsterId == target && IsConfirmedDeath(prepared)));
        return true;
    }

    internal bool HasLivingMonitorTarget(int template) => allRooms.Where(room => room.Loaded)
        .SelectMany(room => room.Plan.Monsters).Any(monster => monster.MonsterId == template && monster.AiCharacter is null &&
            !monster.IsSpecialChild && monster.CanReportDeath && !committedPreparations.Any(prepared =>
                prepared.Death.MonsterKey == monster.ObjectKey && prepared.Monster.MonsterId == template && IsConfirmedDeath(prepared)));

    // 过房或结算后仍核对本Run实际确认的具体事件，不以较早epoch替代事实资格。
    internal bool HasCommittedRoomClear(DfoServer.Game.Quests.QuestClearReceipt receipt) =>
        receipt.SourceType == DfoServer.Game.Quests.QuestClearSourceType.MonsterDeath &&
        entry.QuestAcceptances.Any(active => active.AcceptanceId == receipt.AcceptanceId &&
            active.QuestId == receipt.QuestId && active.Slot == receipt.QuestSlot) &&
        committedPreparations.Any(prepared => prepared.Death.EventId == receipt.EventId && IsConfirmedDeath(prepared) &&
            prepared.Result.RoomClearFact is { } clear && clear.Identity.InstanceId == receipt.InstanceId &&
            clear.Identity.TaskAttempt == receipt.TaskAttempt && clear.Identity.Epoch == receipt.Epoch &&
            clear.DungeonId == receipt.DungeonId && clear.Map.Id == receipt.MapId);

    public DungeonRunResult ConfirmCommittedDeath(PreparedDungeonDeath prepared)
    {
        if (prepared.TagEvildomClear is not null || prepared.ImperialArenaRound is not null) return Result(DungeonRunOutcome.Rejected);
        return ConfirmDeath(prepared);
    }

    internal DungeonRunResult ConfirmCommittedTagEvildomClear(DfoServer.Game.TagEvildom.TagEvildomClearCommit committed)
    {
        if (!ReferenceEquals(committed.Plan.Run, this) || committed.MailId == 0 ||
            committed.Plan.Death.TagEvildomClear is null) return Result(DungeonRunOutcome.Rejected);
        return ConfirmDeath(committed.Plan.Death);
    }

    internal DungeonRunResult ConfirmCommittedImperialArenaDeath(DfoServer.Game.ImperialArena.ImperialArenaDeathCommit committed)
    {
        if (!ReferenceEquals(committed.Plan.Run, this) || committed.Plan.Death.ImperialArenaRound is null)
            return Result(DungeonRunOutcome.Rejected);
        return ConfirmDeath(committed.Plan.Death);
    }

    DungeonRunResult ConfirmDeath(PreparedDungeonDeath prepared)
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
        activeDummyStage = prepared.DummyStageIndex;
        storyBossPosition = prepared.Result.Snapshot.BossPosition;
        identity = prepared.Result.Snapshot.Identity;
        Phase = prepared.Result.Snapshot.Phase;
        if (prepared.Result.ClearFact is { } clear)
            ClearFact = clear;
        pendingDeath = null;
        RecordHopeTarget(prepared);
        return prepared.Result;
    }

    // 仅在存储明确未提交或已回滚时撤销准备；结果不明时保留候选并查询持久receipt。
    public bool CancelUncommittedDeath(PreparedDungeonDeath prepared)
    {
        if (prepared is null || !ReferenceEquals(pendingDeath, prepared))
            return false;
        if (prepared.Result.ClearFact is not null) QuestFacts.CancelClear();
        pendingDeath = null;
        return true;
    }

    public DungeonSettlementPreparation PrepareSettlement(DungeonClearFact fact, ReadOnlySpan<byte> reportBody,
        DungeonClearExperienceReward reward, DungeonCardReward? card = null, DungeonCardOptions? options = null,
        DfoServer.Game.Inventory.Models.InventoryPlan? creatureInventory = null, GriefTowerClearReward? grief = null,
        BossRushClearReward? bossRush = null)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(reward);
        if (entry.LostLand is not null || tagEvildom is not null || entry.IsPractice || entry.DungeonId is >= 11008 and <= 11107 || !ReferenceEquals(ClearFact, fact) || fact.Identity != identity || Phase != SinglePlayerDungeonPhase.Settlement ||
            HasPendingDeath || reportBody.IsEmpty || reportBody.Length > 4096 || reward.TotalExperience == 0 && !entry.IsTutorial && !entry.DisableMonsterAndClearExperience ||
            entry.DisableMonsterAndClearExperience && reward.TotalExperience != 0 ||
            (ulong)reward.BaseExperience + reward.RankExperience + reward.ExtraExperience != reward.TotalExperience || reward.RankBonusIndex is < -1 or > 4 ||
            string.IsNullOrWhiteSpace(reward.RuleId) || reward.RuleId.Length > 128 ||
            string.IsNullOrWhiteSpace(reward.ResourceSource) || reward.ResourceSource.Length > 1024)
            return new(DungeonRunOutcome.Rejected);
        if ((pendingSettlement ?? committedSettlement) is { } frozen)
            return frozen.ReportBody.SequenceEqual(reportBody) && frozen.Reward == reward && frozen.Card == card && frozen.Options == options &&
                ReferenceEquals(frozen.Grief,grief) && ReferenceEquals(frozen.BossRush,bossRush)
                ? new(committedSettlement is null ? DungeonRunOutcome.Accepted : DungeonRunOutcome.Duplicate, frozen)
                : new(DungeonRunOutcome.Rejected);
        card?.Validate();
        if (entry.Maze.IsGriefTower != (grief is not null) || grief is not null &&
            (grief.Before.InstanceId != identity.InstanceId || grief.Before.TaskAttempt != identity.TaskAttempt ||
                grief.Floor.DungeonId != entry.DungeonId || grief.Floor.Layer != grief.Before.State.Layer || entry.GriefLayer != grief.Floor.Layer ||
                card is not null || options is not null || bossRush is not null))
            return new(DungeonRunOutcome.Rejected);
        if (entry.IsBossRush != (bossRush is not null) || bossRush is not null &&
            (card is not null || options is not null || grief is not null))
            return new(DungeonRunOutcome.Rejected);
        pendingSettlement = new(fact, reportBody, reward, current.Plan.Room.Map.Id, card, options, creatureInventory, grief, bossRush);
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

    internal bool IsPassiveClearCondition(DungeonRunIdentity expected, int templateId, int sourceOrdinal) =>
        tagEvildom is null && Matches(expected) && Phase == SinglePlayerDungeonPhase.Dungeon && !entry.IsClassExperience &&
        pendingDeath is null && pendingPassiveClear is null && pendingSettlement is null && CanAdvance() && !HasCommittedPassiveClearCondition &&
        passiveClearTarget == templateId && sourceOrdinal >= 0 && sourceOrdinal < current.Plan.Room.PassiveObjects.Count &&
        current.Plan.Room.PassiveObjects[sourceOrdinal].ObjectId == templateId &&
        current.Plan.Room.PassiveObjects.Count(source => source.ObjectId == templateId) == 1;

    // 冻结真实物件终末及本实例已提交死亡；物件副本允许精确为空的死亡集合。
    internal bool CanPreparePassiveClear(DungeonRunIdentity expected, int templateId, int sourceOrdinal) =>
        IsPassiveClearCondition(expected,templateId,sourceOrdinal) && TerminalBossCleared(passiveConfirmed:true);

    internal PreparedDungeonPassiveClear? PreparePassiveClear(DungeonRunIdentity expected, Guid operationId, int templateId, int sourceOrdinal)
    {
        if (pendingPassiveClear is { } pending)
            return pending.Source == new DungeonPassiveClearSource(operationId, expected, templateId, sourceOrdinal) ? pending : null;
        if (operationId == Guid.Empty || !CanPreparePassiveClear(expected,templateId,sourceOrdinal)) return null;
        var source = new DungeonPassiveClearSource(operationId, expected, templateId, sourceOrdinal);
        var next = Snapshot with { Identity = identity with { Epoch = checked(identity.Epoch + 1) }, Phase = SinglePlayerDungeonPhase.Settlement };
        var fact = new DungeonClearFact(next.Identity, entry.DungeonId, Array.AsReadOnly(deaths.Keys.ToArray()))
            { ClearMap = current.Plan.Room.Map, PassiveSource = source };
        pendingPassiveClear = new(source, new(DungeonRunOutcome.Accepted, next, fact));
        return pendingPassiveClear;
    }

    internal bool IsPendingPassiveClear(PreparedDungeonPassiveClear prepared) => ReferenceEquals(pendingPassiveClear, prepared) &&
        prepared.Source.Identity == identity && Phase == SinglePlayerDungeonPhase.Dungeon;
    internal bool IsCommittedPassiveClear(PreparedDungeonPassiveClear prepared) => ReferenceEquals(committedPassiveClear, prepared) &&
        prepared.Result.ClearFact == ClearFact && prepared.Result.Snapshot.Identity == identity && Phase == SinglePlayerDungeonPhase.Settlement;
    internal void CancelUncommittedPassiveClear(PreparedDungeonPassiveClear prepared)
    {
        if (ReferenceEquals(pendingPassiveClear,prepared)) pendingPassiveClear=null;
    }

    internal DungeonRunResult ConfirmCommittedPassiveClear(PreparedDungeonPassiveClear prepared)
    {
        if (ReferenceEquals(committedPassiveClear, prepared)) return Result(DungeonRunOutcome.Duplicate);
        if (!IsPendingPassiveClear(prepared)) return Result(DungeonRunOutcome.Rejected);
        var source = prepared.Source;
        acceptedActions.Add(source.OperationId, new(source.Identity, $"passive:{source.TemplateId}:{source.SourceOrdinal}", null));
        HasCommittedPassiveClearCondition = true;
        identity = prepared.Result.Snapshot.Identity;
        Phase = prepared.Result.Snapshot.Phase;
        ClearFact = prepared.Result.ClearFact;
        committedPassiveClear = prepared;
        pendingPassiveClear = null;
        return prepared.Result;
    }

    // 由原 Session 在场景事件持久提交后确认；物件不是战斗死亡，不加入死亡集合。
    internal DungeonRunResult ConfirmCommittedPassiveObject(DungeonRunIdentity expected, Guid operationId, int templateId, int sourceOrdinal)
    {
        string kind = $"passive:{templateId}:{sourceOrdinal}";
        if (Replay(expected, operationId, kind, null) is { } replay) return replay;
        if (operationId == Guid.Empty || !IsPassiveClearCondition(expected, templateId, sourceOrdinal))
            return Result(DungeonRunOutcome.Rejected);
        acceptedActions.Add(operationId, new(expected, kind, null));
        HasCommittedPassiveClearCondition = true;
        return SettleIfCleared();
    }

    private DungeonRunResult SettleIfCleared()
    {
        if (tagEvildom is null && !entry.IsClassExperience && TerminalBossCleared() && deaths.Count > 0)
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

    private Room? ResolveLayeredBossTerminal()
    {
        // 未明确声明终末的 dummy Boss 房只作为叠层入口；中途目标不改变同坐标末层的真实Boss终点。
        if ((entry.HasExplicitClearMap && entry.QuestClearMap is null) || entry.BossMap != entry.BaseBossMap || storyRooms.Length != 0 ||
            conditionalRooms.ContainsKey(entry.BaseBossMap.Position)) return null;
        var source = rooms[entry.BaseBossMap.Position].Plan.Monsters.Where(monster => monster.IsBlocking).ToArray();
        if (source.Length == 0 || source.Any(monster => !monster.IsScriptedBoss)) return null;
        var last = entry.Maze.LayeredMaps.LastOrDefault(map => map.Position == entry.BaseBossMap.Position);
        if (last is null) return null;
        var terminal = layeredRooms[last];
        var bosses = terminal.Plan.Monsters.Where(monster => monster.IsBoss).ToArray();
        if (bosses.Length == 0 || bosses.Any(monster => monster.IsScriptedBoss || monster.AiCharacter is not null) ||
            !source.Select(monster => monster.MonsterId).ToHashSet().SetEquals(bosses.Select(monster => monster.MonsterId))) return null;
        return terminal;
    }

    private Room? ResolveCinematicLayerTerminal()
    {
        var entry = this.entry.PartyRoute ?? this.entry;
        if (entry.IsTutorial || entry.IsClassExperience || entry.IsPractice || entry.IsQuestReplay ||
            entry.HasExplicitClearMap || entry.QuestClearMap is not null || entry.TerminalLayerMap is not null ||
            entry.BossMap != entry.BaseBossMap || storyRooms.Length != 0 || conditionalRooms.Count != 0 ||
            entry.Maze.DummyStages.Count != 0 || entry.Maze.ClearConditions.Count != 0 || entry.RouteQuestId is 0 or >= 30000 ||
            !entry.Maze.ConnectsQuest(entry.RouteQuestId,entry.Difficulty) ||
            !entry.QuestAcceptances.Any(active => active.QuestId == entry.RouteQuestId && active.AcceptanceId != Guid.Empty)) return null;
        var basis = rooms[entry.BaseBossMap.Position];
        // 全非战斗基础图的ON START MAP演出明确CHANGE MAP，且只有一个同坐标Boss层。
        if (basis.Plan.Monsters.Any(monster => monster.IsBlocking) ||
            entry.QuestCinematicMapId != basis.Plan.Room.Map.Id || !entry.QuestCinematicChangesLayer ||
            entry.Maze.LayeredMaps.Where(map => map.Position == entry.BaseBossMap.Position).ToArray() is not [var layer] ||
            !layeredRooms.TryGetValue(layer,out var terminal) || terminal.Plan.Room.DeclaredType != "[boss]" ||
            terminal.Plan.Monsters.Where(monster => monster.IsBlocking).ToArray() is not
                [{ IsBoss: true, IsScriptedBoss: true, CanReportDeath: true, IsFriendly: false, AiCharacter: null }]) return null;
        return terminal;
    }

    private Room? ResolvePassiveLayerTerminal()
    {
        var entry = this.entry.PartyRoute ?? this.entry;
        if (entry.IsTutorial || entry.IsClassExperience || entry.IsQuestReplay ||
            entry.QuestClearMap is not null || entry.TerminalLayerMap is not null || entry.BossMap != entry.BaseBossMap ||
            storyRooms.Length != 0 || conditionalRooms.Count != 0 || passiveClearTarget is not { } target || target <= 0 ||
            entry.Maze.ClearConditions is not [{ Type: "destroy object", Count: 1 } condition] || condition.TargetId != target ||
            entry.RouteQuestId is 0 or >= 30000) return null;
        var acceptances = entry.QuestAcceptances.Where(row => row.QuestId == entry.RouteQuestId).ToArray();
        if (acceptances is not [{ Slot: <= 29 } accepted] || accepted.AcceptanceId == Guid.Empty ||
            entry.QuestAcceptances.Count(row => row.Slot == accepted.Slot) != 1 ||
            entry.QuestAcceptances.Count(row => row.AcceptanceId == accepted.AcceptanceId) != 1 ||
            entry.QuestId != 0 && !entry.QuestAcceptances.Any(row => row.QuestId == entry.QuestId &&
                row.Slot == entry.ActiveQuestSlot && row.AcceptanceId == entry.QuestAcceptanceId && row.AcceptanceId != Guid.Empty))
            return null;
        var last = entry.Maze.LayeredMaps.LastOrDefault(map => map.Position == entry.BaseBossMap.Position);
        if (last is null || last.Id <= 0 || last.Path.Length == 0 || !layeredRooms.TryGetValue(last,out var terminal) ||
            terminal.Plan.Room.DeclaredType != "[boss]" ||
            allRooms.Sum(room => room.Plan.Room.PassiveObjects.Count(source => source.ObjectId == target)) != 1 ||
            terminal.Plan.Room.PassiveObjects.Count(source => source.ObjectId == target) != 1) return null;
        var blocking = rooms[entry.BaseBossMap.Position].Plan.Monsters.Where(monster => monster.IsBlocking).ToArray();
        if (blocking.Length == 0 || blocking.Any(monster => monster.Source is null ||
            monster.AiCharacter is not null || !monster.CanReportDeath || monster.IsFriendly || monster.IsScriptedBoss ||
            monster.IsSpecialChild || monster.IsScriptedRetirement)) return null;
        // 单地图声明目标就是基础Boss格时，基础无品级Boss才晋升唯一物件末层；有真实Boss的明确基础终点保持原拒绝。
        if (entry.HasExplicitClearMap ? blocking.Any(monster => monster.IsBoss) : !blocking.Any(monster => monster.IsBoss))
            return null;
        return terminal;
    }

    private bool HasCommittedPassiveBaseDeaths()
    {
        if (entry.PassiveClearTerminalMap is null) return true;
        var basis = rooms[entry.BaseBossMap.Position];
        // Cleared允许清退，Dead也包含非战斗退休；基础阻挡必须另有本Run实际提交的原死亡候选。
        return basis.Plan.Monsters.Where(monster => monster.IsBlocking).All(monster => basis.Dead.Contains(monster.ObjectKey) &&
            committedPreparations.Any(prepared => ReferenceEquals(prepared.Monster,monster) && IsConfirmedDeath(prepared) &&
                ReferenceEquals(prepared.Death.Authority,authority) && prepared.Death.Identity.InstanceId == identity.InstanceId &&
                prepared.Death.Identity.TaskAttempt == identity.TaskAttempt && prepared.Death.Identity.CharacterId == identity.CharacterId &&
                prepared.Death.Identity.QuestId == identity.QuestId));
    }

    private static bool ValidExplicitTerminalLayer(DungeonEntryPlan entry, DungeonFixedRoute route)
    {
        entry = entry.PartyRoute ?? entry;
        if (entry.TerminalLayerMap is not { } layer) return entry.TerminalLayerAcceptanceId == Guid.Empty &&
            entry.TerminalLayerQuestId == 0 && entry.TerminalLayerQuestSlot == byte.MaxValue;
        if (!entry.HasExplicitClearMap || entry.IsTutorial || entry.IsClassExperience || entry.IsQuestReplay ||
            entry.BossMap != entry.BaseBossMap || entry.Maze.StoryStages.Count != 0 || layer.Kind != "layered" ||
            layer.Position == entry.BaseBossMap.Position || layer.Id != entry.BaseBossMap.Id ||
            layer.Path.Length == 0 || layer.Path != entry.BaseBossMap.Path || entry.TerminalLayerAcceptanceId == Guid.Empty ||
            entry.TerminalLayerQuestId is 0 or >= 30000 || entry.TerminalLayerQuestSlot > 29 ||
            entry.QuestAcceptances.Count(row => row.AcceptanceId == entry.TerminalLayerAcceptanceId) != 1 ||
            entry.QuestAcceptances.Count(row => row.Slot == entry.TerminalLayerQuestSlot) != 1 ||
            entry.QuestAcceptances.Count(row => row.AcceptanceId == entry.TerminalLayerAcceptanceId &&
                row.QuestId == entry.TerminalLayerQuestId && row.Slot == entry.TerminalLayerQuestSlot && row.ConditionState <= 1) != 1 ||
            entry.Maze.LayeredMaps.Count(map => map.Id == layer.Id && map.Position != entry.BaseBossMap.Position) != 1 ||
            entry.Maze.LayeredMaps.LastOrDefault(map => map.Position == layer.Position) != layer ||
            entry.Maze.ConditionalMaps.Any(map => map.Position == layer.Position)) return false;
        var rooms = route.Rooms.Where(room => room.Room.Map == layer).ToArray();
        return rooms is [var terminal] && terminal.Room.DeclaredType == "[boss]" &&
            terminal.Monsters.Any(monster => monster.CanReportDeath && monster.IsBoss && monster.AiCharacter is null);
    }

    private bool IsTerminalRoom() => HopeCatalog is not null ? IsHopeTerminalRoom() :
        entry.IsTerminalMap(current.Plan.Room.Map) && (storyRooms.Length == 0 || storyStageIndex == storyRooms.Length-1);

    private bool HasPendingBossDeaths(ushort? preparedMonsterKey)
    {
        int ordinaryBosses = current.Plan.Monsters.Count(monster => monster.IsBoss && monster.AiCharacter is null);
        return current.Plan.Monsters.Any(monster => monster.IsBoss && (monster.AiCharacter is not null || ordinaryBosses > 1) &&
            !current.Dead.Contains(monster.ObjectKey) && monster.ObjectKey != preparedMonsterKey);
    }

    private bool TerminalBossCleared(ushort? preparedMonsterKey = null, bool passiveConfirmed = false)
    {
        if (current.Altar is { } altar)
            return IsTerminalRoom() && (altar.Completed || altar.CanComplete(current.Dead, preparedMonsterKey));
        // 正常通关由当前终点Boss死亡决定；未走过的支路不属于全图清怪要求。
        if (!IsTerminalRoom() || !HasCommittedPassiveBaseDeaths() || HasPendingBossDeaths(preparedMonsterKey) ||
            passiveClearTarget is not null && !HasCommittedPassiveClearCondition && !passiveConfirmed)
            return false;
        // 无Boss的终点仍须等待真实敌对APC；它们不阻挡普通过房，但不能被当作空终点。
        if (!current.Plan.Monsters.Any(monster => monster.IsBoss) &&
            current.Plan.Monsters.Any(monster => monster.AiCharacter is not null && monster.CanReportDeath &&
                !current.Dead.Contains(monster.ObjectKey) && monster.ObjectKey != preparedMonsterKey)) return false;
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
            HasPendingDeath || pendingSettlement is not null || !CanAdvance()) return null;
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
    private bool CanAdvance() => identity.Epoch < long.MaxValue - 2 && tagEvildom?.Snapshot is not { Defeated: true } and not { Cancelled: true };
    private void Advance()
    {
        CancelRidable();
        identity = identity with { Epoch = checked(identity.Epoch + 1) };
    }
    private DungeonRunResult Result(DungeonRunOutcome outcome) => new(outcome, Snapshot);
    private DungeonRunResult? Replay(DungeonRunIdentity expected, Guid actionId, string kind, DungeonPoint? target)
        => acceptedActions.TryGetValue(actionId, out ActionFact? fact)
            ? Result(fact == new ActionFact(expected, kind, target) ? DungeonRunOutcome.Duplicate : DungeonRunOutcome.Rejected)
            : null;
}
