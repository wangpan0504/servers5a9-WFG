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
using DfoServer.Game.Caching;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    sealed record FormalDungeon(DungeonEntryPlan Entry, DungeonFixedRoute Route,
        SinglePlayerDungeonRun Run, NativeDungeonCombatAuthority Authority, uint MapSeed,
        DungeonGroundState Ground, IReadOnlyDictionary<int, DungeonExplicitDropPool> Pools, Func<int, int> Draw,
        Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? PrepareDrops,
        ImmutableDictionary<ushort, DungeonDropGenerationPlan> DropPlans)
    {
        public uint NextObjectKey { get; set; }
        public bool HasPendingFatigue { get; set; }
        public DungeonInventoryDiscardPreparation? PendingInventoryDiscard { get; set; }
        public bool CharacterDead { get; set; }
        public GriefTowerItemUse? GriefItems { get; init; }
        public GriefTowerAdmission? GriefAdmission { get; init; }
        public ImmutableArray<ConsumableState> FreeConsumableEffects { get; set; } = [];
        public long DeathSerial { get; set; }
        public long? AltarRevivalDue { get; set; }
        public int ReviveCount { get; set; }
        public Location? OrdinaryReturnPosition { get; init; }
        public long StartedTimestamp { get; } = System.Diagnostics.Stopwatch.GetTimestamp();
        public long AltarStartedTimestamp { get; init; }
        public uint KillExperienceTotal { get; set; }
        public uint KillGrowthContractExperience { get; set; }
        public uint KillEquipmentExperience { get; set; }
        // 结果页“领主”行只读评分quartet的147槽；按本实例实际到账的领主基础经验累计（不含加成）。
        public uint KillBossExperience { get; set; }
        // 结果页特殊怪物明细：每只实际到账的怪一条（对象键、基础经验），客户端按类型归并。
        public List<DungeonObjectExperience> KillObjectExperience { get; } = [];
        public uint ClearTimeMilliseconds { get; set; }
        public bool SettlementEnabled { get; set; }
        public FormalSettlement? Settlement { get; set; }
        public Dictionary<(DungeonMap Map, int Template, int Ordinal), PendingQuestScene> QuestSceneEvents { get; } = [];
        public QuestDropResources? QuestDrops { get; init; }
        public QuestDropPlan? PendingQuestDrop { get; set; }
        public (PreparedDungeonDeath Death, DungeonExperienceReward Reward, DateTimeOffset At)? PendingDeathReward { get; set; }
        public DfoServer.Game.Tutorials.TutorialCheckpointPlan? PendingTutorialClear { get; set; }
        public ushort? TutorialClearTarget { get; set; }
        public AssaultModeSelection? Assault { get; init; }
        public AssaultModeRewardServices? AssaultRewards { get; init; }
        public CrackDimensionRewardServices? CrackRewards { get; init; }
        // 实验性：深渊派对入场票；入场提交（InstallFormalDungeon）时按当前背包重算并扣除。
        public HellPartyTicket? HellTicket { get; init; }
        public HellPartyTicket? LegendTicket { get; init; }
        public EpicPieceCatalog? HellPieces { get; init; }
        public ImmutableArray<CosmoPassCost> CosmoPasses { get; init; } = [];
        public int CosmoInvitationCount { get; init; }
        public int CosmoStoneCount { get; init; }
        // #510/#511 普通入场票（[required item]「物品 1 1」）的规划目录；入场提交时按最新背包重算。
        public EpicPieceCatalog? EntryPieces { get; init; }
        // 入场代价：DungeonIncountInfo 角色/账号次数与频道专属城镇 [required item] 消耗，入场提交时同一事务扣除。
        public DungeonEntryCost? ChannelCost { get; init; }
        public DungeonSpecialEntry? SpecialEntry { get; init; }
        public AltarSettlementState? AltarSettlement { get; set; }
        public DespairSettlementState? DespairSettlement { get; set; }
        // 实验性：波次怪"带奖励"的掉落计划；死亡时若它是本波次最后一只存活者则用它替换 DropPlans 里的普通计划。
        public ImmutableDictionary<ushort, DungeonDropGenerationPlan> HellRewardPlans { get; init; } = ImmutableDictionary<ushort, DungeonDropGenerationPlan>.Empty;
    }
    static readonly GameCacheSlot<FormalDungeon> dungeonCacheSlot = new();
    FormalDungeon? formalDungeon
    {
        get => gameCache.Get(dungeonCacheSlot);
        set
        {
            var previous = gameCache.Get(dungeonCacheSlot);
            if (previous is not null && (value is null || previous.Run.Snapshot.Identity.InstanceId != value.Run.Snapshot.Identity.InstanceId))
                DungeonTransientReceipts.Forget(owner, previous.Run.Snapshot.Identity);
            if (value is null) gameCache.Remove(dungeonCacheSlot);
            else gameCache.Set(dungeonCacheSlot, value);
        }
    }
    internal int CachedGameContexts => gameCache.Count;
    internal int? FormalEnteredDungeonId => formalDungeon is null ? null : checked((int)formalDungeon.Entry.DungeonId);
    bool PreservesDungeonConsumables => Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement &&
        formalDungeon?.Entry.HasFreeCombatConsumption == true;
    sealed record PreparedFormalDungeon(Character Character, long SelectionEpoch, FormalDungeon Dungeon, uint NextKey)
    {
        public DungeonSelectionContext? ClassExperienceSelection { get; init; }
        // 本次入场计入的冒险团每周地下城列；练习、训练、职业体验与比赛为 null。
        public WeeklyDungeonEntry? Weekly { get; init; }
    }
    public DungeonRunSnapshot? DungeonSnapshot => owner.Execute(() => formalDungeon?.Run.Snapshot);

    public FormalDungeonReceipt? EnterFormalDungeon(uint dungeonId, uint questId, byte difficulty,
        DungeonCatalog catalog, DungeonRoomCatalog rooms, DungeonMonsterDropCatalog? drops = null, ItemCatalog? items = null,
        Func<int, int>? draw = null, EpicPieceCatalog? pieces = null, Character? requiredCharacter = null,
        FormalDungeonReceipt? retry = null,
        Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops = null,
        TutorialCatalog? tutorials = null, TutorialScenes? tutorialScenes = null, bool classExperience = false,
        bool hellParty = false, bool hellLegend = false, bool sealDoor = false, bool practice = false,
        CrackDimensionRewardServices? crackRewards = null)
    {
        LastFormalDungeonRejection = null;
        var prepared = PrepareFormalDungeon(dungeonId,questId,difficulty,catalog,rooms,drops,items,draw,pieces,requiredCharacter,retry,
            prepareDrops,tutorials,tutorialScenes,classExperience,hellParty,hellLegend,sealDoor,practice,crackRewards);
        if (prepared is null) { LastFormalDungeonRejection ??= "prepare-returned-null"; return null; }
        var installed = owner.Execute(() => CanEnterFormalDungeon(retry) ? InstallFormalDungeon(prepared) : null);
        if (installed is null) LastFormalDungeonRejection = "state-changed-before-install";
        return installed;
    }

    /// <summary>#259：最近一次 EnterFormalDungeon 被拒绝的原因（仅用于诊断日志，不影响行为）；成功时为 null。</summary>
    public string? LastFormalDungeonRejection { get => Volatile.Read(ref lastFormalDungeonRejection); private set => Volatile.Write(ref lastFormalDungeonRejection, value); }
    string? lastFormalDungeonRejection;
    PreparedFormalDungeon? RejectFormal(string reason) { LastFormalDungeonRejection = reason; return null; }

    PreparedFormalDungeon? PrepareFormalDungeon(uint dungeonId, uint questId, byte difficulty,
        DungeonCatalog catalog, DungeonRoomCatalog rooms, DungeonMonsterDropCatalog? drops, ItemCatalog? items,
        Func<int, int>? draw, EpicPieceCatalog? pieces, Character? requiredCharacter, FormalDungeonReceipt? retry,
        Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops = null,
        TutorialCatalog? tutorials = null, TutorialScenes? tutorialScenes = null, bool classExperience = false, bool hellParty = false,
        bool hellLegend = false, bool sealDoor = false, bool practice = false,
        CrackDimensionRewardServices? crackRewards = null)
    {
        var (expected, epoch, selection) = owner.Execute(() => (Ready && (retry is not null || pendingDungeonContinuation is null) && CanEnterFormalDungeon(retry) &&
            (requiredCharacter is null || ReferenceEquals(requiredCharacter,Selected)) ? Selected : null, SelectionEpoch, CurrentDungeonSelection));
        if (expected is null || expected.DungeonState is null)
            return RejectFormal("session-not-ready-or-no-dungeon-state");
        GriefTowerAdmission? grief = null;
        uint selectionDungeon = dungeonId;
        if (dungeonId <= int.MaxValue && catalog.GriefTower.FloorsByDungeon.TryGetValue((int)dungeonId, out var requestedGrief))
        {
            if (difficulty != 0 || retry is not null || classExperience || hellParty || hellLegend || sealDoor || practice)
                return RejectFormal("grief-tower-mode-conflict");
            var progress = repository.ReadGriefTower(owner,expected.Id);
            // 选图控制器以首层副本作为塔入口；实际层由账号下该角色的持久账本和专表共同确定。
            var entrance = catalog.GriefTower.FloorsByDungeon.Values.Single(floor => floor.Layer == 1);
            var floor = catalog.GriefTower.FloorsByDungeon.Values.SingleOrDefault(floor => floor.Layer == progress.State.Layer);
            if (floor is null) return RejectFormal("grief-tower-completed");
            if (requestedGrief.Layer != 1 && requestedGrief != floor) return RejectFormal("grief-tower-floor-mismatch");
            dungeonId = checked((uint)floor.DungeonId);
            selectionDungeon = checked((uint)entrance.DungeonId);
            if (questId != 0 && (questId >= 30000 || questCatalog is null || !questCatalog.TryGet((int)questId,out var griefQuest) ||
                !QuestDungeonClearProgress.MatchesAlternative(griefQuest,dungeonId,difficulty)))
                return RejectFormal("grief-tower-quest-mismatch");
            var decision = GriefTowerEntryPolicy.Evaluate(catalog.GriefTower,progress.State,floor.DungeonId,
                expected.Level,progress.AdventureLevel,1,(timeProvider ?? TimeProvider.System).GetUtcNow());
            if (!decision.Accepted) return RejectFormal(decision.Reason);
            grief = new(progress,decision,floor.DungeonId);
            if (decision.UsesInvitation)
            {
                if (pieces is null) return RejectFormal("grief-tower-item-catalog-unavailable");
                var ticket = InventoryMaterialConsumptionService.Plan(expected.Inventory,
                    [new ItemMaterialCost(GriefTowerEntryDecision.InvitationItem,1)],pieces,
                    (timeProvider ?? TimeProvider.System).GetUtcNow());
                if (!ticket.Accepted || ticket.Plan is null) return RejectFormal("grief-tower-invitation-required");
            }
        }
        // #233 强袭：只有 ACK1547 授权过的任务副本可绕过选图地图选项，改以主副本(7300)在当前选图中核对。
        var assault = practice ? null : owner.Execute(() => CurrentAssaultAuthorization(expected, dungeonId, questId));
        if (assault is not null)
        {
            if (retry is not null || classExperience) return RejectFormal("assault-retry-or-class-experience");
            DungeonDefinition assaultDefinition;
            try { assaultDefinition = catalog.Get(checked((int)dungeonId)); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException) { return RejectFormal("assault-dungeon-definition-missing"); }
            if (!assaultDefinition.TryGetDesignatedDifficulty(out byte designated) ||
                difficulty != designated && difficulty != AssaultModeEntryPolicy.ClientAssaultDifficulty) return RejectFormal("assault-difficulty-mismatch");
            difficulty = designated;
            if (!AssaultModeEntryPolicy.ConsumptionPlan(expected.Inventory, assault.Selection.Requirement, assault.Pieces,
                DateTimeOffset.UtcNow).Accepted) return RejectFormal("assault-consumption-rejected");
        }
        uint optionDungeon = assault?.Selection.Parent ?? selectionDungeon;
        DungeonDespairAdmission? despair = null;
        if (dungeonId is >= 11008 and <= 11107)
        {
            if (questId != 0 || difficulty != 0 || practice ||
                hellParty || hellLegend || sealDoor || classExperience || assault is not null || retry is not null)
                return RejectFormal("despair-mode-or-floor-conflict");
            int day = ShopPurchaseCount.Today((timeProvider ?? TimeProvider.System).GetUtcNow());
            var progress = repository.LoadDespairProgress(owner, expected);
            var floor = catalog.Despair.Floors[progress.NextLayer - 1];
            despair = DungeonDespairEntryPolicy.Prepare(dungeonId, expected, day, progress,
                repository.LoadSpecialEntryUsage(owner, expected, day, DungeonSpecialEntryKind.Despair),
                catalog.Despair, catalog.Get(floor.DungeonId), repository.ReadAdventureLevel(owner));
            if (despair is null) return RejectFormal("despair-entry-count-or-progress-rejected");
            optionDungeon = checked((uint)catalog.Despair.RootDungeonId);
            dungeonId = checked((uint)despair.DungeonId);
        }
        if ((dungeonId == 7142) != classExperience || classExperience && (questId != 0 || difficulty != 0 || retry is not null ||
            selection is not { IsClassExperience: true } || selection.SelectionEpoch != epoch || selection.CharacterId != expected.Id ||
            selection.OriginPosition != expected.Position)) return RejectFormal("class-experience-selection-mismatch");
        var done = QuestEligibility.Completed(expected);
        var flow = tutorials?.Get(expected.Job);
        bool training = expected.Tutorial is { Completed: false } pending && pending.DungeonId == dungeonId &&
            flow?.DungeonId == dungeonId && questId == 0 && difficulty == 0 &&
            tutorials!.Owns(expected.Job, dungeonId, catalog.Get(checked((int)dungeonId)));
        bool trainingRoom = dungeonId == 5000 && questId == 0 && difficulty == 0;
        if (practice && (training || trainingRoom || classExperience || hellParty || hellLegend || sealDoor || assault is not null || questId != 0 ||
            owner.Execute(() => HasPendingQuestReplay))) return RejectFormal("practice-mode-conflict");
        if (practice && retry is null && (selection is null || selection.IsClassExperience || selection.IsTutorial ||
            selection.SelectionEpoch != epoch || selection.CharacterId != expected.Id || selection.OriginPosition != expected.Position))
            return RejectFormal("practice-selection-missing");
        if (!training && !trainingRoom && !classExperience && SelectionRejection(expected, done, optionDungeon) is { } selectionReason)
            return RejectFormal(selectionReason);
        // 频道专属城镇（最后防线）的选图副本要求持有 dgn [required item]；次数与第三列消耗见 EntryCost。
        bool channelTown = !training && !trainingRoom && !classExperience && assault is null && !hellParty &&
            ChannelAreaLanding.IsChannelBoundTown(towns, expected.Position.Town);
        // 悲叹/绝望之塔与祭坛的入场券、邀请函由各自准入策略规划，不再按频道城镇 [required item] 先行拒绝。
        bool specialAdmission = grief is not null || despair is not null || dungeonId is 11006 or 11007;
        if (channelTown && !specialAdmission &&
            ChannelTownDungeonRequirement.Check(catalog.Get(checked((int)dungeonId)), expected.Inventory, pieces, DateTimeOffset.UtcNow) is { } missingItem)
            return RejectFormal(missingItem);
        // DungeonIncountInfo 次数：任意城镇、非练习的普通入场（含任务与结算再挑战）；新手训练、训练室、职业体验、强袭、深渊派对不计。
        // 悲叹之塔、绝望之塔与血之/究极祭坛已由各自专表（账号日额度、特殊入场账本）计次扣费，这里不再叠加一次。
        DungeonEntryCost? channelCost = null;
        if (!practice && !training && !trainingRoom && !classExperience && assault is null && !hellParty &&
            !specialAdmission)
        {
            channelCost = EntryCost(expected, dungeonId, catalog.Get(checked((int)dungeonId)), pieces, channelTown, out string? costRejection);
            if (costRejection is not null) return RejectFormal(costRejection);
        }
        // 实验性（未在95客户端实证；依据参考项目 dungeon_hell_party_entry.go）：深渊派对入场条件 = 所在选图区域 [hell dungeon] 为真
        // 且该区域 [hell quest] 列表全部已完成（列表为空视为满足）。拒绝原因以 "hell-party-" 前缀写入 LastFormalDungeonRejection，由 handler 回拒绝包。
        // 票据在下面统一规划并随实例安装原子提交；组队规则未接入。
        if (hellParty)
        {
            if (retry is not null && !retry.Entry.IsCosmo || training || trainingRoom || classExperience || assault is not null || questId != 0 ||
                sealDoor && (hellLegend || dungeonId == DungeonCosmoRewardCatalog.DungeonId))
                return RejectFormal("hell-party-mode-unsupported");
            if (!towns.Areas.TryGetValue((expected.Position.Town, expected.Position.Area), out var hellArea) || hellArea.DungeonWorldMap is not { } hellWorld ||
                !towns.DungeonMaps.TryGetValue(hellWorld, out var hellMap) || hellMap.Error is not null ||
                !(sealDoor || (hellLegend ? hellMap.HellLegendDungeon : hellMap.HellDungeon)))
                return RejectFormal("hell-party-area-unavailable");
            // 实验性：密门（普通入场里按概率换成深渊房间）不查深渊任务；传奇深渊查 [hell legend quest]（6081）。
            var missingHellQuests = sealDoor ? [] : (hellLegend ? hellMap.HellQuests.Concat(hellMap.HellLegendQuests) : hellMap.HellQuests)
                .Where(id => !done.Contains(id)).ToArray();
            if (missingHellQuests.Length > 0) return RejectFormal("hell-party-quest-required:" + missingHellQuests[0]);
            if (dungeonId == DungeonCosmoRewardCatalog.DungeonId)
            {
                var cosmoDefinition = catalog.Get(checked((int)dungeonId));
                if (!cosmoDefinition.IsCosmoDungeon || cosmoDefinition.CosmoQuestId <= 0 || !done.Contains(cosmoDefinition.CosmoQuestId))
                    return RejectFormal("hell-party-quest-required:10112");
            }
            if (hellLegend && (selection is null || selection.SelectionEpoch != epoch || selection.CharacterId != expected.Id ||
                selection.HellLegendDungeonId != dungeonId)) return RejectFormal("hell-party-legend-selection-mismatch");
        }
        if (hellParty && !hellLegend && !sealDoor && selection?.HellLegendDungeonId == dungeonId)
            return RejectFormal("hell-party-legend-selection-mismatch");
        StoryQuest? quest = null;
        if (questCatalog is not null && questId != 0)
        {
            if (questId >= 30000 || !questCatalog.TryGet((int)questId, out quest) ||
                !QuestCoverage.HasOrdinaryDeclarations(quest) ||
                !QuestEligibility.Evaluate(expected with { ActiveMainQuests = expected.ActiveMainQuests.Where(row => row.QuestId != questId).ToArray() },
                    quest, questCatalog).Eligible) return RejectFormal("quest-not-eligible-or-undeclared");
        }
        bool allowClearedQuest = questId is > 0 and < 30000 &&
            repository.LoadQuestClear(owner, expected, (ushort)questId) is not null;
        if (!DungeonEntryPlan.TryCreate(catalog, expected, dungeonId, questId, difficulty, out var entry, out var entryReason, allowClearedQuest, quest,draw,questCatalog,
            allowClearedTerminalQuest: questId == 0 ? HasOriginalTerminalClear : null,
            hellParty: hellParty,hellLegend: hellLegend,practice: practice,
            // 频道专属城镇的选图副本（超时空之战 400000016 的 [boss map] 6 0 只写 `map`）按客户端规则把 Boss 坐标格当 Boss 图；其他副本不变。
            bossCellFromBossMap: channelTown && questId == 0 && !practice && !specialAdmission,grief:grief))
            return RejectFormal(hellParty ? "hell-party-entry-plan:" + entryReason : "entry-plan:" + entryReason);
        if (hellParty && entry!.HellParty is null && !entry.IsCosmo) return RejectFormal("hell-party-room-unavailable");
        HellPartyTicket? hellTicket = null;
        HellPartyTicket? legendTicket = null;
        var cosmoPasses = entry!.IsCosmo && items is not null ? items.CosmoPasses() : [];
        var cosmoDefinitionForCost = entry.IsCosmo ? catalog.Get(checked((int)dungeonId)) : null;
        // 密门不收费；星空裂缝在原深渊邀请函之外消耗客户端指定的灵石，数量来自迷宫声明。
        if (hellParty && !sealDoor)
        {
            // 实验性：入场票。未配置 EpicPieceCatalog 时无法规划消耗，保守拒绝（不免费放行）。
            var hellTickets = towns.Areas.TryGetValue((expected.Position.Town, expected.Position.Area), out var ticketArea) && ticketArea.DungeonWorldMap is { } ticketWorld &&
                towns.DungeonMaps.TryGetValue(ticketWorld, out var ticketMap) ? ticketMap.HellItemConditions : [];
            var dungeonDefinition = catalog.Get(checked((int)dungeonId));
            int ticketCount = entry!.IsCosmo ? dungeonDefinition.CosmoInvitationCount :
                HellPartyEntryCost.RequiredCount(entry.HellParty!, dungeonDefinition.MinimumLevel);
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var pass = entry.IsCosmo && pieces is not null ? HellPartyEntryCost.CosmoPass(expected.Inventory, cosmoPasses, pieces, now) : (null, null, null);
            bool usesPass = pass.Item1 is not null;
            var ticketPlan = usesPass ? (pass.Item1, pass.Item3) : pieces is null ? (null, null) :
                HellPartyEntryCost.Plan(expected.Inventory, hellTickets, ticketCount, pieces, now);
            if (ticketPlan.Item1 is null) return RejectFormal("hell-party-item-required");
            hellTicket = ticketPlan.Item1;
            legendTicket = usesPass ? pass.Item2 : null;
            if (!usesPass && (hellLegend || entry.IsCosmo))
            {
                int count = entry.IsCosmo ? dungeonDefinition.CosmoStoneCount : entry.HellParty!.LegendRequiredCount;
                var stones = pieces is null || ticketPlan.Item2 is null ? (null, null) :
                    HellPartyEntryCost.Plan(ticketPlan.Item2.After, [HellPartyEntryCost.LegendStone], count, pieces, now);
                if (stones.Item1 is null) return RejectFormal("hell-party-item-required");
                legendTicket = stones.Item1;
            }
            // 实验性：档位（A..D）= 请求没点名时按 etc/hellparty.etc [difficulty] 的 Probability 加权随机（95 PVF 里只有 A 有权重，所以实际总是 A）。
            if (!entry.IsCosmo) entry = entry with { HellPartyMode = rooms.HellParty.ResolveManualMode(RandomNumberGenerator.GetInt32) };
        }
        // #233 强袭材料走自己的入场消耗；不叠加 [required item] / 入场一次性疲劳。
        if (assault is not null) entry = entry! with { EntryItem = null, StartFatigue = 0, EntryCostNote = null };
        DungeonSpecialEntry? specialEntry = despair?.Entry;
        if (despair is not null) entry = entry! with { EntryItem = despair.Ticket };
        if (dungeonId is 11006 or 11007)
        {
            if (questId != 0 || difficulty != 0 || practice || hellParty || classExperience || assault is not null)
                return RejectFormal("altar-mode-conflict");
            int day = ShopPurchaseCount.Today((timeProvider ?? TimeProvider.System).GetUtcNow());
            var kind = dungeonId == 11006 ? DungeonSpecialEntryKind.Blood : DungeonSpecialEntryKind.Ultimate;
            var usage = repository.LoadSpecialEntryUsage(owner, expected, day, kind);
            specialEntry = DungeonAltarEntryPolicy.Prepare(expected, catalog.Get((int)dungeonId), day, usage, catalog.AltarAdmission);
            if (specialEntry is null) return RejectFormal("altar-entry-count-or-cost-rejected");
        }
        if (entry!.EntryItem is { } entryItem)
        {
            // #510/#511 普通入场票：准入阶段只按当前背包规划，不写库；入场提交时重算并与尝试号同事务扣除。
            if (pieces is null) return RejectFormal("entry-item-catalog-unavailable");
            var entrySpent = InventoryMaterialConsumptionService.Plan(expected.Inventory,
                [new ItemMaterialCost(entryItem.ItemId, entryItem.Count)], pieces, DateTimeOffset.UtcNow);
            if (!entrySpent.Accepted || entrySpent.Plan is null) return RejectFormal("entry-item-required:" + entryItem.ItemId);
        }
        if (entry.QuestId != 0 && entry.QuestAcceptanceId == Guid.Empty)
            return RejectFormal("quest-acceptance-missing");
        if (entry.IsClassExperience != classExperience) return RejectFormal("class-experience-mismatch");
        if (entry.IsTutorial && !training) return RejectFormal("tutorial-entry-not-training");
        if (questId == 0 && entry.RouteQuestId > 0 && questCatalog is not null &&
            expected.ActiveMainQuests.Any(row => row.QuestId == entry.RouteQuestId) &&
            !QuestEligibility.Evaluate(expected with { ActiveMainQuests = expected.ActiveMainQuests.Where(row => row.QuestId != entry.RouteQuestId).ToArray() },
                questCatalog.Get((int)entry.RouteQuestId),questCatalog).Eligible) return RejectFormal("route-quest-not-eligible");
        var prepared = PrepareFormalDungeonResources(expected,epoch,entry!,rooms,drops,items,draw,pieces,prepareDrops,
            training ? flow!.Destination : null,tutorialScenes,crackRewards);
        if (prepared is not null && entry.Maze.IsGriefTower)
            prepared = prepared with { Dungeon = prepared.Dungeon with { GriefItems = new(catalog.GriefTower), GriefAdmission = grief } };
        // 只有正式入场计入冒险团每周地下城信息：练习、训练、职业体验与比赛不消耗次数。
        WeeklyDungeonEntry? weekly = null;
        if (!entry.IsPractice && !entry.IsTutorial && !entry.IsClassExperience && !entry.IsTournament && !entry.IsTrainingRoom &&
            WeeklyDungeonPolicy.SlotOf(catalog.Get(checked((int)dungeonId))) is { } weeklySlot)
            weekly = new(WeeklyDungeonPolicy.WeekKey((timeProvider ?? TimeProvider.System).GetUtcNow()), weeklySlot);
        return prepared is null ? null : prepared with { ClassExperienceSelection = classExperience ? selection : null, Weekly = weekly,
            // 普通入场票已覆盖相同物品时，频道代价只保留次数；超星空通行证规划字段仍完整传入提交阶段。
            Dungeon = (assault is null ? prepared.Dungeon : prepared.Dungeon with { Assault = assault.Selection, AssaultRewards = assault.Rewards }) with { HellTicket = hellTicket, LegendTicket = legendTicket, HellPieces = pieces, CosmoPasses = cosmoPasses,
                CosmoInvitationCount = cosmoDefinitionForCost?.CosmoInvitationCount ?? 0, CosmoStoneCount = cosmoDefinitionForCost?.CosmoStoneCount ?? 0, EntryPieces = pieces, SpecialEntry = specialEntry,
                ChannelCost = channelCost is { Consume: not null } && entry.EntryItem is not null ? channelCost with { Consume = null } : channelCost } };

        // 普通0只在选中显式末层目标后核对该原接取，不按请求0查库或给裸state0补资格。
        bool HasOriginalTerminalClear(ActiveMainQuest original) =>
            repository.LoadQuestClear(owner,expected,original.QuestId) is { } clear &&
            clear.QuestId == original.QuestId && clear.QuestSlot == original.Slot && clear.AcceptanceId == original.AcceptanceId &&
            clear.DungeonId == dungeonId && questCatalog is not null && questCatalog.TryGet(original.QuestId,out var targetQuest) &&
            QuestClearEligibility.IsSingleMap(targetQuest) && clear.MapId == targetQuest.IntData[0];
    }

    PreparedFormalDungeon? PrepareFormalDungeonResources(Character expected,long epoch,DungeonEntryPlan entry,
        DungeonRoomCatalog rooms,DungeonMonsterDropCatalog? drops,ItemCatalog? items,Func<int,int>? draw,EpicPieceCatalog? pieces,
        Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops = null,
        Location? tutorialReturn = null, TutorialScenes? tutorialScenes = null,
        CrackDimensionRewardServices? crackRewards = null)
    {
        if (expected.DungeonState is not { } state) return RejectFormal("resources-no-dungeon-state");
        state.Validate();
        // 保留 #693 的兼容游标和溢出重试；这个数值只选择新实例编号范围，不恢复旧房间或地面对象。
        // 只有完整准备超界才重试，沿用实例隔离的历史账本和单调尝试号。
        uint first = state.NextObjectKey;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return PrepareFormalDungeonKeyRange(expected, epoch, entry, rooms, drops, items, draw, pieces,
                    first, prepareDrops, tutorialReturn, tutorialScenes, crackRewards);
            }
            catch (DungeonObjectKeyOverflowException)
            {
                if (first == CharacterDungeonState.FirstObjectKey) break;
                first = CharacterDungeonState.FirstObjectKey;
            }
        }
        return RejectFormal("instance-object-key-capacity-exceeded");
    }

    PreparedFormalDungeon? PrepareFormalDungeonKeyRange(Character expected,long epoch,DungeonEntryPlan entry,
        DungeonRoomCatalog rooms,DungeonMonsterDropCatalog? drops,ItemCatalog? items,Func<int,int>? draw,EpicPieceCatalog? pieces,
        uint firstObjectKey,
        Func<DungeonEntryPlan, Func<DungeonPlannedMonster, DungeonDropGenerationPlan>>? prepareDrops,
        Location? tutorialReturn, TutorialScenes? tutorialScenes, CrackDimensionRewardServices? crackRewards)
    {
        if (expected.DungeonState is not { } state) return RejectFormal("resources-no-dungeon-state");
        state.Validate();
        if (entry.RequiresFatigueOwner && expected.Fatigue is null ||
            entry.UsesFatigue && FatigueForClient(expected).Remaining == 0) return RejectFormal("fatigue-exhausted");
        // #511 [use fatigue only start dungeon] N：余额（客户端可见值与库内值取小）不足 N 时不进图，不扣到负数。
        if (entry.StartFatigue > 0 && debugSettings?.InfiniteFatigue != true &&
            (expected.Fatigue is not { } storedFatigue || storedFatigue.Remaining < entry.StartFatigue ||
                FatigueForClient(expected).Remaining < entry.StartFatigue))
            return RejectFormal("start-fatigue-insufficient:" + entry.StartFatigue);
        var ordinaryReturn = entry.IsTrainingRoom || entry.IsClassExperience ? expected.Position :
            entry.IsTutorial ? tutorialReturn : entry.IsOrdinary ? towns.SelectionReturn(expected.Position, expected.CompletedQuests.ToHashSet()) : null;
        if (entry.IsOrdinary && ordinaryReturn is null) return RejectFormal("ordinary-return-location-missing");
        QuestDropResources? questDrops = null;
        if (!entry.IsTutorial && !entry.IsClassExperience && !entry.IsPractice && questCatalog is not null && items is not null && pieces is not null)
        {
            try { questDrops = QuestDropResources.Create(expected,entry,questCatalog,items,pieces,contracts?.Growth); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException) { return RejectFormal("quest-drop-resources-unsupported"); }
        }
        if (state.LastAttempt == long.MaxValue) return RejectFormal("attempt-overflow");
        if (firstObjectKey > ushort.MaxValue) throw new DungeonObjectKeyOverflowException("对象key尾部已耗尽");
        var topology = entry!.Maze.StaticTopology;
        if (!topology.Supported) return RejectFormal("maze-topology-unsupported");
        // GM伴侣：王富贵（20900）与光兵28号（5409）两个开关默认关闭（可各自临时开启）。
        // 只作用于正式副本流程，训练/职业体验/练习/比赛不注入；祭坛、绝望之塔与眩惑之塔按资源标记 AllowsCompanionAi 整类排除。
        // 深渊派对与宇宙裂缝也放行；地图自带原生 AI 演员的房间同样叠加。
        // 放行集合冻结进入场计划，只在起始房间发送：客户端会让该AIC跨房间跟随，逐房重发会每房多出一名并卡清房。
        if (!entry.IsTutorial && !entry.IsTrainingRoom && !entry.IsClassExperience && !entry.IsPractice &&
            !entry.IsTournament && entry.AllowsCompanionAi)
        {
            var companionRows = DungeonCompanion.EnabledRows(debugSettings?.SummonWangFugui == true, debugSettings?.SummonGuangbing == true);
            if (!companionRows.IsEmpty) entry = entry with { CompanionMapRows = companionRows };
        }
        var plans = new List<DungeonMonsterPlan>();
        uint next = firstObjectKey;
        uint mapSeed = checked((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue));
        try
        {
            foreach (var map in entry.Maze.MapSpecification!.Concat(entry.Maze.DummyStages)
                .Concat(entry.Maze.LayeredMaps).Concat(entry.Maze.ConditionalMaps).Distinct())
            {
                if (next > ushort.MaxValue) throw new DungeonObjectKeyOverflowException("整条路线对象key超界");
                var room = rooms.Get(map, requirePassiveDefinitions: false, allowUnresolvedSpecialMetadata: entry.IsTutorial);
                if (entry.IsTutorial) room = (tutorialScenes ?? throw new InvalidDataException("训练动作目录未装配")).Apply(room);
                // 实验性（#259）：比赛副本在唯一比赛图上追加四轮对手（依据来自 Go 项目反汇编，未经 V31 客户端验证）。
                var plan = entry.IsAltar
                    ? DungeonMonsterPlan.FromAltarRoom(room, expected.Id, checked((ushort)next),
                        entry.BasisLevel ?? throw new InvalidDataException("祭坛缺少基础等级"), draw ?? RandomNumberGenerator.GetInt32)
                    : entry.IsTournament && entry.BasisLevel is { } tournamentLevel
                    ? DungeonTournamentPlan.CreateRoomPlan(rooms,room,rooms.ReadTournamentSource(map),mapSeed,expected.Id,checked((ushort)next),
                        tournamentLevel,entry.PlayerTeam)
                    : DungeonMonsterPlan.FromRoom(room, expected.Id, checked((ushort)next), entry.BasisLevel,
                        includeSpecial: !entry.IsTutorial, playerTeam: entry.PlayerTeam,
                        // 伴侣行只进起始房间：客户端会让该AIC跨房间跟随，逐房间重发会每房多出一名并卡清房。
                        companionRows: map.Id == entry.StartMap.Id ? entry.CompanionMapRows : []);
                if (entry.ChampionPercents.Length > 0)
                    plan = plan.WithChampionRolls(entry.ChampionPercents, entry.Difficulty, RandomNumberGenerator.GetInt32);
                // 实验性（未在95客户端实证）：深渊派对房间（[seal door pos] 处换成 [seal door map index] 深渊图）抽波次。
                if (entry.HellParty is { } hellRoom && map.Position == hellRoom.Room && map.Id == hellRoom.MapId)
                    plan = plan.WithHellWaves(rooms.ResolveHellWaves(room, entry.HellPartyMode, entry.BasisLevel, RandomNumberGenerator.GetInt32), expected.Id,
                        rooms.HellParty.RewardRollCount(entry.HellPartyMode));
                plans.Add(plan);
                if (plan.Monsters.Count > 0 || plan.SpecialObjects.Count > 0 || plan.HellWaves.Count > 0) next = plan.NextObjectKey;
            }
            if (entry.IsAltar && (plans.Any(plan => plan.Altar is null || plan.Altar.Definition.Ultimate != (entry.DungeonId == 11007)) ||
                plans.Sum(plan => plan.Altar!.Rounds.Select(row => row.Round).Distinct().Count()) != (entry.DungeonId == 11006 ? 15 : 7)))
                return RejectFormal("altar-room-round-contract-mismatch");
        }
        catch (Exception error) when (error is not DungeonObjectKeyOverflowException &&
            error is InvalidDataException or NotSupportedException or FileNotFoundException or OverflowException) { return RejectFormal("room-data-unsupported"); }
        var pools = new Dictionary<int, DungeonExplicitDropPool>();
        var dropPlans = ImmutableDictionary.CreateBuilder<ushort, DungeonDropGenerationPlan>();
        var hellRewardPlans = ImmutableDictionary.CreateBuilder<ushort, DungeonDropGenerationPlan>();
        var groundReservations = new List<DungeonGroundReservation>();
        if ((drops is null) != (items is null)) throw new InvalidDataException("掉落资源依赖必须同时提供");
        // 比赛副本 [disable drop item]，不准备掉落。
        if (drops is not null && items is not null && !entry.IsClassExperience && !entry.IsTournament && !entry.IsPractice && !entry.IsAltar)
        {
            var prepareMonsterDrops = prepareDrops?.Invoke(entry);
            foreach (var plan in plans)
            foreach (var monster in plan.Monsters.Where(monster =>
                monster.HellWave is not null && monster.HellRewardRolls > 0 && monster.Source is null ||
                monster.IsBlocking && monster.Source is not null &&
                (prepareDrops is not null || monster.MonsterId is 1 or 2 && monster.SpawnType == 0)))
            {
                // 资源准备在owner锁外；每个真实monster保留自己的等级、类型、难度及完整数量规则。
                DungeonDropGenerationPlan? dropGeneration;
                try { dropGeneration = prepareMonsterDrops?.Invoke(monster); }
                catch (Exception error) when (error is NotSupportedException or FileNotFoundException) { return RejectFormal("drop-plan-unsupported"); }
                if (prepareDrops is not null && dropGeneration is null)
                    throw new InvalidDataException("掉落规则准备不能以空引用替代未支持的怪物");
                DungeonDropGenerationPlan? rewardGeneration = null;
                if (dropGeneration is not null && monster.HellRewardRolls > 0)
                {
                    // 波次怪：普通计划 = 不带奖励骰；带奖励计划另存，死亡时若是本波次最后一只才用（地面槽位按带奖励计划的上限预留）。
                    rewardGeneration = dropGeneration;
                    try { dropGeneration = prepareMonsterDrops?.Invoke(monster with { HellRewardRolls = 0 }); }
                    catch (Exception error) when (error is NotSupportedException or FileNotFoundException) { return RejectFormal("drop-plan-unsupported"); }
                    hellRewardPlans[monster.ObjectKey] = rewardGeneration;
                }
                if (dropGeneration is not null) dropPlans.Add(monster.ObjectKey, dropGeneration);
                if (prepareDrops is null && !pools.ContainsKey(monster.MonsterId))
                    pools.Add(monster.MonsterId, DungeonExplicitDropPolicy.CreatePool("monster/monster.lst", drops.Get(monster.MonsterId), items.Get));
                // 创建位置是已证资源位置；不将首次受击坐标冒充死亡瞬间位置。
                // 地面RAW不携带最终库存目的槽，独立对象使用显式临时record slot0。
                // 剧情对象可生成在负坐标，位置按协议的16位字段保留，不作为入场资格。
                for (int ordinal = 0; ordinal < Math.Max(1, rewardGeneration?.MaximumObjects ?? dropGeneration?.MaximumObjects ?? 1); ordinal++)
                {
                    if (next == expected.Id) next++;
                    if (next > ushort.MaxValue) throw new DungeonObjectKeyOverflowException("地面对象预留超界");
                    ushort groundX = 0, groundY = 0;
                    if (monster.Source is { } source)
                    {
                        groundX = unchecked((ushort)source.RawValues[3]);
                        groundY = unchecked((ushort)source.RawValues[4]);
                    }
                    groundReservations.Add(new(monster.ObjectKey, next++, plan.Room.Map, groundX, groundY, 0, ordinal));
                }
            }
        }
        else if (prepareDrops is not null && !entry.IsClassExperience && !entry.IsTournament && !entry.IsPractice && !entry.IsAltar)
            throw new InvalidDataException("集合掉落生成计划必须同时装配实际物品依赖");
        var route = new DungeonFixedRoute(Array.AsReadOnly(plans.ToArray()),
            Array.AsReadOnly(topology.Links!.Select(link => new DungeonRouteConnection(link.First, link.Second)).ToArray()));
        // 原生clear condition/destroy object独立发送39，不要求OBJ另声明quest type。
        if (entry.Maze.ClearConditions is [{ Type: "destroy object", Count: 1 } condition] && questCatalog is not null &&
            expected.ActiveMainQuests.Any(active => active.QuestId == entry.RouteQuestId && QuestHuntProgress.Bound(active, entry)))
        {
            var sources = plans.SelectMany(plan => plan.Room.PassiveObjects.Where(source => source.ObjectId == condition.TargetId &&
                plan.Room.PassiveObjects.Count(other => other.ObjectId == source.ObjectId) == 1)).ToArray();
            if (sources.Length > 0)
                route = route with { PassiveClearTarget = condition.TargetId };
        }
        var authority = new NativeDungeonCombatAuthority();
        var committedEntry = entry with { CharacterRevision = checked(expected.Revision + 1) };
        if (!SinglePlayerDungeonRun.TryCreate(committedEntry, state.LastAttempt + 1, epoch, route, authority, out var run, out var runReason))
            return RejectFormal("run-create:" + runReason);
        if (!authority.BindTo(run!)) return RejectFormal("run-authority-bind-failed");
        var passiveGrounds = new List<DungeonGroundObject>();
        if (passiveDrops is not null && !entry.IsAltar)
        {
            try
            {
                foreach (var plan in plans)
                foreach (var prepared in passiveDrops.Prepare(entry, plan, draw ?? RandomNumberGenerator.GetInt32, DateTimeOffset.UtcNow))
                {
                    var eventId = Guid.NewGuid();
                    for (int ordinal = 0; ordinal < prepared.Contents.Length; ordinal++)
                    {
                        if (next == expected.Id) next++;
                        if (next > ushort.MaxValue) throw new DungeonObjectKeyOverflowException("被动掉落预留超界");
                        var source = prepared.Parent.Source.RawValues;
                        passiveGrounds.Add(new(eventId, run!.Snapshot.Identity,
                            new(prepared.Parent.ObjectKey, next++, prepared.Map, unchecked((ushort)source[1]),
                                unchecked((ushort)source[2]), 0, ordinal), prepared.Contents[ordinal])
                        {
                            PassiveOrigin = prepared.Origin,
                            ResourceIdentity = prepared.ResourceSource,
                            UsesCollectionLedger = true
                        });
                    }
                }
            }
            catch (Exception error) when (error is not DungeonObjectKeyOverflowException &&
                error is InvalidDataException or NotSupportedException or FileNotFoundException or OverflowException)
            { return null; }
        }
        var ground = new DungeonGroundState(run!, route.Rooms, groundReservations, drops is null ? "" : "monster/monster.lst", passiveGrounds);
        var dungeon = new FormalDungeon(run!.Entry, route, run, authority, mapSeed,
            ground, pools, draw ?? RandomNumberGenerator.GetInt32, prepareDrops, dropPlans.ToImmutable())
            { NextObjectKey = next, QuestDrops = questDrops, OrdinaryReturnPosition = ordinaryReturn, HellRewardPlans = hellRewardPlans.ToImmutable(),
                CrackRewards = crackRewards, AltarStartedTimestamp = (timeProvider ?? TimeProvider.System).GetTimestamp() };
        return new(expected,epoch,dungeon,next);
    }

    // #510/#511 选图准入逐项给出具体拒绝原因（只写 LastFormalDungeonRejection 与日志，协议回包仍是 SelectionRejected）。
    // 判定与原合并条件等价：区域→世界地图→该副本选项（任一选项满足即可）→无条件入口。
    string? SelectionRejection(Character expected, IReadOnlySet<int> done, uint optionDungeon)
    {
        if (!towns.Areas.TryGetValue((expected.Position.Town, expected.Position.Area), out var area)) return "selection-area-missing";
        if (area.DungeonWorldMap is not { } worldMap) return "selection-area-without-world-map";
        if (!towns.DungeonMaps.TryGetValue(worldMap, out var selectionMap)) return "selection-world-map-missing:" + worldMap;
        if (selectionMap.Error is not null) return "selection-world-map-invalid:" + worldMap;
        var options = selectionMap.Options.Where(option => option.DungeonId == optionDungeon).ToArray();
        if (options.Length == 0) return "selection-dungeon-not-listed:" + optionDungeon;
        if (!options.Any(option => option.QuestId == -1 || (option.InProgress ?
                expected.ActiveMainQuests.Any(active => active.QuestId == option.QuestId) : done.Contains(option.QuestId))))
            return (options[0].InProgress ? "selection-quest-in-progress-required:" : "selection-quest-required:") + options[0].QuestId;
        if (!area.Map(done).DungeonGates.Any(gate => gate.Quests.All(done.Contains) && gate.Conditions.Length == 0))
            return "selection-gate-unavailable";
        return null;
    }

    bool CanEnterFormalDungeon(FormalDungeonReceipt? retry) => !HasPendingTownQuestCommit && (retry is null ?
        Phase is SessionPhase.Town or SessionPhase.MapSelect && formalDungeon is null :
        CanPublishFormalDungeon(retry) && CanReplaceSettledDungeon(formalDungeon, retry) && !HasPendingQuestScene && !HasPendingFormalCard);

    FormalDungeonReceipt? InstallFormalDungeon(PreparedFormalDungeon prepared)
    {
        if (HasPendingTownQuestCommit || !Ready || SelectionEpoch != prepared.SelectionEpoch || !ReferenceEquals(Selected,prepared.Character)) return null;
        if (prepared.Dungeon.Entry.IsClassExperience && (prepared.ClassExperienceSelection is not { IsClassExperience: true } ||
            !ReferenceEquals(CurrentDungeonSelection, prepared.ClassExperienceSelection))) return null;
        // 先持久预留本实例key/attempt；通知失败不重新安装或复用原实例身份。
        var character = prepared.Character;
        var installing = prepared.Dungeon;
        if (installing.GriefAdmission is { } grief &&
            grief.Decision.Day != QuestCycle.Day((timeProvider ?? TimeProvider.System).GetUtcNow())) return null;
        if (installing.Entry.IsPractice && (installing.Entry.HellParty is not null || installing.Entry.IsHellLegend ||
            installing.HellTicket is not null || installing.Assault is not null)) return null;
        System.Collections.Immutable.ImmutableArray<InventorySlot> assaultSlots = [];
        if (installing.Assault is { } assault)
        {
            // #233 入场提交才扣强袭材料：授权必须仍是同一份选择，按当前背包重新规划后先提交扣除，再重基入场revision。
            if (assaultAuthorization is not { } authorization || !ReferenceEquals(authorization.Selection, assault) ||
                authorization.CharacterId != character.Id) return null;
            var spent = AssaultModeEntryPolicy.ConsumptionPlan(character.Inventory, assault.Requirement, authorization.Pieces, DateTimeOffset.UtcNow);
            if (!spent.Accepted || spent.Plan is not { } plan) return null;
            character = repository.CommitInventory(owner, character, plan);
            assaultSlots = plan.ChangedSlots;
            var rebased = installing.Entry with { CharacterRevision = checked(character.Revision + 1) };
            installing.Run.RebasePreparedRevision(rebased.CharacterRevision);
            installing = installing with { Entry = rebased };
            assaultAuthorization = null;
        }
        System.Collections.Immutable.ImmutableArray<InventorySlot> hellSlots = [];
        InventoryPlan? entryItems = null;
        if (installing.HellTicket is { } hellTicket)
        {
            if (installing.HellPieces is not { } hellPieces) return null;
            ItemMaterialCost[] costs = installing.LegendTicket is { } stone
                ? [new(hellTicket.ItemId, hellTicket.Count), new(stone.ItemId, stone.Count)]
                : [new(hellTicket.ItemId, hellTicket.Count)];
            var spent = InventoryMaterialConsumptionService.Plan(character.Inventory, costs, hellPieces, (timeProvider ?? TimeProvider.System).GetUtcNow());
            if (!spent.Accepted || spent.Plan is not { } hellPlan) return null;
            entryItems = hellPlan;
            hellSlots = hellPlan.ChangedSlots;
        }
        if (installing.Entry.EntryItem is { } pendingEntryItem && entryItems is null)
        {
            // 次数落库前先按当前背包核对普通入场票，避免票已不足却先记了次数。
            if (installing.EntryPieces is not { } checkPieces) return null;
            var check = InventoryMaterialConsumptionService.Plan(character.Inventory,
                [new ItemMaterialCost(pendingEntryItem.ItemId, pendingEntryItem.Count)], checkPieces, DateTimeOffset.UtcNow);
            if (!check.Accepted || check.Plan is null) return null;
        }
        System.Collections.Immutable.ImmutableArray<InventorySlot> channelSlots = [];
        System.Collections.Immutable.ImmutableArray<DungeonEnterCount> enterCounts = [];
        if (installing.ChannelCost is { } cost)
        {
            // 入场提交时按当前背包重算材料；次数已满或材料不足则整个入场作废。
            InventoryPlan? consumption = null;
            if (cost.Consume is { } consume)
            {
                if (cost.Pieces is null) return null;
                var spent = InventoryMaterialConsumptionService.Plan(character.Inventory,
                    [new ItemMaterialCost(consume.ItemId, consume.Count)], cost.Pieces, DateTimeOffset.UtcNow);
                if (!spent.Accepted || spent.Plan is not { } plan) return null;
                consumption = plan;
            }
            // [sub incount on clear dungeon] 的副本这里只核对不记次，通关结算时记（ChargeIncountOnClear）。
            if (repository.CommitDungeonIncountEntry(owner, character, cost.Incount?.CountKey ?? cost.DungeonId, cost.Period, cost.Incount, consumption,
                cost.Account, increment: !cost.ChargeOnClear) is not { } counted)
                return null;
            character = counted.Character;
            if (cost.Incount is { } row && !cost.ChargeOnClear) enterCounts = [.. DungeonIncountPolicy.Views(row, counted.Usage)];
            if (consumption is not null)
            {
                channelSlots = consumption.ChangedSlots;
                var rebased = installing.Entry with { CharacterRevision = checked(character.Revision + 1) };
                installing.Run.RebasePreparedRevision(rebased.CharacterRevision);
                installing = installing with { Entry = rebased };
            }
        }
        System.Collections.Immutable.ImmutableArray<InventorySlot> entrySlots = [];
        if (installing.GriefAdmission is { Decision.UsesInvitation: true })
        {
            if (installing.EntryPieces is not { } griefPieces || entryItems is not null) return null;
            var spent = InventoryMaterialConsumptionService.Plan(character.Inventory,
                [new ItemMaterialCost(GriefTowerEntryDecision.InvitationItem,1)],griefPieces,
                (timeProvider ?? TimeProvider.System).GetUtcNow());
            if (!spent.Accepted || spent.Plan is not { } griefPlan) return null;
            entryItems = griefPlan;
        }
        if (installing.Entry.EntryItem is { } entryItem)
        {
            if (entryItems is not null) throw new InvalidDataException("普通入场票不能叠加深渊入场费用");
            // #510/#511 入场提交时按最新背包重算入场票；prepare 之后变少则整个入场作废。
            if (installing.EntryPieces is not { } entryPieces) return null;
            var spent = InventoryMaterialConsumptionService.Plan(character.Inventory,
                [new ItemMaterialCost(entryItem.ItemId, entryItem.Count)], entryPieces, DateTimeOffset.UtcNow);
            if (!spent.Accepted || spent.Plan is not { } entryPlan) return null;
            entryItems = entryPlan;
        }
        // 无限疲劳开关与逐房规则一致：不扣余额也不写扣点账本。
        if (installing.SpecialEntry is { } specialEntry)
        {
            if (specialEntry.Day != ShopPurchaseCount.Today((timeProvider ?? TimeProvider.System).GetUtcNow()))
                return null;
            if (specialEntry.Kind != DungeonSpecialEntryKind.Despair)
            {
                if (entryItems is not null) return null;
                entryItems = DungeonAltarEntryPolicy.Payment(character, specialEntry);
                if (entryItems is null) return null;
            }
        }
        ushort entryFatigue = debugSettings?.InfiniteFatigue == true ? (ushort)0 : installing.Entry.StartFatigue;
        Character committed;
        if (entryItems is not null || entryFatigue > 0 || installing.GriefAdmission is not null || installing.SpecialEntry is not null)
        {
            // 入场票、入场一次性疲劳、每周地下城计数与尝试号预留同一写事务：重复请求或提交失败都不会只扣一半。
            committed = repository.CommitDungeonEntry(owner, character, prepared.NextKey,
                new DungeonEntryCharge(entryItems, entryFatigue, installing.Run.Snapshot.Identity, installing.Entry.StartMap.Id,
                    prepared.Weekly,installing.GriefAdmission) { Special = installing.SpecialEntry, DungeonId = checked((int)installing.Entry.DungeonId) });
            if (installing.HellTicket is null) entrySlots = entryItems?.ChangedSlots ?? [];
            if (committed.Revision != installing.Entry.CharacterRevision)
            {
                var rebased = installing.Entry with { CharacterRevision = committed.Revision };
                installing.Run.RebasePreparedRevision(rebased.CharacterRevision);
                installing = installing with { Entry = rebased };
            }
        }
        else committed = repository.ReserveDungeonAttempt(owner,character,prepared.NextKey,prepared.Weekly);
        prepared.Dungeon.Ground.PublishPassiveEntry();
        if (formalDungeon is { } previous)
        {
            if (previous.Run.Return(previous.Run.Snapshot.Identity, Guid.NewGuid()).Outcome != DungeonRunOutcome.Accepted)
                throw new InvalidOperationException("已提交再挑战不能结束已复验的原结算实例");
            previous.Ground.ExpireRun();
        }
        Selected = committed;
        formalDungeon = installing;
        pendingDungeonContinuation = null;
        pendingQuestReplay = null;
        CurrentDungeonSelection = null;
        SetState(SessionPhase.Loading);
        return FormalReceipt() with { AssaultConsumedSlots = assaultSlots, HellConsumedSlots = hellSlots, HellTicket = installing.HellTicket, LegendTicket = installing.LegendTicket,
            EntryItemConsumedSlots = entrySlots, EntryFatigueCharged = entryFatigue, ChannelConsumedSlots = channelSlots, EnterCounts = enterCounts,
            GriefAdmission = installing.GriefAdmission, SpecialEntry = installing.SpecialEntry };
    }

    bool CanReplaceSettledDungeon(FormalDungeon? dungeon, FormalDungeonReceipt receipt) =>
        Phase == SessionPhase.Settlement && dungeon is not null && HasFinishedDungeon(dungeon) &&
        dungeon.Run.Snapshot.Identity == receipt.Snapshot.Identity && !dungeon.Run.HasPendingDeath &&
        receipt.Entry == (dungeon.Entry with { StartMap = dungeon.Run.Snapshot.CurrentMap }) &&
        !dungeon.Run.HasPendingSettlement && dungeon.Run.Snapshot.Identity.Epoch != long.MaxValue &&
        !dungeon.HasPendingFatigue && !dungeon.Ground.HasPendingOperation && pendingQuestCompletion is null;

    public FormalDungeonReceipt? CurrentFormalDungeonScene() => owner.Execute(() =>
        Ready && Selected is not null && formalDungeon is not null &&
        Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement ? FormalReceipt() : null);

    public FormalDungeonReceipt? CompleteFormalDungeonLoading()
    {
        var presentation = owner.Execute(() =>
        {
            if (formalDungeon is not { } dungeon || !dungeon.Run.HasScriptedReturn)
                return (Handled: false,Receipt: (FormalDungeonReceipt?)null);
            if (!CanUseScriptedReturn(dungeon)) return (Handled: true,Receipt: (FormalDungeonReceipt?)null);
            var result = dungeon.Run.CompleteScriptedReturn(dungeon.Run.Snapshot.Identity);
            return (Handled: true,Receipt: result == DungeonRunOutcome.Rejected ? null : FormalReceipt() with
            {
                ScriptedReturnId = dungeon.Run.ScriptedReturnId,
                NavigationReplay = result == DungeonRunOutcome.Duplicate
            });
        });
        if (presentation.Handled) return presentation.Receipt;
        var (expected,dungeon,epoch,identity,entry) = owner.Execute(() =>
            (Ready && Phase == SessionPhase.Loading ? Selected : null, formalDungeon, SelectionEpoch,
                formalDungeon?.Run.Snapshot.Identity,formalDungeon?.Run.Entry));
        if (expected is null || dungeon is null || identity is null || entry is null) return null;
        bool hasMatchingQuest = !entry.IsClassExperience && !entry.IsPractice && questCatalog is not null && expected.ActiveMainQuests.Any(active =>
            QuestHuntProgress.Bound(active,entry) && questCatalog.TryGet(active.QuestId,out var quest) &&
            QuestDungeonClearProgress.CountsInitializations(quest,entry));
        return owner.Execute(() =>
        {
            if (!Ready || !ReferenceEquals(Selected,expected) || SelectionEpoch != epoch || Phase != SessionPhase.Loading ||
                !ReferenceEquals(formalDungeon,dungeon) || dungeon.Run.Snapshot.Identity != identity ||
                dungeon.Run.HasScriptedTransit && ScriptedNavigationBlocked(dungeon) ||
                dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement || identity.Epoch == long.MaxValue) return null;
            if (dungeon.Entry.UsesRoomFatigue && debugSettings?.InfiniteFatigue != true)
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
    {
        if (formalDungeon is not { } dungeon) return false;
        if (dungeon.Run.HasScriptedReturn)
            return CanUseScriptedReturn(dungeon) && dungeon.Run.ConfirmScriptedReturnBase(dungeon.Run.Snapshot.Identity,new(x,y));
        return Ready && Selected is not null && Phase is SessionPhase.Loading or SessionPhase.Dungeon &&
            !HasPendingQuestScene && (!dungeon.Run.HasScriptedTransit || !ScriptedNavigationBlocked(dungeon)) &&
            dungeon.Run.ConfirmLayerBase(dungeon.Run.Snapshot.Identity,new(x,y));
    });

    bool CanUseScriptedReturn(FormalDungeon dungeon) => Ready && Selected is not null &&
        Phase is SessionPhase.Dungeon or SessionPhase.Settlement && !ScriptedNavigationBlocked(dungeon);

    bool ScriptedNavigationBlocked(FormalDungeon dungeon) => dungeon.CharacterDead ||
        dungeon.Ground.HasPendingOperation || dungeon.PendingTutorialClear is not null || HasPendingQuestScene ||
        dungeon.HasPendingFatigue || HasPendingTownQuestCommit || HasPendingFormalCard ||
        dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement;

    public void ConfirmScriptedReturnLoading(FormalDungeonReceipt receipt) => owner.Execute(() =>
    {
        if (receipt.ScriptedReturnId != Guid.Empty && CanPublishFormalDungeon(receipt))
            formalDungeon!.Run.ConfirmScriptedReturnLoading(receipt.Snapshot.Identity,receipt.ScriptedReturnId);
        return true;
    });

    public void ConfirmScriptedReturnPresentation(FormalDungeonReceipt receipt) => owner.Execute(() =>
    {
        if (receipt.ScriptedReturnId != Guid.Empty && CanPublishFormalDungeon(receipt))
            formalDungeon!.Run.ConfirmScriptedReturnPresentation(receipt.Snapshot.Identity,receipt.ScriptedReturnId);
        return true;
    });

    // 3984 的 CMT 2843 末段是 CHANGE MAP。两个 CharacterView 都发 kind1，Boss 死亡后阶段已是结算。
    bool CinematicSettlementLayer(FormalDungeon dungeon, DungeonPoint target)
    {
        var snapshot = dungeon.Run.Snapshot;
        if (dungeon.Run.ClearFact is null || snapshot.CurrentMap.Position != target || snapshot.CurrentMap.Kind == "layered" ||
            dungeon.Entry.RouteQuestId is 0 or >= 30000 || questCatalog is null || Selected is null ||
            !questCatalog.TryGet((int)dungeon.Entry.RouteQuestId,out var quest) ||
            !QuestClientSceneProgress.DungeonCinematic(quest) || quest.CinematicMapId != snapshot.CurrentMap.Id ||
            !quest.Definition!.Dungeons.Any(row => row.DungeonId == dungeon.Entry.DungeonId) ||
            !Selected.ActiveMainQuests.Any(active => active.QuestId == quest.Id && QuestHuntProgress.Bound(active,dungeon.Entry)))
            return false;
        return dungeon.Entry.Maze.LayeredMaps.Count(map => map.Position == target) == 1;
    }

    public DungeonRunResult? OperateFormalRidable(byte operation, uint resourceId, byte mapX, byte mapY) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase != SessionPhase.Dungeon || formalDungeon is not { } dungeon ||
            dungeon.CharacterDead || dungeon.Ground.HasPendingOperation || dungeon.PendingTutorialClear is not null || HasPendingQuestScene ||
            !Selected.ActiveMainQuests.Any(active => active.QuestId == dungeon.Entry.RouteQuestId && QuestHuntProgress.Bound(active,dungeon.Entry)))
            return null;
        return dungeon.Run.OperateRidable(dungeon.Run.Snapshot.Identity,operation,resourceId,new(mapX,mapY));
    });

    public FormalDungeonReceipt? MoveFormalDungeon(byte x, byte y, byte moveKind = 0) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement) || formalDungeon is not { } dungeon || dungeon.CharacterDead ||
            dungeon.Ground.HasPendingOperation || dungeon.PendingTutorialClear is not null || HasPendingQuestScene) return null;
        if (moveKind > 1) return null;
        if (dungeon.Run.HasScriptedTransit && !CanUseScriptedReturn(dungeon)) return null;
        // 上车后放弃或重接任务也不能沿用旧接取的移动资格。
        if (!Selected.ActiveMainQuests.Any(active => active.QuestId == dungeon.Entry.RouteQuestId && QuestHuntProgress.Bound(active,dungeon.Entry)))
            dungeon.Run.CancelRidable();
        var target = new DungeonPoint(x,y);
        if (dungeon.Run.HasScriptedReturn || dungeon.Run.CanPresentScriptedBase(target))
        {
            if (!CanUseScriptedReturn(dungeon) ||
                dungeon.Run.BeginScriptedReturn(dungeon.Run.Snapshot.Identity,target,moveKind) is not { } presentation) return null;
            return presentation.Outcome == DungeonRunOutcome.Rejected ? null : FormalReceipt() with
            {
                Snapshot = presentation.Snapshot,
                ScriptedReturnId = dungeon.Run.ScriptedReturnId,
                NavigationReplay = presentation.Outcome == DungeonRunOutcome.Duplicate
            };
        }
        bool settlementLayer = Phase == SessionPhase.Settlement && moveKind == 1 && CinematicSettlementLayer(dungeon,target);
        if (Phase != SessionPhase.Dungeon && !settlementLayer) return null;
        var result = moveKind == 1 || target == dungeon.Run.Snapshot.CurrentMap.Position && dungeon.Run.Snapshot.CurrentMap.Kind == "layered"
            ? dungeon.Run.MoveLayer(dungeon.Run.Snapshot.Identity,Guid.NewGuid(),target,moveKind == 1,settlementLayer)
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
        if (!Ready || Selected is null || Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement) || formalDungeon is not { } dungeon || dungeon.Ground.HasPendingOperation || dungeon.Run.HasPendingPassiveClear || HasPendingQuestScene)
            return new FormalDungeonDeathResult(DungeonRunOutcome.Rejected);
        uint objectKey = normal?.MonsterKey ?? scripted!.MonsterKey;
        var currentRoom = dungeon.Route.Rooms.Single(room => room.Room.Map == dungeon.Run.Snapshot.CurrentMap);
        var target = currentRoom.Monsters.SingleOrDefault(monster => monster.ObjectKey == objectKey);
        if (target is null && objectKey <= ushort.MaxValue && currentRoom.HellWaves.Any(wave => wave.ObjectKey == objectKey))
        {
            // 实验性：深渊波次怪死亡只登记（回 DIE_MONSTER，无经验/掉落/清房影响）。
            var waveOutcome = dungeon.Run.RetireHellWave((ushort)objectKey);
            return waveOutcome is DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate
                ? new FormalDungeonDeathResult(waveOutcome, waveOutcome == DungeonRunOutcome.Accepted ? FormalReceipt() : null, (ushort)objectKey)
                    { SpecialChildRetired = waveOutcome == DungeonRunOutcome.Accepted }
                : new FormalDungeonDeathResult(waveOutcome);
        }
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
        if (dungeon.Entry.IsClassExperience || dungeon.Entry.IsPractice)
        {
            // 体验和练习只推进本次内存死亡，不写成长、掉落或任务账本。
            var death = dungeon.Run.ConfirmCommittedDeath(pending);
            if (death.Outcome != DungeonRunOutcome.Accepted)
                throw new InvalidOperationException("练习或职业体验死亡不能确认原实例准备");
            SetState(dungeon.Run.Phase == SinglePlayerDungeonPhase.Settlement ? SessionPhase.Settlement : SessionPhase.Dungeon);
            if (death.ClearFact is not null)
                dungeon.ClearTimeMilliseconds = checked((uint)System.Diagnostics.Stopwatch.GetElapsedTime(dungeon.StartedTimestamp).TotalMilliseconds);
            return new FormalDungeonDeathResult(DungeonRunOutcome.Accepted, FormalReceipt(), pending.Death.MonsterKey)
                { ReportedTarget = target, ClearFact = death.ClearFact, CulledMonsterKeys = death.CulledMonsterKeys,
                    BossPositionUpdate = death.BossPositionUpdate };
        }
        // 契约跨到期或提交返回丢失时仍恢复原死亡的奖励与规划时刻，不重算已冻结额度。
        if (dungeon.PendingDeathReward is null)
        {
            var at = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var basis = DungeonKillExperiencePolicy.Plan(dungeon.Run, pending, rewards, Selected.Level);
            var bonuses = PlanExperience(Selected, ExperienceEvent.Kill, basis.Amount, 0, at.ToUnixTimeSeconds(), dungeon.Entry.DungeonId, dungeon.Entry.ExperienceEquipmentModule);
            dungeon.PendingDeathReward = (pending, basis with
                { BaseAmount = basis.Amount, Amount = bonuses.Total, GrowthContractExperience = bonuses.Amount(ExperienceBonusSource.GrowthContract),
                    PotionExperience = bonuses.Amount(ExperienceBonusSource.Potion), Bonuses = bonuses }, at);
        }
        var frozen = dungeon.PendingDeathReward.Value;
        if (!ReferenceEquals(frozen.Death, pending))
            throw new InvalidOperationException("待提交死亡奖励不能借用另一事件的候选");
        var reward = frozen.Reward;
        FrozenDungeonGroundDeath? loot = null;
        if (dungeon.Ground.HasReservation(pending.Death.MonsterKey))
            loot = dungeon.DropPlans.TryGetValue(pending.Death.MonsterKey, out var generation0) &&
                (generation0 = dungeon.HellRewardPlans.TryGetValue(pending.Death.MonsterKey, out var rewardPlan) &&
                    dungeon.Run.IsLastLivingHellWaveMember(pending.Death.MonsterKey) ? rewardPlan : generation0) is { } generation
                ? dungeon.Ground.GetOrFreezeAssets(pending, () =>
                {
                    var now = frozen.At;
                    // 当前正式Run为单人；四档是队内有效成长契约人数，不是难度或怪物品级。
                    int bonus = GrowthContractPolicy.Active(Selected, contracts?.Growth, now.ToUnixTimeSeconds()) &&
                        !dungeon.Entry.IsTutorial && !dungeon.Entry.IsTrainingRoom ? contracts!.Growth!.IndependentDropPercent[0] : 0;
                    return generation.WithGrowthContract(bonus).Generate(dungeon.Draw, now);
                }, generation.ResourceSource)
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
                dungeon.Draw,frozen.At);
        var questDrop = dungeon.PendingQuestDrop;
        var committed = CommitDungeonGrowth(SelectionEpoch, Selected, dungeon.Run, pending, reward, growth, loot, questDrop);
        dungeon.PendingDeathReward = null;
        dungeon.PendingQuestDrop = null;
        if (loot is not null && !dungeon.Ground.PublishCommitted(pending, loot))
            throw new InvalidOperationException("已提交掉落的原准备不能发布");
        SetState(dungeon.Run.Phase == SinglePlayerDungeonPhase.Settlement ? SessionPhase.Settlement : SessionPhase.Dungeon);
        // 满级入口线后角色经验到账为 0，击杀累计改用「角色到账 + 熟练度到账」的有效经验。
        uint effective = checked(committed.Commit.GainedExperience + committed.Commit.ProficiencyGained);
        dungeon.KillExperienceTotal = checked(dungeon.KillExperienceTotal + effective);
        dungeon.KillGrowthContractExperience = checked(dungeon.KillGrowthContractExperience +
            (reward.Bonuses?.Applied(effective).Amount(ExperienceBonusSource.GrowthContract) ??
            GrowthContractPolicy.AppliedBonus(reward.Amount - reward.GrowthContractExperience,
                reward.GrowthContractExperience, effective)));
        // 结果页杀怪侧装备加成行按每只怪实际到账的分项累计；未冻结分项的历史奖励没有该口径，保持 0。
        dungeon.KillEquipmentExperience = checked(dungeon.KillEquipmentExperience +
            (reward.Bonuses?.Applied(effective).Amount(ExperienceBonusSource.Equipment) ?? 0));
        dungeon.KillObjectExperience.Add(new(pending.Death.MonsterKey, reward.BaseAmount));
        // 领主行独立于明细列表：客户端用评分段147槽显示，不取该对象在151/155里的条目。
        if (pending.Monster.IsBoss)
            dungeon.KillBossExperience = checked(dungeon.KillBossExperience + reward.BaseAmount);
        if (committed.Death.ClearFact is not null)
            dungeon.ClearTimeMilliseconds = checked((uint)System.Diagnostics.Stopwatch.GetElapsedTime(dungeon.StartedTimestamp).TotalMilliseconds);
        return new FormalDungeonDeathResult(DungeonRunOutcome.Accepted, FormalReceipt(), pending.Death.MonsterKey, reward,
            committed.Death.ClearFact, loot?.UsesCollectionLedger == true ? null : loot?.Ground, previousLevel)
        {
            Drops = loot?.Grounds ?? [],
            GainedExperience = committed.Commit.GainedExperience,
            ProficiencyGained = committed.Commit.ProficiencyGained,
            ReportedTarget = target,
            PreviousQuests = previousQuests,
            QuestDropSlots = questDrop?.Inventory.ChangedSlots ?? [],
            RoomClearFact = committed.Death.RoomClearFact,
            BossPositionUpdate = committed.Death.BossPositionUpdate,
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
                InventoryRewardGrantService.TryGrantCore(Selected, original.Core, items.Get(original.Core.ItemId), pieces, now, usePickupSlotPolicy: true);
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

    public TownTravelReceipt? ReturnFromFormalDungeon() => ReturnFromFormalDungeonCore(false);

    TownTravelReceipt? ReturnFromFormalDungeonCore(bool clearedHell) => owner.Execute(() =>
    {
        if (!CheckState(UserStateRequirement.DungeonContext) || Selected is null || formalDungeon is not { } dungeon || dungeon.Ground.HasPendingOperation || HasPendingFormalCard || pendingQuestCompletion is not null || dungeon.HasPendingFatigue || HasPendingQuestScene) return null;
        if (dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement) return null;
        if (dungeon.PendingTutorialClear is not null) return null;
        // 普通挑战沿本次入口安全点返回；先提交位置，失败时保留原实例与完整角色资产。
        if (dungeon.Entry.IsTutorial && Selected.Tutorial is { Completed: false })
            Selected = repository.CommitTutorialCheckpoint(owner, TutorialCheckpoint(Selected, "skip", Guid.NewGuid(), 0, 0,
                dungeon.Run.Snapshot.Identity, dungeon.Run.Snapshot.CurrentMap.Id, true)).Character;
        else
        {
            var before = Selected.Inventory;
            var retreatAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var after = clearedHell ? before : StaminaRecoveryService.ApplyDungeonRetreat(Selected, dungeon.Entry, dungeon.Run.Phase, retreatAt,
                PremiumPcRoom);
            after = ConsumableStatePolicy.CrossBoundary(after, ConsumableMaintenance.Town, retreatAt.ToUnixTimeMilliseconds());
            var back = dungeon.OrdinaryReturnPosition ?? Selected.Position;
            if (Selected.Position != back || after != before)
                Selected = repository.Save(owner, Selected, back, Selected.Return,
                    after == before ? null : new InventoryPlan(before, after, "dungeon_retreat_stamina", new(Selected)));
        }
        if (dungeon.Run.Return(dungeon.Run.Snapshot.Identity, Guid.NewGuid()).Outcome != DungeonRunOutcome.Accepted) return null;
        dungeon.Ground.ExpireRun();
        formalDungeon = null;
        CrossConsumableCooldownBoundary(ConsumableMaintenance.Town);
        CancelDungeonContinuation();
        pendingQuestReplay = null;
        SetState(SessionPhase.Town);
        return new TownTravelReceipt(Selected, owner.Generation, SelectionEpoch);
    });

    public bool CanPublishFormalDungeon(FormalDungeonReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        ReferenceEquals(Selected, receipt.Character) && formalDungeon is { } dungeon && dungeon.Run.Snapshot.Identity == receipt.Snapshot.Identity &&
        (receipt.ScriptedReturnId == Guid.Empty || CanUseScriptedReturn(dungeon) && dungeon.Run.MatchesScriptedReturn(receipt.ScriptedReturnId)));

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
        return new(Selected!, owner.Generation, SelectionEpoch, dungeon.Entry with { StartMap = snapshot.CurrentMap }, snapshot, monsters, dungeon.MapSeed)
        {
            PassiveItems = dungeon.Ground.CurrentPassiveItems(),
            ClearTimeMilliseconds = dungeon.ClearTimeMilliseconds
        };
    }
}
