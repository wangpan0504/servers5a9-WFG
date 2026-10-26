using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
namespace DfoServer.Game.Characters;
// 角色用例依赖的持久化边界；SQLite、迁移与离线导入细节不进入 Game。
// 角色改名：独立接口，避免所有仓库包装类都要实现；只由 GM 使用。
internal interface ICharacterRenamer
{
    Character Rename(DfoServer.Game.Accounts.AccountOwners.Lease owner, Character expected, string newName);
}

// 副职业类型与累计熟练度：只由开发 GM 使用，避免所有仓库包装类都要实现。
internal interface IExpertJobSetter
{
    Character SetExpertJob(AccountOwners.Lease owner, Character expected, byte type, int experience);
}

internal interface ICharacterRepository
{
    QuestStageState LoadQuestStageState(AccountOwners.Lease owner, Character expected);
    string ReadAdventureName(AccountOwners.Lease owner);
    bool AdventureNameExists(AccountOwners.Lease owner, string name);
    DfoServer.Game.Adventure.AdventureNameCommit CommitAdventureName(AccountOwners.Lease owner,
        DfoServer.Game.Adventure.AdventureNamePlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now);
    DfoServer.Game.Adventure.AdventureCapsuleCommit CommitAdventureCapsule(AccountOwners.Lease owner,
        DfoServer.Game.Adventure.AdventureCapsulePlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now);
    DfoServer.Game.Adventure.AdventureShopCommit CommitAdventureShop(AccountOwners.Lease owner,
        DfoServer.Game.Adventure.AdventureShopPlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now);
    DfoServer.Game.Adventure.AdventureSnapshot CommitAdventureExpedition(AccountOwners.Lease owner,
        DfoServer.Game.Adventure.AdventureExpeditionPlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now);
    DfoServer.Game.Adventure.AdventureSnapshot CommitAdventureRepresentative(AccountOwners.Lease owner,
        DfoServer.Game.Adventure.AdventureRepresentativePlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now);
    DfoServer.Game.Adventure.AdventureSnapshot ReadAdventure(AccountOwners.Lease owner, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now);
    // 每周地下城信息页：718名册含等级/职业/成长与本周各列已用次数，791保存客户端提交的5槽位排列。
    System.Collections.Immutable.ImmutableArray<DfoServer.Game.Adventure.AdventureWeeklyCharacter> ReadAdventureWeeklyRoster(
        AccountOwners.Lease owner, int week);
    System.Collections.Immutable.ImmutableArray<byte> ReadAdventureWeeklyConfig(AccountOwners.Lease owner);
    void WriteAdventureWeeklyConfig(AccountOwners.Lease owner, System.Collections.Immutable.ImmutableArray<byte> slots);
    // 结算加成只读取账号等级，不装载商店、代表与远征；未初始化账号按初始等级1处理。
    int ReadAdventureLevel(AccountOwners.Lease owner);
    void InitializeAdventure(AccountOwners.Lease owner, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now);
    DfoServer.Game.Tutorials.TutorialCheckpointResult CommitTutorialCheckpoint(AccountOwners.Lease owner,
        DfoServer.Game.Tutorials.TutorialCheckpointPlan plan);
    OperationHopeRewardCommit CommitOperationHopeReward(AccountOwners.Lease owner, OperationHopeRewardPlan plan);
    Character ClearOperationHopeAcceptances(AccountOwners.Lease owner, Character expected,
        DfoServer.GameWorld.Dungeons.OperationHopeCatalog catalog, IReadOnlyList<ActiveMainQuest> removals);
    Character ConsumeOperationHopeAreaFatigue(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run);
    Character ConsumeDungeonRoomFatigue(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run);
    Character GrantBlackDiamondFatigue(AccountOwners.Lease owner, Character expected, DateTimeOffset now);
    CardMembership LoadCardMembership(AccountOwners.Lease owner,ushort characterId,DateTimeOffset now);
    CardMembership SetCardMembership(AccountOwners.Lease owner,string name,CardMembershipKind kind,long expires);
    DungeonPaidCardCommit CommitPaidDungeonCard(AccountOwners.Lease owner,Character expected,PreparedDungeonSettlement prepared,
        DungeonCardDelivery delivery,DateTimeOffset now);
    DungeonFreeCardCommit CommitFreeDungeonCard(AccountOwners.Lease owner, Character expected,
        PreparedDungeonSettlement prepared, DungeonCardDelivery delivery);
    DfoServer.Game.Currency.AccountWallet LoadAccountWallet(AccountOwners.Lease owner);
    CharacterRosterEntry[] ListRoster(AccountOwners.Lease owner);
    Character LoadCharacter(AccountOwners.Lease owner, ushort slot);
    Character[] List(AccountOwners.Lease owner);
    bool NameExists(string name);
    Character Create(AccountOwners.Lease owner, NewCharacter character);
    bool CanCreatePvpPrivate(AccountOwners.Lease owner, byte job, byte grow, DfoServer.Game.Pvp.PvpPrivateCharacterCreation creation);
    CharacterDeletion LoadDeletionTarget(AccountOwners.Lease owner, ushort slot);
    CharacterDeletion Delete(AccountOwners.Lease owner, CharacterDeletion expected);
    void SwapSlots(AccountOwners.Lease owner, ushort slotA, ushort slotB);
    Character Save(AccountOwners.Lease owner, Character expected, Location position, Location? returnPoint, InventoryPlan? inventory = null);
    Character CommitTeleport(AccountOwners.Lease owner, DfoServer.Game.Town.TeleportPlan plan);
    Character CommitLearning(AccountOwners.Lease owner, Character expected, CharacterLearningState learning, int? totalSp = null, int? totalTp = null);
    Character CommitSkillLayout(AccountOwners.Lease owner, Character expected, CharacterLearningState learning);
    Character UpdateProgress(AccountOwners.Lease owner, CharacterProgressEdit edit);
    Character AdvanceAcceptedStoryLevel(AccountOwners.Lease owner, Character expected);
    Character ResetProgress(AccountOwners.Lease owner, CharacterProgressResetPlan plan);
    Character CommitClassChange(AccountOwners.Lease owner, ClassChangePlan plan);
    int LoadGrowUpChangeCount(AccountOwners.Lease owner, ushort characterId);
    GrowUpChangeCommit CommitGrowUpChange(AccountOwners.Lease owner, GrowUpChangePlan plan);
    DfoServer.Game.ExpertJob.ExpertJobTradeCommit CommitExpertJobTrade(AccountOwners.Lease customerLease, AccountOwners.Lease ownerLease, DfoServer.Game.ExpertJob.ExpertJobTradePlan plan);
    Character CommitExpertJobAction(AccountOwners.Lease owner, DfoServer.Game.ExpertJob.ExpertJobActionPlan plan);
    Character CommitExpertJobLearning(AccountOwners.Lease owner, DfoServer.Game.ExpertJob.ExpertJobLearningPlan plan);
    Character CommitExpertJobClear(AccountOwners.Lease owner, Character expected);
    Character CommitExpertJobCraft(AccountOwners.Lease owner, Character expected, InventoryPlan inventory, int experience, QuestLifecyclePlan? tutorial = null);
    Character CommitExpertJobExperience(AccountOwners.Lease owner, Character expected, int experience);
    Character CommitExpertJobGiveUp(AccountOwners.Lease owner, Character expected, InventoryPlan wallet, int giveUpState, CharacterLearningState learning);
    Character CommitExpertJobMachine(AccountOwners.Lease owner, Character expected, InventoryPlan wallet, int grade, int endurance);
    Character CommitExperienceConsumable(AccountOwners.Lease owner, ExperienceConsumablePlan plan, DateTimeOffset now);
    Character CommitPermanentGrowth(AccountOwners.Lease owner, PermanentGrowthPlan plan, DateTimeOffset now);
    Character CommitLegacyStatusConsumable(AccountOwners.Lease owner, LegacyStatusConsumablePlan plan, DateTimeOffset now);
    Character CommitFatigueConsumable(AccountOwners.Lease owner, FatigueConsumablePlan plan, DateTimeOffset now);
    Character ReserveDungeonAttempt(AccountOwners.Lease owner, Character expected, uint nextObjectKey,
        WeeklyDungeonEntry? weekly = null);
    // #510/#511 入场票、入场一次性疲劳与尝试号预留同一写事务提交。
    DungeonEntryCommit CommitDungeonEntry(AccountOwners.Lease owner, Character expected, uint nextObjectKey, DungeonEntryCharge charge);
    Guid? LoadOrdinaryLukeDropEvent(AccountOwners.Lease owner, ushort characterId, int day, int sourceMonsterId);
    OrdinaryLukeProgress LoadOrdinaryLukeProgress(AccountOwners.Lease owner, ushort characterId);
    GriefTowerProgress ReadGriefTower(AccountOwners.Lease owner, ushort characterId);
    DungeonSpecialEntryUsage LoadSpecialEntryUsage(AccountOwners.Lease owner, Character expected, int day, DungeonSpecialEntryKind kind);
    DungeonDespairProgress LoadDespairProgress(AccountOwners.Lease owner, Character expected);
    DungeonDespairClearCommit CommitDungeonDespairClear(AccountOwners.Lease owner, SinglePlayerDungeonRun run,
        PreparedDungeonDespairClear prepared);
    DungeonAltarSettlementCommit CommitDungeonAltarSettlement(AccountOwners.Lease owner, SinglePlayerDungeonRun run,
        PreparedDungeonAltarSettlement prepared, CharacterGrowthCatalog growth);
    QuestClearReceipt? LoadQuestClear(AccountOwners.Lease owner, Character expected, ushort questId);
    QuestCompletionCommit CommitQuestCompletion(AccountOwners.Lease owner, QuestCompletionPlan plan);
    QuestLifecycleCommit CommitQuestLifecycle(AccountOwners.Lease owner, QuestLifecyclePlan plan);
    QuestQuickClearCommit CommitQuestQuickClear(AccountOwners.Lease owner, QuestQuickClearPlan plan);
    ImmutableArray<uint> CommitQuestEligibilityMail(AccountOwners.Lease owner, QuestEligibilityMailPlan plan);
    Character AdvanceQuestCycle(AccountOwners.Lease owner, Character expected, int day, QuestAutoDailyPlan? daily = null);
    DungeonGrowthCommit CommitDungeonGrowth(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run,
        PreparedDungeonDeath prepared, DungeonExperienceReward reward, CharacterGrowthCatalog catalog, FrozenDungeonGroundDeath? loot = null, QuestDropPlan? questDrops = null);
    DungeonSettlementCommit CommitDungeonSettlement(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run,
        PreparedDungeonSettlement prepared, CharacterGrowthCatalog catalog, InventoryPlan? cardInventory = null, bool mailCardItem = false,
        DungeonCardDelivery? premiumDelivery = null, bool grantLuckyStar = false, int luckyStarLimit = 0,
        bool recommendedClear = false);
    DungeonPickupCommit CommitDungeonPickup(AccountOwners.Lease owner, Character expected,
        DungeonGroundPickupPreparation prepared, InventoryPlan plan, DfoServer.GameWorld.Items.EpicPieceCatalog? pieces = null);
    Character CommitDungeonDiscard(AccountOwners.Lease owner, DungeonInventoryDiscardPreparation prepared);
    Character CommitInventory(AccountOwners.Lease owner, Character expected, InventoryPlan plan);
    Character CommitSkillSwitchInventory(AccountOwners.Lease owner, SkillSwitchInventoryPlan plan);
    Character CommitPvpTrainingSkills(AccountOwners.Lease owner,DfoServer.Game.Pvp.PvpTrainingSkillPlan plan);
    Character CommitEpicProduction(AccountOwners.Lease owner,DfoServer.Game.EpicProduction.EpicProductionPlan plan);
    Character CommitCollectBox(AccountOwners.Lease owner, DfoServer.Game.CollectBox.CollectBoxPlan plan);
    Character CommitTitleAchievement(AccountOwners.Lease owner, DfoServer.Game.Titles.TitleAchievementPlan plan);
    InventorySnapshot ReadPublicTitleBook(AccountOwners.Lease owner, ushort characterId);
    DfoServer.Game.Dungeons.AssaultDailyCounts LoadAssaultDaily(DfoServer.Game.Accounts.AccountOwners.Lease owner, ushort characterId, int day);
    DfoServer.Game.Dungeons.AssaultDailyCommit CommitAssaultClear(DfoServer.Game.Accounts.AccountOwners.Lease owner, Character expected, int day, bool hidden, DfoServer.Game.Inventory.Models.InventoryPlan? reward);
    // DungeonIncountInfo 角色日/周与账号进入次数；只有 SQLite 仓库实现，测试包装默认不支持。
    DungeonIncountUsage LoadDungeonIncount(AccountOwners.Lease owner, ushort characterId, int dungeon, DungeonIncountPeriod now) =>
        throw new NotSupportedException("仓库不支持副本进入次数");
    DungeonIncountCommit? CommitDungeonIncountEntry(AccountOwners.Lease owner, Character expected, int dungeon, DungeonIncountPeriod now,
        DfoServer.GameWorld.Dungeons.DungeonIncountRow? row, InventoryPlan? consumption,
        System.Collections.Immutable.ImmutableArray<AccountIncountCharge> account = default, bool increment = true, bool check = true) =>
        throw new NotSupportedException("仓库不支持副本进入次数");
    int LoadAccountDungeonIncount(AccountOwners.Lease owner, int dungeon, int period) => throw new NotSupportedException("仓库不支持账号副本进入次数");
    DfoServer.Game.Dungeons.CrackDailyState LoadCrackDaily(DfoServer.Game.Accounts.AccountOwners.Lease owner, ushort characterId, int day);
    DfoServer.Game.Dungeons.CrackDailyCommit CommitCrackClear(DfoServer.Game.Accounts.AccountOwners.Lease owner, Character expected, int day, DfoServer.Game.Dungeons.CrackDailyState state, DfoServer.Game.Inventory.Models.InventoryPlan? reward);
}
