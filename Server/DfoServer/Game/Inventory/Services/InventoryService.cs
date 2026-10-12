using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 统一业务入口：只读规划完成后才提交。在线真源为当前角色的不可变 InventorySnapshot。
internal sealed class InventoryService(ItemCatalog catalog, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces)
{
    public InventoryResult Move(Character character, InventoryMoveRequest request) => InventoryMoveService.Plan(character, request, catalog, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public InventoryResult Sort(Character character, ItemKind kind, byte condition) => InventorySortService.Plan(character.Inventory, kind, condition, catalog, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public InventoryResult Sort(Character character, InventorySortRequest request) => InventorySortService.Plan(character.Inventory,
        request.Kind, request.Condition, catalog, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), request.List);
    public InventoryResult Grant(Character character, IReadOnlyList<InventoryReward> rewards) => InventoryRewardGrantService.TryGrantBatch(character, rewards, catalog, goldLimits, pieces);
    public InventoryResult Delete(Character character, InventoryDeleteRequest request, bool preserveClearCube = false) =>
        InventoryDeleteService.PlanBatch(character.Inventory, request, preserveClearCube);
    public (InventoryDiscardPlan? Plan, ushort Error) Discard(Character character, InventoryDiscardRequest request)
    {
        if (request.Slot > short.MaxValue || character.Inventory.Find(new((InventoryListType)request.List, (short)request.Slot)) is not { } source)
            return (null, 17);
        return InventoryDiscardPlan.Prepare(character, request, catalog.Get(source.Core.ItemId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
    public InventoryResult UseGuardianGem(Character character, GuardianGemUseRequest request) =>
        InventoryGuardianGemService.Plan(character.Inventory, request, catalog, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public InventoryResult Lock(Character character, InventorySlot slot, InventoryLockOperation operation) =>
        InventoryLockService.Plan(character.Inventory, slot, operation, catalog, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public static Character Commit(ICharacterRepository repository, AccountOwners.Lease owner, Character character, InventoryResult result) => owner.Execute(() =>
    {
        if (!result.Accepted || result.Plan is null || !result.Plan.Changed)
            return character;
        // Repository 内部重验 generation/revision，同一事务保存核心、槽位与审计；失败不发布规划快照。
        return repository.CommitInventory(owner, character, result.Plan);
    });
}
