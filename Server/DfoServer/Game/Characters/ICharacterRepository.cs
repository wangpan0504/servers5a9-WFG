using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Inventory.Models;
namespace DfoServer.Game.Characters;
// 角色用例依赖的持久化边界；SQLite、迁移与离线导入细节不进入 Game。
internal interface ICharacterRepository
{
    DfoServer.Game.Currency.AccountWallet LoadAccountWallet(AccountOwners.Lease owner);
    CharacterRosterEntry[] ListRoster(AccountOwners.Lease owner);
    Character LoadCharacter(AccountOwners.Lease owner, ushort slot);
    Character[] List(AccountOwners.Lease owner);
    bool NameExists(string name);
    Character Create(AccountOwners.Lease owner, NewCharacter character);
    Character Save(AccountOwners.Lease owner, Character expected, Location position, Location? returnPoint);
    Character CommitLearning(AccountOwners.Lease owner, Character expected, CharacterLearningState learning);
    Character UpdateProgress(AccountOwners.Lease owner, CharacterProgressEdit edit);
    Character CommitInventory(AccountOwners.Lease owner, Character expected, InventoryPlan plan);
}
