using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Quests;
using DfoServer.Game.Quests;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using System.Security.Cryptography;
using System.Collections.Immutable;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record FormalDungeon(DungeonEntryPlan Entry, DungeonFixedRoute Route,
        SinglePlayerDungeonRun Run, NativeDungeonCombatAuthority Authority, uint MapSeed,
        DungeonGroundState Ground, IReadOnlyDictionary<int, DungeonExplicitDropPool> Pools, Func<int, int> Draw,
        Func<DungeonEntryPlan, DungeonPlannedMonster, DungeonDropGenerationPlan>? PrepareDrops,
        ImmutableDictionary<ushort, DungeonDropGenerationPlan> DropPlans)
    {
        public bool HasPendingFatigue { get; set; }
        public DungeonInventoryDiscardPreparation? PendingInventoryDiscard { get; set; }
        public bool CharacterDead { get; set; }
        public long DeathSerial { get; set; }
        public Location? OrdinaryReturnPosition { get; init; }
        public long StartedTimestamp { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
        public uint KillExperienceTotal { get; set; }
        public uint ClearTimeMilliseconds { get; set; }
        public bool SettlementEnabled { get; set; }
        public FormalSettlement? Settlement { get; set; }
        public Dictionary<(DungeonMap Map, int Template), PendingQuestScene> QuestSceneEvents { get; } = [];
        public QuestDropResources? QuestDrops { get; init; }
        public QuestDropPlan? PendingQuestDrop { get; set; }
        public DfoServer.Game.Tutorials.TutorialCheckpointPlan? PendingTutorialClear { get; set; }
        public ushort? TutorialClearTarget { get; set; }
    }
    FormalDungeon? formalDungeon;
    bool PreservesTrainingItems => Phase == SessionPhase.Dungeon && formalDungeon?.Entry.IsTrainingRoom == true;
    sealed record PreparedFormalDungeon(Character Character, long SelectionEpoch, FormalDungeon Dungeon, uint NextKey)
    {
        public DungeonSelectionContext? ClassExperienceSelection { get; init; }
    }
    public DungeonRunSnapshot? DungeonSnapshot => owner.Execute(() => formalDungeon?.Run.Snapshot);

    public FormalDungeonReceipt? EnterFormalDungeon(uint dungeonId, uint questId, byte difficulty,
        DungeonCatalog catalog, DungeonRoomCatalog rooms, DungeonMonsterDropCatalog? drops = null, ItemCatalog? items = null,
        Func<int, int>? draw = null, EpicPieceCatalog? pieces = null, Character? requiredCharacter = null,
        FormalDungeonReceipt? retry = null,
        Func<DungeonEntryPlan, DungeonPlannedMonster, DungeonDropGenerationPlan>? prepareDrops = null,
        TutorialCatalog? tutorials = null, TutorialScenes? tutorialScenes = null, bool classExperience = false)
    {
        var prepared = PrepareFormalDungeon(dungeonId,questId,difficulty,catalog,rooms,drops,items,draw,pieces,requiredCharacter,retry,
            prepareDrops,tutorials,tutorialScenes,classExperience);
        return prepared is null ? null : owner.Execute(() => CanEnterFormalDungeon(retry) ? InstallFormalDungeon(prepared) : null);
    }

    PreparedFormalDungeon? PrepareFormalDungeon(uint dungeonId, uint questId, byte difficulty,
        DungeonCatalog catalog, DungeonRoomCatalog rooms, DungeonMonsterDropCatalog? drops, ItemCatalog? items,
        Func<int, int>? draw, EpicPieceCatalog? pieces, Character? requiredCharacter, FormalDungeonReceipt? retry,
        Func<DungeonEntryPlan, DungeonPlannedMonster, DungeonDropGenerationPlan>? prepareDrops = null,
        TutorialCatalog? tutorials = null, TutorialScenes? tutorialScenes = null, bool classExperience = false)
    {
        var (expected, epoch, selection) = owner.Execute(() => (Ready && CanEnterFormalDungeon(retry) &&
            (requiredCharacter is null || ReferenceEquals(requiredCharacter,Selected)) ? Selected : null, SelectionEpoch, CurrentDungeonSelection));
        if (expected is null || expected.DungeonState is null)
            return null;
        if ((dungeonId == 7142) != classExperience || classExperience && (questId != 0 || difficulty != 0 || retry is not null ||
            selection is not { IsClassExperience: true } || selection.SelectionEpoch != epoch || selection.CharacterId != expected.Id ||
            selection.OriginPosition != expected.Position)) return null;
        var done = QuestEligibility.Completed(expected);
        var flow = tutorials?.Get(expected.Job);
        bool training = expected.Tutorial is { Completed: false } pending && pending.DungeonId == dungeonId &&
            flow?.DungeonId == dungeonId && questId == 0 && difficulty == 0 &&
            tutorials!.Owns(expected.Job, dungeonId, catalog.Get(checked((int)dungeonId)));
        bool trainingRoom = dungeonId == 5000 && questId == 0 && difficulty == 0;
        if (!training && !trainingRoom && !classExperience && (!towns.Areas.TryGetValue((expected.Position.Town, expected.Position.Area), out var area) ||
            area.DungeonWorldMap is not { } worldMap || !towns.DungeonMaps.TryGetValue(worldMap,out var selectionMap) ||
            selectionMap.Error is not null || !selectionMap.Options.Any(option => option.DungeonId == dungeonId &&
                (option.QuestId == -1 || (option.InProgress ? expected.ActiveMainQuests.Any(active => active.QuestId == option.QuestId) :
                    done.Contains(option.QuestId)))) ||
            !area.Map(done).DungeonGates.Any(gate =>
                gate.Quests.All(done.Contains) && gate.Conditions.Length == 0))) return null;
        StoryQuest? quest = null;
        if (questCatalog is not null && questId != 0)
        {
            if (questId >= 30000 || !questCatalog.TryGet((int)questId, out quest) ||
                !QuestCoverage.HasOrdinaryDeclarations(quest) ||
                !QuestEligibility.Evaluate(expected with { ActiveMainQuests = expected.ActiveMainQuests.Where(row => row.QuestId != questId).ToArray() },
                    quest, questCatalog).Eligible) return null;
        }
        bool allowClearedQuest = questId is > 0 and < 30000 &&
            repository.LoadQuestClear(owner, expected, (ushort)questId) is not null;
        if (!DungeonEntryPlan.TryCreate(catalog, expected, dungeonId, questId, difficulty, out var entry, out _, allowClearedQuest, quest,draw,questCatalog))
            return null;
        if (entry!.QuestId != 0 && entry.QuestAcceptanceId == Guid.Empty)
            return null;
        if (entry.IsClassExperience != classExperience) return null;
        if (entry.IsTutorial && !training) return null;
        if (questId == 0 && entry.RouteQuestId > 0 && questCatalog is not null &&
            expected.ActiveMainQuests.Any(row => row.QuestId == entry.RouteQuestId) &&
            !QuestEligibility.Evaluate(expected with { ActiveMainQuests = expected.ActiveMainQuests.Where(row => row.QuestId != entry.RouteQuestId).ToArray() },
                questCatalog.Get((int)entry.RouteQuestId),questCatalog).Eligible) return null;
        var prepared = PrepareFormalDungeonResources(expected,epoch,entry!,rooms,drops,items,draw,pieces,prepareDrops,
            training ? flow!.Destination : null,tutorialScenes);
        return prepared is null ? null : prepared with { ClassExperienceSelection = classExperience ? selection : null };
    }

    PreparedFormalDungeon? PrepareFormalDungeonResources(Character expected,long epoch,DungeonEntryPlan entry,
        DungeonRoomCatalog rooms,DungeonMonsterDropCatalog? drops,ItemCatalog? items,Func<int,int>? draw,EpicPieceCatalog? pieces,
        Func<DungeonEntryPlan, DungeonPlannedMonster, DungeonDropGenerationPlan>? prepareDrops = null,
        Location? tutorialReturn = null, TutorialScenes? tutorialScenes = null)
    {
        if (expected.DungeonState is not { } state) return null;
        state.Validate();
        if (entry.UsesFatigue && (expected.Fatigue is null || FatigueForClient(expected).Remaining == 0)) return null;
        var ordinaryReturn = entry.IsTrainingRoom || entry.IsClassExperience ? expected.Position :
            entry.IsTutorial ? tutorialReturn : entry.IsOrdinary ? towns.SelectionReturn(expected.Position, expected.CompletedQuests.ToHashSet()) : null;
        if (entry.IsOrdinary && ordinaryReturn is null) return null;
        QuestDropResources? questDrops = null;
        if (!entry.IsTutorial && !entry.IsClassExperience && questCatalog is not null && items is not null && pieces is not null)
        {
            try { questDrops = QuestDropResources.Create(expected,questCatalog,items,pieces); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException) { return null; }
        }
        if (state.NextObjectKey > ushort.MaxValue || state.LastAttempt == long.MaxValue)
            return null;
        var topology = entry!.Maze.StaticTopology;
        if (!topology.Supported) return null;
        var plans = new List<DungeonMonsterPlan>();
        uint next = state.NextObjectKey;
        try
        {
            foreach (var map in entry.Maze.MapSpecification!.Concat(entry.Maze.LayeredMaps).Concat(entry.Maze.ConditionalMaps))
            {
                if (next > ushort.MaxValue) return null;
                var room = rooms.Get(map, requirePassiveDefinitions: false, allowUnresolvedSpecialMetadata: entry.IsTutorial);
                if (entry.IsTutorial) room = (tutorialScenes ?? throw new InvalidDataException("训练动作目录未装配")).Apply(room);
                var plan = DungeonMonsterPlan.FromRoom(room, expected.Id, checked((ushort)next), entry.BasisLevel,
                    includeSpecial: !entry.IsTutorial, playerTeam: entry.PlayerTeam);
                plans.Add(plan);
                if (plan.Monsters.Count > 0 || plan.SpecialObjects.Count > 0) next = plan.NextObjectKey;
            }
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException or OverflowException) { return null; }
        var pools = new Dictionary<int, DungeonExplicitDropPool>();
        var dropPlans = ImmutableDictionary.CreateBuilder<ushort, DungeonDropGenerationPlan>();
        var groundReservations = new List<DungeonGroundReservation>();
        if ((drops is null) != (items is null)) throw new InvalidDataException("掉落资源依赖必须同时提供");
        if (drops is not null && items is not null && !entry.IsClassExperience)
        {
            foreach (var plan in plans)
            foreach (var monster in plan.Monsters.Where(monster => monster.IsBlocking && monster.Source is not null &&
                (prepareDrops is not null || monster.MonsterId is 1 or 2 && monster.SpawnType == 0)))
            {
                // 资源准备在owner锁外；每个真实monster保留自己的等级、类型、难度及完整数量规则。
                DungeonDropGenerationPlan? dropGeneration;
                try { dropGeneration = prepareDrops?.Invoke(entry, monster); }
                catch (Exception error) when (error is NotSupportedException or FileNotFoundException) { return null; }
                if (prepareDrops is not null && dropGeneration is null)
                    throw new InvalidDataException("掉落规则准备不能以空引用替代未支持的怪物");
                if (dropGeneration is not null) dropPlans.Add(monster.ObjectKey, dropGeneration);
                if (prepareDrops is null && !pools.ContainsKey(monster.MonsterId))
                    pools.Add(monster.MonsterId, DungeonExplicitDropPolicy.CreatePool("monster/monster.lst", drops.Get(monster.MonsterId), items.Get));
                // 创建位置是已证资源位置；不将首次受击坐标冒充死亡瞬间位置。
                // 地面RAW不携带最终库存目的槽，独立对象使用显式临时record slot0。
                // 剧情对象可生成在负坐标，位置按协议的16位字段保留，不作为入场资格。
                for (int ordinal = 0; ordinal < Math.Max(1, dropGeneration?.MaximumObjects ?? 1); ordinal++)
                {
                    if (next == expected.Id) next++;
                    if (next > ushort.MaxValue) return null;
                    groundReservations.Add(new(monster.ObjectKey, next++, plan.Room.Map,
                        unchecked((ushort)monster.Source!.RawValues[3]), unchecked((ushort)monster.Source.RawValues[4]), 0, ordinal));
                }
            }
        }
        else if (prepareDrops is not null && !entry.IsClassExperience)
            throw new InvalidDataException("集合掉落生成计划必须同时装配实际物品依赖");
        var route = new DungeonFixedRoute(Array.AsReadOnly(plans.ToArray()),
            Array.AsReadOnly(topology.Links!.Select(link => new DungeonRouteConnection(link.First, link.Second)).ToArray()));
        var authority = new NativeDungeonCombatAuthority();
        var committedEntry = entry with { CharacterRevision = checked(expected.Revision + 1) };
        if (!SinglePlayerDungeonRun.TryCreate(committedEntry, state.LastAttempt + 1, epoch, route, authority, out var run, out _) ||
            !authority.BindTo(run!)) return null;
        var ground = new DungeonGroundState(run!, route.Rooms, groundReservations, drops is null ? "" : "monster/monster.lst");
        var dungeon = new FormalDungeon(committedEntry, route, run!, authority, checked((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue)),
            ground, pools, draw ?? RandomNumberGenerator.GetInt32, prepareDrops, dropPlans.ToImmutable())
            { QuestDrops = questDrops, OrdinaryReturnPosition = ordinaryReturn };
        return new(expected,epoch,dungeon,next);
    }

    bool CanEnterFormalDungeon(FormalDungeonReceipt? retry) => retry is null ?
        Phase is SessionPhase.Town or SessionPhase.MapSelect && formalDungeon is null :
        CanPublishFormalDungeon(retry) && CanReplaceSettledDungeon(formalDungeon, retry) && !HasPendingQuestScene && !HasPendingFormalCard;

    FormalDungeonReceipt? InstallFormalDungeon(PreparedFormalDungeon prepared)
    {
        if (!Ready || SelectionEpoch != prepared.SelectionEpoch || !ReferenceEquals(Selected,prepared.Character)) return null;
        if (prepared.Dungeon.Entry.IsClassExperience && (prepared.ClassExperienceSelection is not { IsClassExperience: true } ||
            !ReferenceEquals(CurrentDungeonSelection, prepared.ClassExperienceSelection))) return null;
        // 先持久预留key/attempt；通知失败也不回绕key或复用旧身份。
        var committed = repository.ReserveDungeonAttempt(owner,prepared.Character,prepared.NextKey);
        if (formalDungeon is { } previous)
        {
            if (previous.Run.Return(previous.Run.Snapshot.Identity, Guid.NewGuid()).Outcome != DungeonRunOutcome.Accepted)
                throw new InvalidOperationException("已提交再挑战不能结束已复验的原结算实例");
            previous.Ground.ExpireRun();
        }
        Selected = committed;
        formalDungeon = prepared.Dungeon;
        pendingDungeonContinuation = null;
        pendingQuestReplay = null;
        CurrentDungeonSelection = null;
        SetState(SessionPhase.Loading);
        return FormalReceipt();
    }

    bool CanReplaceSettledDungeon(FormalDungeon? dungeon, FormalDungeonReceipt receipt) =>
        Phase == SessionPhase.Settlement && dungeon?.Settlement is { Committed: true } &&
        dungeon.Run.Snapshot.Identity == receipt.Snapshot.Identity && !dungeon.Run.HasPendingDeath &&
        receipt.Entry == (dungeon.Entry with { StartMap = dungeon.Run.Snapshot.CurrentMap }) &&
        !dungeon.Run.HasPendingSettlement && dungeon.Run.Snapshot.Identity.Epoch != long.MaxValue &&
        !dungeon.HasPendingFatigue && !dungeon.Ground.HasPendingOperation && pendingQuestCompletion is null;

    public FormalDungeonReceipt? CurrentFormalDungeonScene() => owner.Execute(() =>
        Ready && Selected is not null && formalDungeon is not null &&
        Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement ? FormalReceipt() : null);

    public FormalDungeonReceipt? CompleteFormalDungeonLoading()
    {
        var (expected,dungeon,epoch,identity,entry) = owner.Execute(() =>
            (Ready && Phase == SessionPhase.Loading ? Selected : null, formalDungeon, SelectionEpoch,
                formalDungeon?.Run.Snapshot.Identity,formalDungeon?.Run.Entry));
        if (expected is null || dungeon is null || identity is null || entry is null) return null;
        bool hasMatchingQuest = !entry.IsClassExperience && questCatalog is not null && expected.ActiveMainQuests.Any(active =>
            QuestHuntProgress.Bound(active,entry) && questCatalog.TryGet(active.QuestId,out var quest) &&
            QuestDungeonClearProgress.CountsInitializations(quest,entry));
        return owner.Execute(() =>
        {
            if (!Ready || !ReferenceEquals(Selected,expected) || SelectionEpoch != epoch || Phase != SessionPhase.Loading ||
                !ReferenceEquals(formalDungeon,dungeon) || dungeon.Run.Snapshot.Identity != identity ||
                dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement || identity.Epoch == long.MaxValue) return null;
            if (dungeon.Entry.UsesFatigue && debugSettings?.InfiniteFatigue != true)
            {
                dungeon.HasPendingFatigue = true;
                Selected = repository.ConsumeDungeonRoomFatigue(owner, expected, dungeon.Run);
                dungeon.HasPendingFatigue = false;
            }
            var result = dungeon.Run.CompleteLoading(identity,Guid.NewGuid(),hasMatchingQuest);
            if (result.Outcome != DungeonRunOutcome.Accepted) return null;
            pendingDungeonContinuation = null;
            pendingQuestReplay = null;
            SetState(dungeon.Run.Phase == SinglePlayerDungeonPhase.Settlement ? SessionPhase.Settlement : SessionPhase.Dungeon);
            return FormalReceipt();
        });
    }

    public bool ConfirmFormalDungeonLayerBase(byte x, byte y) => owner.Execute(() =>
        Ready && Selected is not null && formalDungeon is { } dungeon &&
        Phase is SessionPhase.Loading or SessionPhase.Dungeon && !HasPendingQuestScene &&
        dungeon.Run.ConfirmLayerBase(dungeon.Run.Snapshot.Identity,new(x,y)));

    public FormalDungeonReceipt? MoveFormalDungeon(byte x, byte y, byte moveKind = 0) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase != SessionPhase.Dungeon || formalDungeon is not { } dungeon || dungeon.CharacterDead ||
            dungeon.Ground.HasPendingOperation || dungeon.PendingTutorialClear is not null || HasPendingQuestScene) return null;
        if (moveKind > 1) return null;
        var target = new DungeonPoint(x,y);
        var result = moveKind == 1 || target == dungeon.Run.Snapshot.CurrentMap.Position && dungeon.Run.Snapshot.CurrentMap.Kind == "layered"
            ? dungeon.Run.MoveLayer(dungeon.Run.Snapshot.Identity,Guid.NewGuid(),target,moveKind == 1)
            : dungeon.Run.Move(dungeon.Run.Snapshot.Identity,Guid.NewGuid(),target);
        if (result.Outcome != DungeonRunOutcome.Accepted) return null;
        SetState(SessionPhase.Loading);
        return FormalReceipt();
    });

    public FormalDungeonReceipt? PendingDungeonBossEntrance() => owner.Execute(() =>
        Ready && Selected is not null && formalDungeon is { } dungeon &&
        Phase is SessionPhase.Dungeon or SessionPhase.Settlement && dungeon.Run.BossEntranceReady && !dungeon.Run.BossEntranceOpen
            ? FormalReceipt() : null);

    public void ConfirmDungeonBossEntrance(FormalDungeonReceipt receipt) => owner.Execute(() =>
    {
        if (CanPublishFormalDungeon(receipt)) formalDungeon!.Run.ConfirmBossEntranceOpen();
        return true;
    });

    public FormalDungeonDeathResult ObserveFormalDungeonDeath(NativeDungeonDeathObservation observation,
        DungeonKillExperienceCatalog rewards, CharacterGrowthCatalog growth) => ObserveDungeonDeath(observation,null,rewards,growth);

    public FormalDungeonDeathResult ObserveFormalDungeonScriptedDeath(NativeDungeonScriptedDeathObservation observation,
        DungeonKillExperienceCatalog rewards, CharacterGrowthCatalog growth) => ObserveDungeonDeath(null,observation,rewards,growth);

    FormalDungeonDeathResult ObserveDungeonDeath(NativeDungeonDeathObservation? normal, NativeDungeonScriptedDeathObservation? scripted,
        DungeonKillExperienceCatalog rewards, CharacterGrowthCatalog growth) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement) || formalDungeon is not { } dungeon || dungeon.Ground.HasPendingOperation || HasPendingQuestScene)
            return new FormalDungeonDeathResult(DungeonRunOutcome.Rejected);
        uint objectKey = normal?.MonsterKey ?? scripted!.MonsterKey;
        var target = dungeon.Route.Rooms.Single(room => room.Room.Map == dungeon.Run.Snapshot.CurrentMap)
            .Monsters.SingleOrDefault(monster => monster.ObjectKey == objectKey);
        var issued = normal is not null ? dungeon.Authority.ValidateAndIssue(dungeon.Run,dungeon.Run.Snapshot.Identity,normal) :
            dungeon.Authority.ValidateAndIssueScripted(dungeon.Run,dungeon.Run.Snapshot.Identity,scripted!);
        if (issued.Death is null)
        {
            if (issued.Outcome is not (DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate))
                return new FormalDungeonDeathResult(issued.Outcome) { ReportedTarget = target };
            ushort key = checked((ushort)objectKey);
            if (target is not { CanReportDeath: false })
                return new FormalDungeonDeathResult(issued.Outcome,MonsterKey:key) { ReportedTarget = target };
            dungeon.Run.RetireNonCombatObject(key);
            return new FormalDungeonDeathResult(issued.Outcome,FormalReceipt(),key) { ReportedTarget = target };
        }
        if (issued.Outcome == DungeonRunOutcome.Duplicate && dungeon.Route.Rooms.Any(room => room.Monsters.Any(monster =>
            monster.IsSpecialChild && monster.ObjectKey == issued.Death.MonsterKey)))
            return new FormalDungeonDeathResult(DungeonRunOutcome.Duplicate) { ReportedTarget = target };
        if (dungeon.Run.CurrentLivingMonster(issued.Death.MonsterKey) is { } retiring &&
            (retiring.IsSpecialChild || dungeon.Entry.IsTutorial && (retiring.IsScriptedRetirement || retiring.AiCharacter?.Faction is "[character]" or "[neutral]")))
        {
            var retired = dungeon.Run.RetireSpecialChild(issued.Death);
            var elevator = retired == DungeonRunOutcome.Accepted && dungeon.Run.CurrentElevator is { } state &&
                state.Complete(issued.Death.MonsterKey) is { } notice ? ElevatorReceipt(state, notice) : null;
            return new(retired, retired == DungeonRunOutcome.Accepted ? FormalReceipt() : null, issued.Death.MonsterKey)
                { ReportedTarget = target, SpecialChildRetired = retired == DungeonRunOutcome.Accepted, Elevator = elevator };
        }
        var preparation = dungeon.Run.PrepareConfirmedDeath(issued.Death);
        if (preparation.Outcome == DungeonRunOutcome.Duplicate)
            return new FormalDungeonDeathResult(DungeonRunOutcome.Duplicate,MonsterKey:issued.Death.MonsterKey) { ReportedTarget = target };
        if (preparation.Outcome != DungeonRunOutcome.Accepted)
            return new FormalDungeonDeathResult(preparation.Outcome) { ReportedTarget = target };
        var pending = preparation.Pending!;
        if (dungeon.Entry.IsClassExperience)
        {
            // 体验死亡只推进本次内存实例；不写成长/掉落/任务账本，也不向客户端覆盖临时技能。
            var death = dungeon.Run.ConfirmCommittedDeath(pending);
            if (death.Outcome != DungeonRunOutcome.Accepted)
                throw new InvalidOperationException("职业体验死亡不能确认原实例准备");
            return new FormalDungeonDeathResult(DungeonRunOutcome.Accepted, FormalReceipt(), pending.Death.MonsterKey)
                { ReportedTarget = target };
        }
        var reward = DungeonKillExperiencePolicy.Plan(dungeon.Run, pending, rewards, Selected.Level);
        FrozenDungeonGroundDeath? loot = null;
        if (dungeon.Ground.HasReservation(pending.Death.MonsterKey))
            loot = dungeon.DropPlans.TryGetValue(pending.Death.MonsterKey, out var generation)
                ? dungeon.Ground.GetOrFreezeAssets(pending, () => generation.Generate(dungeon.Draw, DateTimeOffset.UtcNow), generation.ResourceSource)
                : dungeon.Ground.GetOrFreeze(pending, () =>
            {
                var choice = DungeonExplicitDropPolicy.Select(dungeon.Pools[pending.Monster.MonsterId],
                    dungeon.Draw(DungeonExplicitDropPolicy.TriggerDenominator), dungeon.Draw(DungeonExplicitDropPolicy.SelectionDenominator));
                return choice is null ? null : InventoryRewardGrantService.Create(choice.Template, choice.Count);
            });
        byte previousLevel = Selected.Level;
        var previousQuests = Selected.ActiveMainQuests;
        if (dungeon.QuestDrops is { } resources)
            dungeon.PendingQuestDrop ??= QuestDropPlan.Create(Selected,dungeon.Run.Entry,resources,pending.Death.EventId,
                pending.Death.Identity,pending.Result.Snapshot.CurrentMap.Id,pending.Monster.MonsterId,pending.Monster.EnemyType,pending.Result.ClearFact is not null,
                dungeon.Draw,DateTimeOffset.UtcNow);
        var questDrop = dungeon.PendingQuestDrop;
        var committed = CommitDungeonGrowth(SelectionEpoch, Selected, dungeon.Run, pending, reward, growth, loot, questDrop);
        dungeon.PendingQuestDrop = null;
        if (loot is not null && !dungeon.Ground.PublishCommitted(pending, loot))
            throw new InvalidOperationException("已提交掉落的原准备不能发布");
        SetState(dungeon.Run.Phase == SinglePlayerDungeonPhase.Settlement ? SessionPhase.Settlement : SessionPhase.Dungeon);
        dungeon.KillExperienceTotal = checked(dungeon.KillExperienceTotal + committed.Commit.GainedExperience);
        if (committed.Death.ClearFact is not null)
            dungeon.ClearTimeMilliseconds = checked((uint)System.Diagnostics.Stopwatch.GetElapsedTime(dungeon.StartedTimestamp).TotalMilliseconds);
        return new FormalDungeonDeathResult(DungeonRunOutcome.Accepted, FormalReceipt(), pending.Death.MonsterKey, reward,
            committed.Death.ClearFact, loot?.UsesCollectionLedger == true ? null : loot?.Ground, previousLevel)
        {
            Drops = loot?.Grounds ?? [],
            GainedExperience = committed.Commit.GainedExperience,
            ReportedTarget = target,
            PreviousQuests = previousQuests,
            QuestDropSlots = questDrop?.Inventory.ChangedSlots ?? [],
            CulledMonsterKeys = committed.Death.CulledMonsterKeys
        };
    });

    public FormalDungeonPickupResult PickFormalDungeonItem(uint groundKey, ItemCatalog items, EpicPieceCatalog pieces,
        GoldLimitCatalog? goldLimits = null) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement) || formalDungeon is not { } dungeon || dungeon.Run.HasPendingSettlement || HasPendingQuestScene)
            return new FormalDungeonPickupResult(DungeonRunOutcome.Rejected);
        var identity = dungeon.Run.Snapshot.Identity;
        var original = dungeon.Ground.FindAvailable(identity, groundKey) ?? dungeon.Ground.Snapshot.Items
            .Where(item => item.Status == DungeonGroundStatus.PickupPending && item.Ground.Reservation.GroundKey == groundKey)
            .Select(item => item.Ground).SingleOrDefault();
        if (original is null) return new FormalDungeonPickupResult(DungeonRunOutcome.Rejected);
        var prepared = dungeon.Ground.PreparePickup(identity, original);
        if (prepared is null) return new FormalDungeonPickupResult(DungeonRunOutcome.Rejected);
        var now = DateTimeOffset.UtcNow;
        InventoryResult plan;
        if (original.InventoryOrigin is not null && goldLimits is not null && original.Core.Kind is ItemKind.CreatureEquipment or ItemKind.GuildMedal)
            plan = InventoryRewardGrantService.TryGrantItems(Selected, [new(0, new(InventoryListType.Main, 0), original.Core)],
                items, goldLimits, pieces, now);
        else if (original.UsesCollectionLedger && goldLimits is not null)
            plan = original.IsGold ? DungeonPickupPlanning.Gold(Selected, original.Content, goldLimits) :
                DungeonPickupPlanning.Item(Selected, original.Content, items.Get(original.Core.ItemId), goldLimits, pieces, now);
        else
            plan = original.IsGold ? InventoryResult.Reject() :
                InventoryRewardGrantService.TryGrantCore(Selected, original.Core, items.Get(original.Core.ItemId), pieces, now);
        if (!plan.Accepted)
        {
            dungeon.Ground.CancelPickup(prepared);
            return new FormalDungeonPickupResult(DungeonRunOutcome.Rejected) { CapacityRejected = plan.Error == 17 };
        }
        // 存储异常/结果不明保留同一准备；原key重试只读已提交receipt，不重发资产。
        var commit = repository.CommitDungeonPickup(owner, Selected, prepared, plan.Plan!);
        if (!dungeon.Ground.ConfirmPickup(prepared)) throw new InvalidOperationException("已提交拾取不能确认原准备");
        Selected = commit.Character;
        return new FormalDungeonPickupResult(DungeonRunOutcome.Accepted, FormalReceipt(), original, commit);
    });

    public TownTravelReceipt? ReturnFromFormalDungeon() => owner.Execute(() =>
    {
        if (!Ready || Selected is null || formalDungeon is not { } dungeon || dungeon.Ground.HasPendingOperation || HasPendingFormalCard || pendingQuestCompletion is not null || dungeon.HasPendingFatigue || HasPendingQuestScene) return null;
        if (dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement) return null;
        if (dungeon.PendingTutorialClear is not null) return null;
        // 普通挑战沿本次入口安全点返回；先提交位置，失败时保留原实例与完整角色资产。
        if (dungeon.Entry.IsTutorial && Selected.Tutorial is { Completed: false })
            Selected = repository.CommitTutorialCheckpoint(owner, TutorialCheckpoint(Selected, "skip", Guid.NewGuid(), 0, 0,
                dungeon.Run.Snapshot.Identity, dungeon.Run.Snapshot.CurrentMap.Id, true)).Character;
        else if (dungeon.OrdinaryReturnPosition is { } back && Selected.Position != back)
            Selected = repository.Save(owner, Selected, back, Selected.Return);
        if (dungeon.Run.Return(dungeon.Run.Snapshot.Identity, Guid.NewGuid()).Outcome != DungeonRunOutcome.Accepted) return null;
        dungeon.Ground.ExpireRun();
        formalDungeon = null;
        pendingDungeonContinuation = null;
        pendingQuestReplay = null;
        SetState(SessionPhase.Town);
        return new TownTravelReceipt(Selected, owner.Generation, SelectionEpoch);
    });

    public bool CanPublishFormalDungeon(FormalDungeonReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        ReferenceEquals(Selected, receipt.Character) && formalDungeon?.Run.Snapshot.Identity == receipt.Snapshot.Identity);

    public bool CanPublishFormalDungeonRejection(Character character, long epoch, SessionPhase phase,
        DungeonSelectionContext? selection) => owner.Execute(() =>
    {
        if (!Ready || SelectionEpoch != epoch || Phase != phase || formalDungeon is not null ||
            !ReferenceEquals(CurrentDungeonSelection, selection))
            return false;
        if (!ReferenceEquals(Selected, character))
            throw new InvalidOperationException("副本拒绝发布前角色快照已变化");
        return true;
    });

    FormalDungeonReceipt FormalReceipt()
    {
        var dungeon = formalDungeon ?? throw new InvalidOperationException("无正式副本实例");
        var snapshot = dungeon.Run.Snapshot;
        var original = dungeon.Route.Rooms.Single(room => room.Room.Map == snapshot.CurrentMap);
        var living = snapshot.LivingMonsters.ToHashSet();
        var monsters = original with { Monsters = Array.AsReadOnly(original.Monsters.Where(monster => living.Contains(monster.ObjectKey)).ToArray()) };
        return new(Selected!, owner.Generation, SelectionEpoch, dungeon.Entry with { StartMap = snapshot.CurrentMap }, snapshot, monsters, dungeon.MapSeed);
    }
}
