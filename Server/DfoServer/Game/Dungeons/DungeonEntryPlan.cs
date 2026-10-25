using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.Game.Quests;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

// 请求任务身份与实际路线分别冻结；计划不建立实例或证明客户端加载。
// 调用方仍须在有效账号owner下绑定epoch，并在提交时复验原角色快照。
internal sealed record DungeonEntryPlan(
    ushort CharacterId,
    long CharacterRevision,
    uint DungeonId,
    uint QuestId,
    byte ActiveQuestSlot,
    byte Difficulty,
    DungeonMaze Maze,
    DungeonMap StartMap,
    DungeonMap BossMap)
{
    // 实验性（未在95客户端实证）：深渊派对入场（op16 请求 [7]=1）。非空表示本次入场走 maze 的 [hell dungeon] 深渊房间，
    // 并在 DUNGEON_INFO/START_MAP 里带深渊标志；HellPartyMode 取请求标志（参考项目 ResolveHellPartyMode：requestFlag != 0 ? requestFlag : 按难度权重）。
    public DungeonHellPartyRoom? HellParty { get; init; }
    public byte HellPartyMode { get; init; }
    public bool IsHellLegend { get; init; }
    public bool IsCosmo { get; init; }
    public int? BasisLevel { get; init; }
    // 入场冻结的推荐资格不是完成事实；任务仍等待本Run终末与原接取身份。
    public bool RecommendedQuestClear { get; init; }
    public int? PlayerTeam { get; init; }
    // GM伴侣：本次入场放行的地图 [ai character] 行编号。空表示未开启或该副本类型不注入；
    // 只在起始房间发送，非起始房间的行由 DungeonMonsterPlan.FromRoom 丢弃。
    public ImmutableArray<int> CompanionMapRows { get; init; } = [];
    // 资源侧准入：祭坛、塔与亡者峡谷不注入伴侣（DungeonDefinition.AllowsCompanionAi）；
    // 地图自带原生 [ai character] 行的房间照常叠加伴侣行。
    public bool AllowsCompanionAi { get; init; } = true;
    public bool IsTutorial { get; init; }
    public bool DisableClearReward { get; init; }
    public bool DisableMonsterAndClearExperience { get; init; }
    public bool DisableGiveupPenalty { get; init; }
    public int? CoinLimit { get; init; }
    public bool IsAltar => DungeonId is 11006 or 11007;
    public bool IsDespair => DungeonId is >= 11008 and <= 11107;
    public bool DisableFatigueConsumption { get; init; }
    public Guid QuestAcceptanceId { get; init; }
    // 队长已批准的物理路线，只供分层、演出和物件终点核验；个人任务始终读取本人的 QuestAcceptances。
    public DungeonEntryPlan? PartyRoute { get; init; }
    // 带原接取身份的普通路线，含猎杀、收集及列表通关；同样核对普通入场费用。
    public bool IsOrdinaryHunt { get; init; }
    public bool IsOrdinary => !IsTutorial && RouteQuestId == 0 && (QuestId == 0 || IsOrdinaryHunt);
    public bool IsTrainingRoom => DungeonId == 5000 && Maze.IsTrainingRoom;
    public bool IsClassExperience => DungeonId == 7142 && Maze.IsClassExperience;
    public bool IsPractice { get; init; }
    public bool HasFreeCombatConsumption => IsPractice || IsTrainingRoom || IsClassExperience;
    // 实验性（#259）：比赛副本（PVF [tournament dungeon] 1，[no fatigue]），不消耗疲劳。
    public bool IsTournament => Maze.IsTournament;
    public int? GriefLayer { get; init; }
    public bool RequiresFatigueOwner => IsOrdinary && !IsTrainingRoom && !IsClassExperience && !IsTournament && !IsPractice && !Maze.IsGriefTower;
    public bool UsesFatigue => RequiresFatigueOwner && !DisableFatigueConsumption;
    // #511 普通入场的 [use fatigue only start dungeon] N：入场提交时一次扣 N，之后首次房间不再逐房扣点。0 = 沿用逐房一点。
    public ushort StartFatigue { get; init; }
    public OperationHopeAdmission? Hope { get; init; }
    public bool UsesRoomFatigue => UsesFatigue && StartFatigue == 0 && Hope is null;
    // #510/#511 普通入场的 [required item]「物品 1 1」：入场提交时与疲劳、尝试号同事务扣除。
    public DungeonRequiredItemRow? EntryItem { get; init; }
    // 资源声明了入场票或入场疲劳但形状未确认时的诊断，只写日志，不改变行为。
    public string? EntryCostNote { get; init; }
    public string ExperienceEquipmentModule { get; init; } = "dungeon";
    public int? MonsterBasisLevel { get; init; }
    public DungeonMap BaseBossMap { get; init; } = BossMap;
    public bool HasExplicitClearMap { get; init; }
    // 中途任务目标独立冻结，不改变Boss坐标或终末资格。
    public DungeonMap? QuestClearMap { get; init; }
    // 不同坐标的显式末层只覆盖领域终点，BossMap仍保留原NOTI28投影。
    public DungeonMap? TerminalLayerMap { get; init; }
    public ushort TerminalLayerQuestId { get; init; }
    public byte TerminalLayerQuestSlot { get; init; } = byte.MaxValue;
    public Guid TerminalLayerAcceptanceId { get; init; }
    // Run从批准路线冻结的单物件末层，只覆盖领域终点，不改原BossMap的wire投影。
    public DungeonMap? PassiveClearTerminalMap { get; init; }
    // Run从已批准的进图演出换层及末层Boss声明冻结，不改变NOTI28的基础Boss坐标。
    public DungeonMap? CinematicClearTerminalMap { get; init; }
    public uint RouteQuestId { get; init; } = QuestId;
    public ImmutableArray<ActiveMainQuest> QuestAcceptances { get; init; } = [];
    public float ExperienceWeight { get; init; } = 1;
    public IReadOnlyList<int> NamedMonsters { get; init; } = Array.Empty<int>();
    // 断头谷 [champion] 后四项：难度 0–3 把普通怪升为精英的百分比。其它副本保持 0，不改已有生成。
    public int[] ChampionPercents { get; init; } = [];
    public bool IndividualMapMovement { get; init; }
    public bool InitializationCounterSupported { get; init; }
    public bool IsQuestReplay { get; init; }
    public bool IsTerminalMap(DungeonMap map) => Hope is { } hope ? OperationHopePlan.IsDeclaredTerminal(hope.Catalog,hope.MazeOrdinal,map.Id) : Maze.DummyStages.Count > 0 ? map == Maze.DummyStages[^1] :
        PassiveClearTerminalMap is { } passive ? map == passive :
        CinematicClearTerminalMap is { } cinematic ? map == cinematic :
        TerminalLayerMap is { } layer ? map == layer :
        map == BossMap || BossMap.Kind != "layered" && map.IsConditional &&
        map.Position == BaseBossMap.Position && Maze.ConditionalMaps.Contains(map);

    public static bool TryCreateReplay(DungeonCatalog catalog,QuestReplayPlan replay,out DungeonEntryPlan? plan,out string reason)
    {
        plan = null;
        DungeonDefinition definition;
        try { definition = catalog.Get(checked((int)replay.DungeonId)); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
        { reason = "dungeon-resource-unavailable"; return false; }
        return TryCreate(definition,replay.Expected,replay.DungeonId,replay.QuestId,0,out plan,out reason,
            quest: replay.Quest,resolveMaze: maze => catalog.ResolveOrdinaryMaze(definition,maze),isReplay: true);
    }

    public static bool TryCreate(DungeonCatalog catalog, Character character, uint dungeonId, uint questId,
        byte difficulty, out DungeonEntryPlan? plan, out string reason, bool allowClearedQuest = false, StoryQuest? quest = null,
        Func<int,int>? draw = null, QuestCatalog? quests = null, Func<ActiveMainQuest,bool>? allowClearedTerminalQuest = null,
        bool hellParty = false, bool hellLegend = false, bool practice = false, bool bossCellFromBossMap = false, GriefTowerAdmission? grief = null, OperationHopeAdmission? hope = null, DungeonEntryPlan? partyRoute = null)
    {
        plan = null;
        reason = ValidateRequest(character, dungeonId, questId, difficulty, allowClearedQuest, quest);
        if (reason != "resource-request-ready")
            return false;
        DungeonDefinition definition;
        try
        {
            definition = catalog.Get((int)dungeonId);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
        {
            reason = "dungeon-resource-unavailable";
            return false;
        }

        return TryCreate(definition, character, dungeonId, questId, difficulty, out plan, out reason, allowClearedQuest, quest,draw,quests,
            maze => catalog.ResolveOrdinaryMaze(definition,maze,draw,hellParty),allowClearedTerminalQuest:allowClearedTerminalQuest,
            hellParty:hellParty,hellLegend:hellLegend,practice:practice,bossCellFromBossMap:bossCellFromBossMap,grief:grief,hope:hope,partyRoute:partyRoute);
    }

    internal static bool TryCreate(DungeonDefinition definition, Character character, uint dungeonId, uint questId,
        byte difficulty, out DungeonEntryPlan? plan, out string reason, bool allowClearedQuest = false, StoryQuest? quest = null,
        Func<int,int>? draw = null, QuestCatalog? quests = null, Func<DungeonMaze,DungeonMaze>? resolveMaze = null,bool isReplay = false,
        Func<ActiveMainQuest,bool>? allowClearedTerminalQuest = null,bool hellParty = false,bool hellLegend = false,bool practice = false,
        bool bossCellFromBossMap = false, GriefTowerAdmission? grief = null, OperationHopeAdmission? hope = null, DungeonEntryPlan? partyRoute = null)
    {
        plan = null;
        reason = ValidateRequest(character, dungeonId, questId, difficulty, allowClearedQuest, quest,isReplay);
        if (hellLegend && !hellParty) { reason = "hell-legend-requires-hell-party"; return false; }
        if (hellParty && (questId != 0 || isReplay)) { reason = "hell-party-quest-or-replay-unsupported"; return false; }
        if (reason != "resource-request-ready")
            return false;
        if (definition.Id != dungeonId)
        {
            reason = "dungeon-resource-mismatch";
            return false;
        }
        bool cosmo = definition.RequiresCosmoEntryContract;
        if (cosmo)
        {
            if (!hellParty || hellLegend || practice || isReplay || questId != 0)
            { reason = "cosmo-mode-required"; return false; }
            if (!definition.IsCosmoDungeon || definition.CosmoInvitationCount != 40 || definition.CosmoStoneCount != 4 ||
                definition.CosmoQuestId != 10112 || !definition.TryGetDesignatedDifficulty(out byte cosmoDifficulty) || cosmoDifficulty != 4 ||
                definition.StartFatigue != 8)
            { reason = "cosmo-entry-contract-unsupported"; return false; }
        }
        // 悲叹只能由已核对账号额度和当前楼层的专用准入进入，不能借普通入口跳层或叠加两张票。
        if (definition.RequiresGriefEntryContract)
        {
            // 029FFAA0 在定义 +0x619（[no fatigue]）非零时返回不要求疲劳。
            // 0409EC16 / 02A37134 另把 [enter without fatigue] 的非零值抄到实例 +0x84C。
            // 两条都出现但不一致时单独拒绝；都没有时仍走原来的整包拒绝，避免把几何当成准入。
            bool fatigueDeclared = definition.RawSections.Any(row => row.Tag is "no fatigue" or "enter without fatigue");
            if (fatigueDeclared && (!definition.DisableFatigueConsumption || !definition.EnterWithoutFatigue))
            {
                reason = "grief-tower-fatigue-mismatch";
                return false;
            }
            if (grief is not { Decision.Accepted: true } || grief.DungeonId != dungeonId ||
                definition.GriefFloor is not { } floor || floor.Layer != grief.Before.State.Layer ||
                questId != 0 && (quest is null || !QuestDungeonClearProgress.MatchesAlternative(quest,dungeonId,difficulty)) ||
                difficulty != 0 || isReplay || hellParty || hellLegend || practice)
            {
                reason = "grief-tower-admission-required";
                return false;
            }
        }
        else if (grief is not null) { reason = "grief-tower-resource-mismatch"; return false; }
        // 02A00360 只识别类型码 0x54。5×5 网格和把 6,6 搬进格子都不是这条房间合同。
        if (definition.IsBossRushDungeon)
        {
            reason = "boss-rush-room-contract-unproven";
            return false;
        }
        // 实例 +0x7BC 与 0x4A 的唯一比较在 029FFAA0：该类型返回不要求疲劳。
        // 定义 +0x760 只在解析时写成 0x4A，没有第二道类型准入。有票据或疲劳声明对不上时仍拒绝。
        if (definition.IsDazzlementTower &&
            (!definition.DisableFatigueConsumption || !definition.EnterWithoutFatigue || !definition.RequiredItemRows.IsEmpty))
        {
            reason = "dazzlement-tower-entry-unproven";
            return false;
        }
        if (practice && (!definition.AllowsPractice || questId != 0 || isReplay || hellParty || hellLegend || quest is not null))
        { reason = "practice-mode-or-resource-unsupported"; return false; }

        if (dungeonId == 7142 && (!definition.IsClassExperience || questId != 0 || difficulty != 0 || isReplay))
        {
            reason = "class-experience-resource-or-mode-invalid";
            return false;
        }

        if (definition.MinimumLevel is null)
        {
            reason = "dungeon-level-unknown";
            return false;
        }

        // #438: 剧情副本最低等级可能高于关联任务最低等级（根特南门65/任务3524为64）；仅当角色已接取关联本副本的任务且等级达到任务最低等级才放行。
        if (character.Level < definition.MinimumLevel && !QuestEntryWaivesDungeonLevel(definition,character,dungeonId,questId,difficulty,quest,quests,isReplay))
        {
            reason = "dungeon-level-too-low";
            return false;
        }

        if (partyRoute is { } shared)
        {
            if (shared.PartyRoute is not null || shared.DungeonId != dungeonId || shared.Difficulty != difficulty ||
                shared.IsPractice || shared.IsTutorial || shared.IsClassExperience || shared.IsTrainingRoom || shared.IsTournament ||
                shared.IsQuestReplay || shared.Hope is not null || (shared.HellParty is not null || shared.IsCosmo) != hellParty || shared.IsHellLegend != hellLegend)
            { reason = "party-route-mismatch"; return false; }
            var accepted = questId == 0 ? null : character.ActiveMainQuests.Single(row => row.QuestId == questId);
            var participantTerminal = character.ActiveMainQuests.SingleOrDefault(row => row.QuestId == shared.TerminalLayerQuestId);
            plan = shared with
            {
                CharacterId = character.Id, CharacterRevision = character.Revision,
                QuestId = questId, ActiveQuestSlot = accepted?.Slot ?? byte.MaxValue,
                QuestAcceptanceId = accepted?.AcceptanceId ?? Guid.Empty,
                QuestAcceptances = character.ActiveMainQuests.ToImmutableArray(),
                IsOrdinaryHunt = accepted is not null && shared.RouteQuestId == 0,
                TerminalLayerQuestId = participantTerminal?.QuestId ?? 0, TerminalLayerQuestSlot = participantTerminal?.Slot ?? byte.MaxValue,
                TerminalLayerAcceptanceId = participantTerminal?.AcceptanceId ?? Guid.Empty,
                PassiveClearTerminalMap = null, CinematicClearTerminalMap = null, PartyRoute = shared
            };
            reason = "resource-plan-ready";
            return true;
        }
        // 仅识别匹配 PVF 已确认的三个整数；未知引用和其他条件形状不能变成默认迷宫。
        uint routeQuest = !isReplay && quest?.MainQuest is > 0 ? checked((uint)quest.MainQuest.Value) : questId;
        bool ordinaryHunt = false;
        StoryQuest? layoutQuest = quest;
        // 已达成但未交付的寻物、清图或猎杀保留接取身份，再次选图使用真实普通路线。
        // 寻物复核未过期材料；清图要有持久凭据。裸条件0不能绕过剧情路线。
        var finishedObjective = new HashSet<int>();
        if (!isReplay && !practice && !hellParty)
            foreach (var accepted in character.ActiveMainQuests)
            {
                var acceptedDefinition = quest?.Id == accepted.QuestId ? quest :
                    quests is not null && quests.TryGet(accepted.QuestId,out var found) ? found : null;
                if (acceptedDefinition is not null && (CanLeaveSeekingRoute(definition,character,acceptedDefinition,accepted) ||
                    CanLeaveClearedStoryRoute(definition,character,acceptedDefinition,accepted,questId,allowClearedQuest,allowClearedTerminalQuest)))
                    finishedObjective.Add(accepted.QuestId);
            }
        var candidates = DistinctQuestRoutes(definition.Mazes.Where(maze => maze.ConnectsQuest(questId,difficulty)));
        bool finishedObjectiveRequest = questId != 0 && finishedObjective.Contains((int)questId);
        if (practice || finishedObjectiveRequest)
        {
            candidates = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).ToArray();
            if (candidates.Length > 1)
            {
                int index = (draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32)(candidates.Length);
                if (index < 0 || index >= candidates.Length) throw new InvalidDataException("练习迷宫随机索引越界");
                candidates = [candidates[index]];
            }
            if (finishedObjectiveRequest)
            {
                ordinaryHunt = true;
                routeQuest = 0;
                layoutQuest = null;
            }
        }
        else if (questId == 0 && quests is not null)
        {
            candidates = DistinctQuestRoutes(definition.Mazes.Where(maze => character.ActiveMainQuests.Any(active =>
                !finishedObjective.Contains(active.QuestId) && maze.ConnectsQuest(active.QuestId,difficulty))));
            var sequences = new List<(StoryQuest Parent, StoryQuest Child)>();
            if (candidates.Length == 0)
            {
                foreach (var acceptedQuest in character.ActiveMainQuests)
                    if (!finishedObjective.Contains(acceptedQuest.QuestId) &&
                        quests.TryGet(acceptedQuest.QuestId,out var parent) && CurrentSequentialChild(character,parent,quests,dungeonId,difficulty) is { } child &&
                        !finishedObjective.Contains(child.Id))
                        sequences.Add((parent,child));
                candidates = DistinctQuestRoutes(definition.Mazes.Where(maze => sequences.Any(pair => maze.ConnectsQuest((uint)pair.Child.Id,difficulty))));
            }
            if (candidates.Length == 0)
                candidates = DistinctQuestRoutes(definition.Mazes.Where(maze => QuestEligibility.Completed(character).Any(id => maze.ConnectsQuest((uint)id,difficulty,1))));
            if (candidates.Length == 0)
            {
                candidates = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).ToArray();
                if (candidates.Length > 0)
                {
                    int index = candidates.Length == 1 ? 0 : (draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32)(candidates.Length);
                    if (index < 0 || index >= candidates.Length) throw new InvalidDataException("迷宫随机索引越界");
                    candidates = [candidates[index]];
                }
            }
            else if (candidates.Length == 1)
            {
                routeQuest = checked((uint)candidates[0].ConnectionQuestId!.Value);
                if (quests is null || !quests.TryGet((int)routeQuest,out quest) || !QuestCoverage.HasOrdinaryDeclarations(quest))
                { reason = "quest-route-definition-unavailable"; return false; }
                layoutQuest = quest;
                if (sequences.SingleOrDefault(pair => pair.Child.Id == routeQuest) is { Parent: not null } sequence)
                {
                    quest = sequence.Parent;
                    routeQuest = checked((uint)quest.Id);
                }
            }
        }
        else if (candidates.Length == 0 && routeQuest != questId)
            candidates = DistinctQuestRoutes(definition.Mazes.Where(maze => maze.ConnectsQuest(routeQuest,difficulty)));
        else if (questId == 0) candidates = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).ToArray();
        else routeQuest = questId;
        // 父请求保留自己的接取身份；布局和终点只借用真实顺序中的当前子任务。
        if (!isReplay && candidates.Length == 0 && questId != 0 && quest is not null && quests is not null &&
            QuestRelationProgress.SequentialParent(quest,quests))
        {
            layoutQuest = CurrentSequentialChild(character,quest,quests,dungeonId,difficulty);
            if (layoutQuest is null) { reason = "quest-sequential-child-unavailable"; return false; }
            candidates = DistinctQuestRoutes(definition.Mazes.Where(maze => maze.ConnectsQuest((uint)layoutQuest.Id,difficulty)));
        }
        // 转职等清图资源可能只声明 dungeon info，没有任务连接。只借用明确归属的无连接路线，仍由后续终点检查核对目标地图。
        if (candidates.Length == 0 && questId != 0 && quest is not null && QuestClearEligibility.IsSingleMap(quest) &&
            quest.Definition is { Diagnostics.Length: 0 } declared && declared.Dungeons.Any(row => row.DungeonId == dungeonId) &&
            definition.Mazes.All(maze => maze.QuestConnectionTokens.Count == 0))
            candidates = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).ToArray();
        // 普通任务只借用明确归属的普通迷宫；已有本任务连接或未知连接不能降级为普通路线。
        if (!isReplay && candidates.Length == 0 && questId != 0 && quest is not null &&
            CanUseOrdinaryQuestRoute(definition,character,quest,dungeonId,questId,difficulty))
        {
            candidates = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0).ToArray();
            if (candidates.Length > 0)
            {
                int index = candidates.Length == 1 ? 0 : (draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32)(candidates.Length);
                if (index < 0 || index >= candidates.Length) throw new InvalidDataException("迷宫随机索引越界");
                candidates = [candidates[index]];
                ordinaryHunt = true;
                routeQuest = 0;
                layoutQuest = null;
            }
        }
        // 实验性：深渊派对只走普通（无任务连接）且声明了 [hell dungeon] 深渊房间的 maze；不沿用任务/顺序任务/普通狩猎的路线推断。
        if (hellParty)
        {
            candidates = definition.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0 && (cosmo || maze.HellParty is not null) &&
                (cosmo || !hellLegend || maze.HellParty!.LegendMapId > 0 && maze.HellLegendPath is not null)).ToArray();
            ordinaryHunt = false; routeQuest = 0; quest = null; layoutQuest = null;
            if (candidates.Length == 0) { reason = "hell-party-maze-unavailable"; return false; }
            // 同一副本声明了多个带深渊房间的普通 maze（如 291100319 的 313166/313173）：与普通入场一样随机选一个。
            if (candidates.Length > 1)
            {
                int hellIndex = (draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32)(candidates.Length);
                if (hellIndex < 0 || hellIndex >= candidates.Length) throw new InvalidDataException("深渊 maze 抽签越界");
                candidates = [candidates[hellIndex]];
            }
        }
        if (hope is not null)
        {
            if (dungeonId != 8523 || questId != 0 || difficulty != hope.Catalog.Difficulty || isReplay || practice || hellParty ||
                hope.SourceRevision != character.Revision || !hope.Catalog.Mazes.ContainsKey(hope.MazeOrdinal))
            { reason = "operation-hope-admission-mismatch"; return false; }
            candidates = definition.Mazes.Where(row => row.SourceOrdinal == hope.MazeOrdinal && row.QuestConnectionTokens.Count == 0).ToArray();
            routeQuest = 0; layoutQuest = null;
        }
        if (candidates.Length != 1)
        {
            reason = candidates.Length == 0 ? "route-maze-unavailable" : "route-maze-ambiguous";
            if (questId != 0 || quests is not null) reason = candidates.Length == 0 ? "quest-maze-unavailable" : "quest-maze-ambiguous";
            return false;
        }

        DungeonMaze maze;
        try { maze = resolveMaze is null ? candidates[0] : resolveMaze(candidates[0]); }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
        { reason = "dungeon-resource-unavailable"; return false; }
        if (!definition.AllowsDifficulty(difficulty,maze.QuestConnectionTokens.Count != 0,maze.ConnectionQuestId))
        { reason = "difficulty-unsupported"; return false; }
        // 多起点、多Boss是候选集合，由资源装配选择本次实际路线；深渊沿用同一组格入口。
        if (maze.SourceOrdinal < 0 || maze.Width <= 0 || maze.Height <= 0 || maze.Starts.Count != 1 ||
            (maze.StoryStages.Count == 0
                ? maze.Bosses.Count != 1
                : maze.StoryStages.Count != maze.Bosses.Count)
            || maze.MapSpecification is not { Count: > 0 } maps)
        {
            reason = "quest-maze-layout-unsupported";
            return false;
        }

        var start = maze.Starts[0];
        var boss = maze.StoryStages.Count > 0 ? maze.StoryStages[^1].Position : maze.Bosses[0];
        if (!InBounds(start, maze) || !InBounds(boss, maze))
        {
            reason = "quest-maze-layout-unsupported";
            return false;
        }

        var starts = maps.Where(map => map.Position == start).ToArray();
        if (maze.StoryStages.Count > 0 && starts.Length > 1)
            starts = [starts.FirstOrDefault(map => map.StoryStageIndex < 0) ?? starts[0]];
        var bosses = maze.StoryStages.Count > 0 ? new[] { maze.StoryStages[^1] } : maps.Where(map => map.Position == boss).ToArray();
        // 显式清图目标等于 Boss 坐标的基础图或叠层时，基础格可以只声明为 map；仅在本次计划中投影为 Boss，保留原资源身份。
        // 客户端 02A17ABB：02A16B60 判定 (x,y) 在当前迷宫 Boss 坐标（实例 +0x6F4 记录）时取格子 boss 列表(+0x18)，
        // 该列表为空才退回 map 列表(+4)。bossCellFromBossMap 只由调用方在已证明的范围内打开（见 PrepareFormalDungeon）。
        bool bossMapCell = bossCellFromBossMap && layoutQuest is null && questId == 0 && !hellParty && !maps.Any(map => map.Kind == "boss");
        // 02A17ABB..02A17AE4：Boss坐标的boss候选为空时使用该格map候选。普通路线按资源声明的坐标投影，
        // 不能要求DGN重复写boss种类；只改本次计划，地图与怪物身份仍由原资源及Run复验。
        // 任务路线继续要求明确的清图目标，叠层、条件和多阶段路线保留原合同。
        bool ordinaryBossCell = !definition.IsTutorial && !definition.IsClassExperience && !definition.IsTournament && dungeonId != 5000 &&
            routeQuest == 0 && (questId == 0 || ordinaryHunt) && layoutQuest is null &&
            maze.QuestConnectionTokens.Count == 0 && maze.LayeredMaps.Count == 0 && maze.ConditionalMaps.Count == 0 && maze.DummyStages.Count == 0;
        if (maze.StoryStages.Count == 0 && bosses.Length == 1 && bosses[0].Kind == "map" && (bossMapCell || ordinaryBossCell || layoutQuest is not null &&
            QuestClearEligibility.IsSingleMap(layoutQuest) && (bosses[0].Id == layoutQuest.IntData[0] ||
                maze.LayeredMaps.Any(map => map.Position == boss && map.Id == layoutQuest.IntData[0]))))
        {
            var promoted = bosses[0] with { Kind = "boss" };
            maze = maze with
            {
                MapSpecification = maze.MapSpecification!.Select(map => map == bosses[0] ? promoted : map).ToArray(),
                ConditionalMaps = maze.ConditionalMaps.Select(map => map.Position == promoted.Position && map.Kind == bosses[0].Kind
                    ? map with { Kind = promoted.Kind } : map).ToArray()
            };
            starts = starts.Select(map => map == bosses[0] ? promoted : map).ToArray();
            bosses = [promoted];
        }
        if (starts.Length != 1 || bosses.Length != 1 || bosses[0].Kind != "boss" && !(definition.IsTutorial && bosses[0].Kind == "map"))
        {
            reason = "quest-maze-map-unavailable";
            return false;
        }

        var terminal = bosses[0];
        DungeonMap? questClearMap = null;
        DungeonMap? terminalLayer = null;
        ActiveMainQuest? terminalQuest = null;
        Guid terminalAcceptance = Guid.Empty;
        if (layoutQuest is not null && QuestClearEligibility.IsSingleMap(layoutQuest))
        {
            var layers = maze.LayeredMaps.Where(map => map.Position == boss && map.Id == layoutQuest.IntData[0]).ToArray();
            if (layers.Length != 0) terminal = layers[^1];
            else if (layoutQuest.IntData[0] != terminal.Id)
            {
                var replacements = maze.ConditionalMaps.Where(map => map.Position == boss && map.Id == layoutQuest.IntData[0]).ToArray();
                if (replacements.Length == 1) terminal = replacements[0];
                else
                {
                    var targets = maze.MapSpecification!.Concat(maze.LayeredMaps).Concat(maze.ConditionalMaps)
                        .Where(map => map.Id == layoutQuest.IntData[0]).Distinct().ToArray();
                    if (targets.Length != 1) { reason = "quest-terminal-map-unavailable"; return false; }
                    questClearMap = targets[0];
                }
            }
            // 同ID基础Boss与异坐标末层不能改写wire坐标；仅原接取的显式目标可申请领域别名。
            if (!isReplay && terminal == bosses[0] && layoutQuest.IntData[0] == terminal.Id && maze.StoryStages.Count == 0)
            {
                var aliases = maze.LayeredMaps.Where(map => map.Position != boss && map.Id == terminal.Id).ToArray();
                if (aliases.Length > 1) { reason = "quest-terminal-layer-ambiguous"; return false; }
                var accepted = character.ActiveMainQuests.Where(row => row.QuestId == layoutQuest.Id).ToArray();
                if (aliases is [var alias] && accepted is [{ Slot: <= 29 } original] &&
                    (original.ConditionState == 1 || original.ConditionState == 0 &&
                        (allowClearedQuest && original.QuestId == questId || allowClearedTerminalQuest?.Invoke(original) == true)) &&
                    alias.Path == terminal.Path &&
                    original.AcceptanceId != Guid.Empty && character.ActiveMainQuests.Count(row => row.Slot == original.Slot) == 1 &&
                    maze.LayeredMaps.LastOrDefault(map => map.Position == alias.Position) == alias &&
                    !maze.ConditionalMaps.Any(map => map.Position == alias.Position))
                {
                    terminalLayer = alias;
                    terminalQuest = original;
                    terminalAcceptance = original.AcceptanceId;
                }
                else if (aliases.Length != 0) { reason = "quest-terminal-layer-unavailable"; return false; }
            }
        }
        if (quest is not null && (quest.Id != (questId == 0 ? routeQuest : questId) || quest.Definition is null ||
            quest.Definition.Dungeons.Length != 0 && !quest.Definition.Dungeons.Any(row => row.DungeonId == dungeonId)))
        {
            reason = "quest-dungeon-mismatch";
            return false;
        }

        // SourceOrdinal 仅保留资源顺序，不能直接作为 NOTI28 的 wire 枚举。
        // 实验性（未在95客户端实证；依据参考项目 dungeon_hell_room.go / A21 FindHellMapRoom）：深渊房间不是新增房间，而是 [seal door pos] 那个普通房间
        // 在深渊派对模式下改用 [seal door map index] 的深渊地图（柱子 + 波次 + 固定小怪）。起点/Boss/分层/条件/剧情图占用同一坐标时无法替换，按不可用拒绝。
        var planMaze = maze;
        // 实验性：传奇深渊把房间换成 [seal door legend map index] 图（黑暗都市 313169），其余同普通深渊。
        var activeHell = hellParty && maze.HellParty is { } baseRoom && hellLegend ? baseRoom with { MapId = baseRoom.LegendMapId } : maze.HellParty;
        var activeHellPath = hellLegend ? maze.HellLegendPath : maze.HellPartyPath;
        if (hellParty && activeHell is { } hellRoom && activeHellPath is { } hellMapPath && maze.MapSpecification is { } hellSpec)
        {
            if (starts[0].Position == hellRoom.Room || bosses.Any(boss => boss.Position == hellRoom.Room) ||
                maze.DummyStages.Any(stage => stage.Position == hellRoom.Room) ||
                maze.LayeredMaps.Concat(maze.ConditionalMaps).Concat(maze.StoryStages).Any(map => map.Position == hellRoom.Room))
            { reason = "hell-party-room-conflict"; return false; }
            planMaze = maze with { MapSpecification = hellSpec.Select(map => map.Position == hellRoom.Room && map.Kind == "map"
                ? map with { Id = hellRoom.MapId, Path = hellMapPath, CandidateIds = Array.Empty<int>() } : map).ToArray() };
        }
        var active = questId == 0 || isReplay ? null : character.ActiveMainQuests.Single(quest => quest.QuestId == questId);
        plan = new(character.Id, character.Revision, dungeonId,isReplay ? 0 : questId, active?.Slot ?? byte.MaxValue, difficulty, planMaze, starts[0], terminal)
        {
            BasisLevel = definition.BasisLevel,
            RecommendedQuestClear = !isReplay && !practice && !definition.IsTutorial && !definition.IsClassExperience && dungeonId != 5000 &&
                DungeonLuckyStarPolicy.IsRecommendedClear(definition, questId, routeQuest, character.Level),
            // 本入口只规划原生普通单人PVE；构造初始team0，START_MAP普通分支保持该值。
            // 未实现的组队/对抗模式须提供自身阵营来源，不能沿用这个入口的单人合同。
            PlayerTeam = 0,
            IsTutorial = definition.IsTutorial,
            DisableClearReward = definition.DisableClearReward,
            DisableMonsterAndClearExperience = definition.DisableMonsterAndClearExperience,
            DisableGiveupPenalty = definition.DisableGiveupPenalty,
            CoinLimit = definition.Id is >= 11006 and <= 11107 ? definition.CoinLimit : null,
            DisableFatigueConsumption = definition.DisableFatigueConsumption,
            MonsterBasisLevel = definition.BasisLevel,
            QuestAcceptanceId = active?.AcceptanceId ?? Guid.Empty,
            BaseBossMap = bosses[0],
            HasExplicitClearMap = layoutQuest is not null && QuestClearEligibility.IsSingleMap(layoutQuest),
            QuestClearMap = questClearMap,
            TerminalLayerMap = terminalLayer,
            TerminalLayerQuestId = terminalQuest?.QuestId ?? 0,
            TerminalLayerQuestSlot = terminalQuest?.Slot ?? byte.MaxValue,
            TerminalLayerAcceptanceId = terminalAcceptance,
            RouteQuestId = routeQuest,
            IsOrdinaryHunt = ordinaryHunt,
            QuestAcceptances = definition.IsClassExperience || practice ? [] : character.ActiveMainQuests.ToImmutableArray(),
            ExperienceEquipmentModule = definition.RawSections.Any(row => row.Tag == "tower of despair") ? "despair tower" :
                definition.RawSections.Any(row => row.Tag == "tower of scream") ? "scream tower" :
                definition.DungeonType?.Trim().Trim('`', '[', ']') switch
                { "tower of death" => "dead tower", "advance altar" => "advanced altar", null or "" => "dungeon", var module => module },
            ExperienceWeight = DungeonKillExperienceCatalog.GetDungeonWeight(definition),
            NamedMonsters = DungeonKillExperienceCatalog.GetNamedMonsters(definition),
            ChampionPercents = DungeonChampionRates.For(definition),
            IndividualMapMovement = definition.IndividualMapMovement,
            IsQuestReplay = isReplay,
            IsPractice = practice,
            GriefLayer = grief?.Before.State.Layer,
            HellParty = hellParty && !cosmo ? activeHell : null,
            IsHellLegend = hellParty && !cosmo && hellLegend,
            IsCosmo = cosmo,
            AllowsCompanionAi = definition.AllowsCompanionAi,
            HellPartyMode = hellParty ? (byte)1 : (byte)0,
            StartFatigue = cosmo ? definition.StartFatigue ?? (ushort)0 : (ushort)0,
            InitializationCounterSupported = !definition.IsClassExperience && !practice && maze.LayeredMaps.Count == 0 && maze.StoryStages.Count == 0 &&
                !definition.RawSections.Any(section => section.Tag is "mercenary broker dungeon" or "dnfaos dungeon")
        };
        plan = WithEntryCost(plan, definition, hellParty || isReplay || grief is not null);
        if (hope is not null)
            plan = plan with { Hope = hope, StartFatigue = checked((ushort)hope.Catalog.FatigueValue),
                QuestAcceptances = plan.QuestAcceptances.Add(hope.Acceptance) };
        reason = "resource-plan-ready";
        return true;
    }

    // #510/#511 入场票与入场一次性疲劳只用于普通入场；深渊派对、练习、比赛、回放、修炼场、职业体验及任务路线保持各自规则。
    static DungeonEntryPlan WithEntryCost(DungeonEntryPlan plan, DungeonDefinition definition, bool excluded)
    {
        if (excluded || !plan.IsOrdinary || plan.IsPractice || plan.IsTrainingRoom || plan.IsClassExperience || plan.IsTournament)
            return plan;
        var notes = new List<string>();
        var ticket = definition.OrdinaryEntryTicket;
        if (ticket is null && definition.DeclaresRequiredItem) notes.Add("required-item-shape-unsupported");
        ushort start = 0;
        if (plan.UsesFatigue && definition.DeclaresStartFatigue)
        {
            if (definition.StartFatigue is { } amount) start = amount;
            else notes.Add("start-fatigue-shape-unsupported");
        }
        return plan with { EntryItem = ticket, StartFatigue = start, EntryCostNote = notes.Count == 0 ? null : string.Join(',', notes) };
    }

    static bool CanUseOrdinaryQuestRoute(DungeonDefinition definition, Character character, StoryQuest quest,
        uint dungeonId, uint questId, byte difficulty)
    {
        bool listClear = quest.Grade == "[side]" && QuestDungeonClearProgress.MatchesAlternative(quest,dungeonId,difficulty);
        bool autoDaily = QuestAutoDailyPlan.MatchesDungeon(quest,dungeonId,difficulty);
        // 已支持的材料每日与每日累计通关任务保留接取身份，但不要求原资源不存在的任务迷宫。
        bool normalMission = QuestCycle.NormalDungeonMission(quest) &&
            (quest.Definition!.Dungeons.Any(row => row.DungeonId == dungeonId &&
                (row.Parameter == -1 || row.Parameter is >= 0 and <= 4 && row.Parameter <= difficulty)) ||
             quest.Definition.Drops.Any(row => row.MatchesEntry(dungeonId,difficulty)) ||
             QuestHuntProgress.TryTargets(quest,out var missionTargets) && missionTargets.Any(target =>
                target.DungeonId == dungeonId && target.MinimumDifficulty <= difficulty) ||
             QuestDungeonClearProgress.TryTarget(quest,out var missionClear) && missionClear.DungeonId == dungeonId &&
                missionClear.MinimumDifficulty <= difficulty);
        bool dailyClear = QuestDungeonClearProgress.CountOncePerDay(quest) &&
            quest.Grade is "[side]" or "[mission]" && QuestDungeonClearProgress.TryTarget(quest,out var target) &&
            target.DungeonId == dungeonId && (target.MinimumDifficulty == -1 || target.MinimumDifficulty <= difficulty);
        // 已核对的普通普雷考验仅要求本图通关一次；不旁路其它使命或每日累计的专属合同。
        bool preyNormalClear = quest is { Id:8925, Grade:"[mission]", Condition:"[condition under clear]",
            SubType:6, IntData:[400000046,-1] } && dungeonId == 400000046;
        if (definition.IsTutorial || definition.IsClassExperience || dungeonId == 5000 || quest.Id != questId ||
            quest.SubType != -1 && !QuestLifecyclePlan.HarlemSeeking(quest) && !listClear && !normalMission && !dailyClear && !preyNormalClear || quest.MainQuest is not null || quest.Definition is not { Diagnostics.Length: 0 } declared ||
            // 悲叹列表通关的目标由int data给出；已核对专用准入后不强求不存在的dungeon info。
            // 其它支线仍须命中dungeon info，杀怪保留0..3，列表通关沿正式0..4难度域。
            quest.Grade == "[side]" && !dailyClear && !(definition.IsGriefTower && listClear) && !declared.Dungeons.Any(row => row.DungeonId == dungeonId &&
                (row.Parameter == -1 || (quest.Condition == "[hunt monster]" && row.Parameter is >= 0 and <= 3 ||
                    listClear && row.Parameter is >= 0 and <= 4) && row.Parameter <= difficulty)) ||
            quest.Grade == "[achievement]" && (declared.JobChange is not (2 or 3) ||
                !declared.Dungeons.Any(row => row.DungeonId == dungeonId && row.Parameter is >= 0 and <= 3 && row.Parameter <= difficulty)) ||
            quest.Grade is not ("[side]" or "[achievement]") && !autoDaily && !normalMission && !dailyClear && !preyNormalClear ||
            definition.Mazes.Any(maze => maze.QuestConnectionTokens.Count != 0 &&
                (maze.ConnectionQuestId is not { } connected || connected == questId))) return false;
        if (quest.Condition == "[hunt monster]" || normalMission && quest.Condition == "[hunt enemy]")
        {
            if (!QuestHuntProgress.TryTargets(quest,out var targets)) return false;
            if (normalMission && !targets.Any(target => target.DungeonId == dungeonId)) return false;
            if (targets.Where(target => !normalMission || target.DungeonId == dungeonId).Any(target =>
                target.MonsterId <= 0 && target.MonsterId != QuestHuntProgress.EliteMonsterSelector && target.MonsterId != -3 && !(autoDaily && target.MonsterId == -1) ||
                target.DungeonId != -1 && target.DungeonId != dungeonId || target.MinimumDifficulty > difficulty)) return false;
        }
        // 觉醒收集阶段只借用普通路线；材料由任务声明的本图怪物掉落规则产生，不改变掉落或进度合同。
        // #256黑市EXP的6053/6056/6059（seeking sub type 0）同样只借用普通路线，材料来自任务声明的本图怪物掉落。
        else if (!(normalMission && QuestDungeonClearProgress.Supports(quest)) && !listClear && !dailyClear && !preyNormalClear && (!(quest.Grade == "[achievement]" && quest.Condition == "[seeking]" && QuestLifecyclePlan.ValidSeeking(quest) ||
                QuestLifecyclePlan.HarlemSeeking(quest) || normalMission) ||
            !declared.Drops.Any(row => row.MatchesEntry(dungeonId,difficulty)) &&
                !(normalMission && quest.Condition == "[seeking]" && declared.Drops.Length == 0 && QuestLifecyclePlan.ValidSeeking(quest)))) return false;
        var active = character.ActiveMainQuests.Single(row => row.QuestId == questId);
        return active.AcceptanceId != Guid.Empty &&
            character.ActiveMainQuests.Count(row => row.AcceptanceId == active.AcceptanceId) == 1;
    }

    static bool CanLeaveSeekingRoute(DungeonDefinition definition, Character character, StoryQuest quest, ActiveMainQuest active)
    {
        if (definition.IsTutorial || definition.IsClassExperience || definition.Id == 5000 ||
            character.Level < definition.MinimumLevel || quest.Condition != "[seeking]" || quest.MainQuest is not null ||
            !QuestLifecyclePlan.SeekingSubtype(quest) || quest.Definition is not { Diagnostics.Length: 0 } declared ||
            !declared.Dungeons.Any(row => row.DungeonId == definition.Id) ||
            !definition.Mazes.Any(maze => maze.QuestConnectionTokens.Count == 0) ||
            !definition.Mazes.Any(maze => maze.HasActiveQuestConnection(quest.Id)) ||
            active.ConditionState != 0 || active.Slot > 29 || active.AcceptanceId == Guid.Empty ||
            QuestEligibility.Completed(character).Contains(quest.Id) ||
            character.ActiveMainQuests.Count(row => row.QuestId == active.QuestId) != 1 ||
            character.ActiveMainQuests.Count(row => row.Slot == active.Slot) != 1 ||
            character.ActiveMainQuests.Count(row => row.AcceptanceId == active.AcceptanceId) != 1)
            return false;
        return QuestLifecyclePlan.HasMaterials(character,quest,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    // 剧情迷宫只覆盖普通难度时，目标已经做成但任务还没交，再进应使用循环用的普通迷宫。
    static bool CanLeaveClearedStoryRoute(DungeonDefinition definition, Character character, StoryQuest quest,
        ActiveMainQuest active, uint questId, bool allowClearedQuest, Func<ActiveMainQuest,bool>? allowClearedTerminalQuest)
    {
        if (definition.IsTutorial || definition.IsClassExperience || definition.Id == 5000 ||
            definition.MinimumLevel is not int minimum || character.Level < minimum ||
            quest.Definition is not { Diagnostics.Length: 0 } ||
            !definition.Mazes.Any(maze => maze.QuestConnectionTokens.Count == 0) ||
            !definition.Mazes.Any(maze => maze.HasActiveQuestConnection(quest.Id)) ||
            active.ConditionState != 0 || active.Slot > 29 || active.AcceptanceId == Guid.Empty ||
            QuestEligibility.Completed(character).Contains(quest.Id) ||
            character.ActiveMainQuests.Count(row => row.QuestId == active.QuestId) != 1 ||
            character.ActiveMainQuests.Count(row => row.Slot == active.Slot) != 1 ||
            character.ActiveMainQuests.Count(row => row.AcceptanceId == active.AcceptanceId) != 1)
            return false;
        bool receipt = quest.Id == questId && allowClearedQuest || allowClearedTerminalQuest?.Invoke(active) == true;
        if (QuestClearEligibility.IsSingleMap(quest) || quest.Condition is "[condition under clear]" or "[clear a dungeon]")
            return receipt;
        // 被动物件的条件0不代替本实例场景事实，必须保留原任务地图和终点。
        return quest.Condition is "[hunt enemy]" or "[hunt monster]" &&
            QuestHuntProgress.TryTargets(quest,out var targets) && targets.All(target => target.EnemyType != 3);
    }

    static StoryQuest? CurrentSequentialChild(Character character, StoryQuest parent, QuestCatalog quests, uint dungeonId, byte difficulty)
    {
        if (!QuestRelationProgress.SequentialParent(parent,quests)) return null;
        var completed = QuestEligibility.Completed(character);
        if (completed.Contains(parent.Id)) return null;
        var accepted = character.ActiveMainQuests.Where(active => active.QuestId == parent.Id).ToArray();
        if (accepted is not [{ Slot: <= 29, AcceptanceId: var acceptance, ConditionState: > 0 } active] || acceptance == Guid.Empty ||
            character.ActiveMainQuests.Count(row => row.Slot == active.Slot) != 1 ||
            active.ConditionState != QuestRelationProgress.Initial(parent,quests,completed)) return null;
        int next = parent.IntData.FirstOrDefault(id => !completed.Contains(id));
        if (!quests.TryGet(next,out var child) || !QuestCoverage.HasOrdinaryDeclarations(child) ||
            child.Definition!.Dungeons.Length != 0 && !child.Definition.Dungeons.Any(row => row.DungeonId == dungeonId) ||
            !QuestEligibility.Evaluate(character with { ActiveMainQuests = character.ActiveMainQuests.Where(row => row.QuestId != child.Id).ToArray() },
                child,quests).Eligible) return null;
        var childAcceptances = character.ActiveMainQuests.Where(row => row.QuestId == child.Id).ToArray();
        if (childAcceptances.Length != 0 && (childAcceptances.Any(row => row.AcceptanceId == Guid.Empty) ||
            ValidateRequest(character,dungeonId,checked((uint)child.Id),difficulty,false,child) != "resource-request-ready")) return null;
        // 子任务未接取时只选择路线；真实31提交后沿已有BindQuestAcceptance绑定，不能在规划中造接取。
        return child;
    }

    static DungeonMaze[] DistinctQuestRoutes(IEnumerable<DungeonMaze> source)
    {
        var routes = new List<DungeonMaze>();
        foreach (var maze in source)
            if (maze.QuestConnectionTokens.Count == 0 || !routes.Any(existing =>
                existing.QuestConnectionTokens.SequenceEqual(maze.QuestConnectionTokens) && existing.SameLayout(maze)))
                routes.Add(maze);
        return routes.ToArray();
    }

    // #438: 任务驱动进入放宽副本最低等级。仅限：任务已接取、与本副本关联（迷宫 quest connection 或 dungeon info）、角色等级不低于任务最低等级。
    private static bool QuestEntryWaivesDungeonLevel(DungeonDefinition definition,Character character,uint dungeonId,uint questId,byte difficulty,
        StoryQuest? quest,QuestCatalog? quests,bool isReplay)
    {
        if (isReplay) return false;
        uint[] ids = questId != 0 ? new[] { questId } : character.ActiveMainQuests.Select(active => (uint)active.QuestId).ToArray();
        foreach (var id in ids)
        {
            if (!character.ActiveMainQuests.Any(active => active.QuestId == id)) continue;
            StoryQuest? candidate = quest is not null && quest.Id == id ? quest : null;
            if (candidate is null && (quests is null || !quests.TryGet((int)id,out candidate))) continue;
            bool related = definition.Mazes.Any(maze => maze.ConnectsQuest(id,difficulty)) ||
                candidate.Definition is { Diagnostics.Length: 0 } declared && declared.Dungeons.Any(row => row.DungeonId == dungeonId);
            if (related && character.Level >= candidate.MinimumLevel) return true;
        }
        return false;
    }

    private static string ValidateRequest(Character character, uint dungeonId, uint questId, byte difficulty, bool allowClearedQuest, StoryQuest? quest,bool isReplay = false)
    {
        if (character.IsPvpPrivate) return "pvp-private-character";
        if (dungeonId is 0 or > int.MaxValue)
            return "dungeon-unsupported";
        if (questId >= 30000)
            return "quest-unsupported";
        if (difficulty > 4 || questId == 0 && dungeonId is 3 or 5 or 6 or 7 or 8 or 9 or 1000 && difficulty != 0)
            return "difficulty-unsupported";
        if (character.Level is < 1 or > 95)
            return "character-level-invalid";
        if (isReplay)
            return questId > 0 && difficulty == 0 && quest?.Id == questId && QuestEligibility.Completed(character).Contains((int)questId)
                ? "resource-request-ready" : "quest-replay-unavailable";
        if (questId == 0) return "resource-request-ready";
        if (QuestEligibility.Completed(character).Contains((int)questId))
            return "quest-already-completed";
        var active = character.ActiveMainQuests.Where(quest => quest.QuestId == questId).ToArray();
        if (active.Length == 0)
            return "quest-not-active";
        if (active.Length != 1 || active[0].Slot > 29
            || character.ActiveMainQuests.Count(quest => quest.Slot == active[0].Slot) != 1)
            return "quest-active-conflict";
        // 已达成条件只有持久清图资格才能继续挑战；裸条件值不能伪装正式通关。
        if ((quest is null || QuestClearEligibility.IsSingleMap(quest)) && active[0].ConditionState != 1 &&
            !(allowClearedQuest && active[0].ConditionState == 0) ||
            quest?.Condition is "[condition under clear]" or "[clear a dungeon]" && (!QuestDungeonClearProgress.ValidScalar(quest,active[0].ConditionState) ||
                active[0].ConditionState == 0 && !allowClearedQuest) ||
            quest?.Condition is "[hunt enemy]" or "[hunt monster]" && !QuestHuntProgress.ValidScalar(quest, active[0].ConditionState))
            return "quest-condition-not-pending";
        return "resource-request-ready";
    }

    private static bool InBounds(DungeonPoint point, DungeonMaze maze) =>
        point.X >= 0 && point.Y >= 0 && point.X < maze.Width && point.Y < maze.Height;
}
