using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.Game.Quests;
namespace DfoServer.Game.Characters;
// 角色用例依赖的持久化边界；SQLite、迁移与离线导入细节不进入 Game。
internal interface ICharacterRepository
{
    Character ConsumeDungeonRoomFatigue(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run);
    CardMembership LoadCardMembership(AccountOwners.Lease owner,ushort characterId,DateTimeOffset now);
    CardMembership SetCardMembership(AccountOwners.Lease owner,string name,CardMembershipKind kind,long expires);
    DungeonPaidCardCommit CommitPaidDungeonCard(AccountOwners.Lease owner,Character expected,PreparedDungeonSettlement prepared,
        DungeonCardDelivery delivery,DateTimeOffset now);
    DfoServer.Game.Currency.AccountWallet LoadAccountWallet(AccountOwners.Lease owner);
    CharacterRosterEntry[] ListRoster(AccountOwners.Lease owner);
    Character LoadCharacter(AccountOwners.Lease owner, ushort slot);
    Character[] List(AccountOwners.Lease owner);
    bool NameExists(string name);
    Character Create(AccountOwners.Lease owner, NewCharacter character);
    CharacterDeletion LoadDeletionTarget(AccountOwners.Lease owner, ushort slot);
    CharacterDeletion Delete(AccountOwners.Lease owner, CharacterDeletion expected);
    Character Save(AccountOwners.Lease owner, Character expected, Location position, Location? returnPoint);
    Character CommitLearning(AccountOwners.Lease owner, Character expected, CharacterLearningState learning, int? totalSp = null, int? totalTp = null);
    Character CommitSkillLayout(AccountOwners.Lease owner, Character expected, CharacterLearningState learning);
    Character UpdateProgress(AccountOwners.Lease owner, CharacterProgressEdit edit);
    Character AdvanceAcceptedStoryLevel(AccountOwners.Lease owner, Character expected);
    Character ResetProgress(AccountOwners.Lease owner, CharacterProgressResetPlan plan);
    Character ReserveDungeonAttempt(AccountOwners.Lease owner, Character expected, uint nextObjectKey);
    QuestClearReceipt? LoadQuestClear(AccountOwners.Lease owner, Character expected, ushort questId);
    QuestCompletionCommit CommitQuestCompletion(AccountOwners.Lease owner, QuestCompletionPlan plan);
    QuestLifecycleCommit CommitQuestLifecycle(AccountOwners.Lease owner, QuestLifecyclePlan plan);
    QuestQuickClearCommit CommitQuestQuickClear(AccountOwners.Lease owner, QuestQuickClearPlan plan);
    Character AdvanceQuestCycle(AccountOwners.Lease owner, Character expected, int day);
    DungeonGrowthCommit CommitDungeonGrowth(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run,
        PreparedDungeonDeath prepared, DungeonExperienceReward reward, CharacterGrowthCatalog catalog, FrozenDungeonGroundDeath? loot = null, QuestDropPlan? questDrops = null);
    DungeonSettlementCommit CommitDungeonSettlement(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run,
        PreparedDungeonSettlement prepared, CharacterGrowthCatalog catalog, InventoryPlan? cardInventory = null, bool mailCardItem = false,
        DungeonCardDelivery? premiumDelivery = null);
    DungeonPickupCommit CommitDungeonPickup(AccountOwners.Lease owner, Character expected,
        DungeonGroundPickupPreparation prepared, InventoryPlan plan);
    Character CommitInventory(AccountOwners.Lease owner, Character expected, InventoryPlan plan);
}
