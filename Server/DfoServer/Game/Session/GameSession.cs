using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Characters;
using DfoServer.Game.Auth;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Settings;
using DfoServer.Game.Caching;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

// 角色会话阶段由Session维护，正式副本按其私有实例推进。
internal enum SessionPhase : byte
{
    Unselected,
    Town,
    MapSelect,
    Loading,
    Dungeon,
    Settlement,
    PvpRoom
}

internal sealed partial class GameSession(ICharacterRepository repository, TownCatalog towns, AccountOwners.Lease owner, AuthenticatedIdentity? identity = null,
    DfoServer.GameWorld.Quests.QuestCatalog? questCatalog = null, GameDebugSettings? debugSettings = null, GameWorld.Channels.ChannelEntry? entry = null,
    DfoServer.Game.Quests.QuestSkipEffects? questEffects = null, PremiumContractCatalog? contracts = null, TimeProvider? timeProvider = null,
    DfoServer.Game.Dungeons.DungeonPassiveDropPolicy? passiveDrops = null,
    Game.Creatures.CreatureService? creatures = null, GameWorld.Quests.QuestStageGraph? stageGraph = null,
    ItemCatalog? items = null, EpicPieceCatalog? pieces = null,
    ExperienceBonusService? experienceBonuses = null, GameWorld.Dungeons.DungeonIncountCatalog? incounts = null,
    ChannelHistory? channelHistory = null, GameWorld.Dungeons.OperationHopeCatalog? operationHope = null,
    GameWorld.Guilds.GuildDungeonCatalog? guildDungeons = null,
    Game.Pvp.PvpRatingSettlement? pvpRatings = null,
    DungeonBattleLevelSettings? monsterBattleLevels = null, DungeonMonsterAiSettings? monsterAi = null,
    DfoServer.Game.Inventory.Services.ServerItemCycleService? itemCycles = null,
    DfoServer.Game.Guild.GuildNpcBenefitService? guildNpcBenefits = null,
    DfoServer.Game.CashShopPromotions.ICashShopPromotionStore? promotionStore = null,
    RecoveryConsumableCatalog? consumables = null, SpringTreasureDropPolicy? springTreasure = null,
    GameWorld.Dungeons.Strongway2019Catalog? strongway2019Catalog = null,
    Func<Character, int>? strongway2019AntiEvil = null, Game.Events.ServerEventService? serverEvents = null,
    GoldLimitCatalog? directPickupGoldLimits = null,
    DfoServer.Game.Events.PcRoomTimePointService? pcRoomTimePoint = null,
    DfoServer.Game.Dungeons.RiftNamedEpicDropPolicy? riftNamedEpics = null)
{
    readonly GameCacheScope gameCache = owner.CreateCacheScope();

    public AuthenticatedIdentity? Identity { get; } = identity;
    public GameWorld.Channels.ChannelEntry? ChannelEntry { get; } = entry;
    Character? selected;
    public Character? Selected
    {
        get => selected;
        private set { InvalidateGuildHallWait(selected, value); TrackHiddenQuestArea(selected, value); selected = value; }
    }
    public bool Ready { get; private set; }
    SessionPhase phase = SessionPhase.Unselected;
    public SessionPhase Phase
    {
        get => phase;
        private set
        {
            if (phase != value)
            {
                InvalidateHelperAssetSafety();
                guildHallWait = null;
                hiddenAreaEnteredAt = value == SessionPhase.Town ? HiddenQuestTimestamp() : null;
            }
            phase = value;
        }
    }
    public long SelectionEpoch { get; private set; }
    public string TravelReason { get; private set; } = "not-requested";
    public bool PremiumPcRoom => debugSettings?.PremiumPcRoom == true ||
        (Selected?.Inventory.AccountWallet.ContractExpiry(128) ?? 0) >
            (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
    public CharacterFatigueState FatigueForClient(Character character)
    {
        var state = (character.Fatigue ?? CharacterFatigueState.NewOrdinary)
            .ForBlackDiamond(character.BlackDiamondExpiresAt > (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds());
        state.Validate();
        return debugSettings?.InfiniteFatigue == true ? state with { Used = 0, Remaining = state.Maximum } : state;
    }

    IReadOnlySet<int> completed = new HashSet<int>();
    public CharacterRosterEntry[] Roster() => repository.ListRoster(owner);
    public Character[] QueryCharacters() => repository.List(owner);

    internal OnlineCharacterSnapshot? OnlineCharacter()
    {
        try
        {
            return owner.Execute(() => Selected is { } character
                ? new OnlineCharacterSnapshot(owner.Account, character.Id, character.Name, Ready, Phase, ReadState(Phase)) : null);
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation")
        {
            return null;
        }
    }

    public bool NameExists(string name) => owner.Execute(() =>
    {
        if (Selected is not null)
            throw new InvalidDataException("Name check after selection");
        return repository.NameExists(name);
    });

    public Character CreateCharacter(string name, CharacterProfile profile, SkillLearningCatalog? learning = null,
        TutorialFlow? tutorial = null) => CreateCharacter(name, profile, learning is null ? null : learning.Get, tutorial);

    public CharacterCreationReceipt CreateCharacterForClient(string name, CharacterProfile profile, SkillLearningCatalog? learning = null,
        TutorialFlow? tutorial = null) => PrepareCharacterCreation(name, profile, learning is null ? null : learning.Get, tutorial);

    internal Character CreateCharacter(string name, CharacterProfile profile, Func<byte, ushort, SkillLearningDefinition>? definition,
        TutorialFlow? tutorial = null) => PrepareCharacterCreation(name, profile, definition, tutorial).Created;

    CharacterCreationReceipt PrepareCharacterCreation(string name, CharacterProfile profile, Func<byte, ushort, SkillLearningDefinition>? definition,
        TutorialFlow? tutorial = null)
    {
        long epoch = owner.Execute(() =>
        {
            if (Selected is not null)
                throw new InvalidDataException("Create after selection");
            return SelectionEpoch;
        });
        var creation = CharacterCreation.Prepare(name, profile, towns.Room, towns.InitialReturn, definition, tutorial);
        return owner.Execute(() =>
        {
            if (Selected is not null || SelectionEpoch != epoch)
                throw new InvalidOperationException("Stale character creation state");
            var created = repository.Create(owner, creation);
            SelectionEpoch++;
            return new CharacterCreationReceipt(created, CaptureRoster());
        });
    }

    public void ReturnToRoster()
    {
        var notices = new DepartureNotices();
        Exception? failure = null;
        try { ReturnToRoster(notices); }
        catch (Exception error) { failure = error; throw; }
        finally { notices.Publish(failure); }
    }

    // 需要在外层owner中复验的用例传入本次收尾容器，由最外层退出锁后发布。
    internal void ReturnToRoster(DepartureNotices notices) => owner.Execute(() =>
    {
        notices.Battle = raidCombat?.Battle;
        // 移动和传送已逐次提交；这里只结束当前角色会话，不重写位置或进度。
        try { CompleteCurrentImperialArena(); }
        finally { Invalidate("not-requested", notices); }
        return true;
    });

    public void End()
    {
        var notices = new DepartureNotices { Battle = raidCombat?.Battle };
        Exception? failure = null;
        // 断线时旧generation已不能拿锁，仍要让这个会话自己的代数失效。
        try
        {
            try
            {
                owner.Execute(() =>
                {
                    try { CompleteCurrentImperialArena(); }
                    finally { Invalidate("session-ended", notices); }
                    return true;
                });
            }
            catch (InvalidOperationException ex) when (ex.Message == "Stale account generation")
            {
                Invalidate("session-ended", notices, publishExpertJob: false);
            }
            finally { ReleasePcRoomTimePoint(); gameCache.Dispose(); }
        }
        catch (Exception error) { failure = error; throw; }
        finally { notices.Publish(failure); }
    }

    public bool MatchesSelection(long epoch, ushort characterId) =>
        Phase == SessionPhase.Town && SelectionEpoch == epoch && Selected?.Id == characterId;

    // 本次选角是否按原生换频道快照恢复城镇（决定入城包序）；由 SelectCharacter 设置。
    public bool TownSnapshotResumed { get; private set; }

    public void Select(ushort slot, GoldLimitCatalog? goldLimits = null, bool resumeTown = false)
    {
        SelectCharacter(slot, goldLimits, null, resumeTown);
        PrepareSelectedQuestItemFacts();
    }

    void PrepareSelectedQuestItemFacts()
    {
        if (questCatalog?.Items is not { } items) return;
        var character = owner.Execute(() => Selected);
        if (character is not null && character.ActiveMainQuests.Any(row => questCatalog.TryGet(row.QuestId, out var quest) &&
            (quest.TargetKind == GameWorld.Quests.QuestTargetKind.EquippedItem || DfoServer.Game.Quests.QuestItemProgress.HasSelector(quest))))
            // 首次选角在库存初始化之前、owner锁外装载；后续SQL只读共享目录中的已装载事实。
            DfoServer.Game.Quests.QuestItemProgress.Prepare(character.Inventory, items);
    }

    void SelectCharacter(ushort slot, GoldLimitCatalog? goldLimits, CharacterRosterEntry? expected, bool resumeTown = false)
    {
        owner.Execute(() =>
        {
            if (Selected is not null)
                throw new InvalidDataException("Invalid selection state");
            var character = repository.LoadCharacter(owner, slot);
            // 只恢复本账号原角色的持久整局来源；来源冻结的接取UUID不匹配时不会补给后来接取的任务。
            if (pvpRatings is not null)
                pvpRatings.Recover(owner, character.Id, receipt =>
                {
                    if (receipt.QuestAdvance is { } progress) character = ApplyPvpQuestAdvance(character, progress);
                });
            if (expected is not null && (character.Id != expected.Id || character.Slot != expected.Slot || character.Name != expected.Name))
                throw new InvalidOperationException("Selection published roster identity changed");
            // 普通选角与原生换频道共用此门禁；拒绝发生在修复、扣费和位置保存之前。
            if (DfoServer.Game.Raids.RaidAdmissionPlan.Rejection(ChannelEntry, character) is { } raidRejection)
                throw new DfoServer.Game.Raids.RaidAdmissionException(raidRejection);
            if (operationHope is not null)
            {
                var orphaned = character.ActiveMainQuests.Where(row => operationHope.Missions.ContainsKey(row.QuestId)).ToArray();
                if (orphaned.Length > 0) character = repository.ClearOperationHopeAcceptances(owner,character,operationHope,orphaned);
            }
            if (character.IsPvpPrivate && ChannelEntry?.Type is not (8 or 24))
                throw new InvalidDataException("专用决斗角色只允许进入已实现的决斗频道");
            var fatigueNow = (timeProvider ?? TimeProvider.System).GetUtcNow();
            character = repository.ResetDailyFatigue(owner, character,
                FatigueDailyReset.Day(fatigueNow, (timeProvider ?? TimeProvider.System).LocalTimeZone), fatigueNow);
            character = repository.GrantBlackDiamondFatigue(owner, character, fatigueNow);
            if (questCatalog is not null)
            {
                if(DfoServer.Game.Quests.QuestQuickClearPlan.TryCreateOrphanRepair(character,questCatalog,out var repair))
                    character=repository.CommitQuestQuickClear(owner,repair!).Character;
                int day = DfoServer.Game.Quests.QuestCycle.Day(DateTimeOffset.UtcNow);
                character = repository.AdvanceQuestCycle(owner, character, day,
                    DfoServer.Game.Quests.QuestAutoDailyPlan.Prepare(character,questCatalog,day,allowAssignment: true));
            }
            var quests = DfoServer.Game.Quests.QuestEligibility.Completed(character);
            // 客户端没有保留换频道快照时（普通频道与团队页签频道互换、进出魔界大战等类型谓词不一致的换频道），按普通选角落点和包序。
            // 旧频道类型已知（同一服务端进程内的上一次选角）时直接比较谓词；否则按存档城镇推断。
            int? previousType = ChannelEntry is null ? null : channelHistory?.Previous(owner.Account);
            bool resumed = resumeTown && !ChannelAreaLanding.IsRaidInstanceArea(character.Position) &&
                !ChannelAreaLanding.LeavesRetiredTown(character.Position) &&
                ChannelAreaLanding.AllowsFiendwarTown(ChannelEntry, character.Position.Town) &&
                ChannelAreaLanding.AllowsEvildomWarChallengeTown(ChannelEntry, character.Position.Town) &&
                (ChannelEntry is not null && previousType is int previous
                ? ChannelAreaLanding.SameSnapshotPredicates(previous, ChannelEntry.Type)
                : ChannelAreaLanding.ClientKeepsTownSnapshot(towns, ChannelEntry, character.Position));
            // 修复旧115落点；重选角色不恢复上次攻坚队的候战/战场，五分钟重返另由原实例恢复。
            if (ChannelEntry?.Type == ChannelAreaLanding.FiendwarType &&
                (character.Position.Town != towns.Room.Town || character.Position.Area != towns.Room.Area) &&
                character.Position is not { Town: ChannelAreaLanding.FiendwarTown, Area: <= 1 }) resumed = false;
            // 修复旧版本把普通街道保留在决斗频道的存档；同类型重连也不能继续恢复错误城镇。
            if (ChannelEntry is not null && ChannelAreaLanding.IsPvpType(ChannelEntry.Type) && character.Position.Town != 10)
                resumed = false;
            // 旧版在共享房间下线的TAG角色也须进入本频道准备场景，不能恢复旧错误落点。
            if (ChannelEntry?.Type is 39 or 53 &&
                character.Position.Town != (ChannelEntry.Type == 39 ? 48 : 62))
                resumed = false;
            // 同64历史只能保留英雄城镇；旧共享房间或普通战役67不能借同类型快照绕过落点修复。
            if (ChannelEntry?.Type == ChannelAreaLanding.EvildomWarChallengeType &&
                character.Position.Town != ChannelAreaLanding.EvildomWarChallengeTown)
                resumed = false;
            character = CharacterSelection.Prepare(repository, owner, character, towns, quests, goldLimits, resumed, items, timeProvider, ChannelEntry, previousType);
            TownSnapshotResumed = resumed;
            if (ChannelEntry is not null)
                channelHistory?.Record(owner.Account, ChannelEntry.Type);
            character = ReconcileSkillSwitchInventory(character);
            Selected = character;
            completed = quests;
            Ready = false;
            SetState(SessionPhase.Unselected);
            SelectionEpoch++;
            return true;
        });
    }

    public bool CanPublishCreation(CharacterCreationReceipt receipt) => owner.Execute(() =>
        MatchesRoster(receipt.Roster) &&
        Selected is null && Phase == SessionPhase.Unselected);

    // 客户端每次选角入城后约0.3秒会把已穿装备按登录态复述一次 Main→Equipment 搬移（源格为空、源物品编号为0）。
    // 该形状与真实卸装相同，只在入城窗口内吸收；窗口外仍按原顺序校验，不吞掉玩家主动卸装。
    const long LoginWearRestatementWindowMillis = 5000;
    long loginWearRestatementUntil;
    public bool LoginWearRestatementWindowOpen => Ready && Phase == SessionPhase.Town && loginWearRestatementUntil >
        (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();

    public void MarkReady()
    {
        owner.Execute(() =>
        {
            if (Selected is null)
                throw new InvalidOperationException("No selected character");
            if (Ready || Phase != SessionPhase.Unselected)
                throw new InvalidOperationException("入城就绪只能由当前未完成的选角初始化推进");
            // 活动初值读取失败时保留未就绪状态，允许同一次入城请求正常重试。
            StartPcRoomTimePoint();
            Ready = true;
            // 窗口只随选角入城开启；副本返回城镇不重开，避免把城镇内的真实卸装当成复述。
            loginWearRestatementUntil = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds() +
                LoginWearRestatementWindowMillis;
            SetState(SessionPhase.Town);
            ObserveGuildPresence();
            ObserveGuildAttendance();
            return true;
        });
    }

    public Character UpdateProgress(CharacterProgressEdit request) => owner.Execute(() =>
    {
        if (pendingRental is not null)
            throw new InvalidOperationException("租赁结果待确认，须恢复原提交或重新选角");
        if (HasPendingFormalCard && string.Equals(Selected?.Name, request.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("翻牌结果待确认，不能覆盖当前角色进度");
        if (request.Account != owner.Account)
            throw new InvalidDataException("账号与当前会话不一致");
        // 达人契约属于账号；即使命令目标是另一角色，也会替换当前技能快照。
        if (HasPendingTownQuestCommit && (request.Field == CharacterProgressField.ExpertContract ||
            string.Equals(Selected?.Name, request.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("任务结果待确认，不能覆盖当前角色进度");
        var updated = repository.UpdateProgress(owner, request);
        // 旧GM单字段合同可能不推进revision，所以非当前角色（尤其是1394备用角色）的进度改动靠本版本号失效。
        // 主角由客户端自己持有的真实Actor提供，改动随Selected一起刷新，不必重装双Actor；
        // 捕获到发布的窗口由CanPublishTagEvildomCharacters的引用相等与钱包版本把关。
        if (Selected?.Id != updated.Id) tagEvildomCharacterVersion++;
        // 提交成功后才替换内存。任务集合立即参与城镇准入；客户端画面仍等下次选角。
        if (Selected?.Id == updated.Id)
        {
            InvalidateImageCommunication();
            Selected = updated;
            completed = DfoServer.Game.Quests.QuestEligibility.Completed(updated);
        }
        else if (request.Field == CharacterProgressField.ExpertContract && Selected is { } selected)
            Selected = selected with { Learning = selected.Learning with { ExpertContractExpiresAt = updated.Learning.ExpertContractExpiresAt } };

        return updated;
    });

    public Character ResetProgress(CharacterProgressEdit request, CharacterProgressReset reset)
    {
        var captured = owner.Execute(() =>
        {
            if (pendingRental is not null)
                throw new InvalidOperationException("租赁结果待确认，须恢复原提交或重新选角");
            if (request.Account != owner.Account) throw new InvalidDataException("账号与当前会话不一致");
            if (HasPendingTownQuestCommit && string.Equals(Selected?.Name, request.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("任务结果待确认，不能重置当前角色进度");
            var target = repository.ListRoster(owner).SingleOrDefault(value => string.Equals(value.Name, request.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("角色不存在");
            var character = repository.LoadCharacter(owner, target.Slot);
            return (Character: character, Selected, SelectionEpoch);
        });
        var plan = reset.Prepare(captured.Character, request);
        return owner.Execute(() =>
        {
            if (pendingRental is not null || !ReferenceEquals(Selected, captured.Selected) || SelectionEpoch != captured.SelectionEpoch ||
                (HasPendingFormalCard || HasPendingTownQuestCommit) && Selected?.Id == captured.Character.Id)
                throw new InvalidOperationException("在线角色快照已变化，请重试");
            var updated = repository.ResetProgress(owner, plan);
            // 与UpdateProgress同口径：只有非当前角色的进度改动需要让双Actor资料失效。
            if (Selected?.Id != updated.Id) tagEvildomCharacterVersion++;
            if (Selected?.Id == updated.Id)
            {
                InvalidateImageCommunication();
                Selected = updated;
            }
            return updated;
        });
    }

    void Invalidate(string reason, DepartureNotices notices, bool publishExpertJob = true)
    {
        ResetStrongway2019Matching();
        CheckpointPcRoomTimePoint();
        bool warPersistenceFailed = false;
        try { if (owner.IsCurrent) DisconnectEvildomWar(); }
        catch (Exception error)
        {
            warPersistenceFailed = true;
            EvildomWarCleanupFailed?.Invoke(error);
        }
        GuildManagementClosing?.Invoke();
        ObserveGuildPresence();
        InvalidateRaids(notices, reason == "session-ended");
        CancelHostedDungeonEligibility();
        mailboxRestore = null;
        npcTownVisit = null;
        hopeSelection = null;
        pvpTraining = null;
        pvpPractice = null;
        Volatile.Write(ref pvpArena, null);
        pvpArenaVersion++;
        pendingPvpSkills = null;
        pendingTrainingCopy = null;
        InvalidateExpertJobStore(publishExpertJob);
        // 原实例失效后，不让其迟到回执找到同账号的新Selected。
        tagEvildomPair = null;
        tagEvildomSelection = null;
        ReleaseCurrentEvildomWarDungeon(cleanup: true, defer: warPersistenceFailed);
        evildomWar = null;
        formalDungeon?.Run.CancelTagEvildom();
        formalDungeon = null;
        gameCache.Clear();
        rechargeTicket = null;
        ClearConsumableUse();
        publishedRoster = null;
        selectedRoster = null;
        pendingDungeonContinuation = null;
        pendingQuestReplay = null;
        pendingQuestCompletion = null;
        pendingQuestLifecycle = null;
        pendingRental = null;
        InvalidateImageCommunication();
        pendingQuestQuickClear = null;
        SelectionEpoch++;
        Selected = null;
        completed = new HashSet<int>();
        Ready = false;
        CurrentDungeonSelection = null;
        SetState(SessionPhase.Unselected);
        TravelReason = reason;
        // 名册已移除；记录故障仍须传播，但不能让旧角色停留在半失效状态。
        notices.CommitDisconnect();
    }
}
