using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 统一业务入口：只读规划完成后才提交。在线真源为当前角色的不可变 InventorySnapshot。
internal sealed class InventoryService(ItemCatalog catalog, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
    PremiumContractCatalog? contracts = null, TimeProvider? clock = null)
{
    readonly TimeProvider time = clock ?? TimeProvider.System;

    // 在线入口在owner锁外预读至多两端及六个快捷格；规划中的Get只命中共享模板缓存。
    public void PrepareMove(Character character, InventoryMoveRequest request)
    {
        if (!QuickSlotEquipmentRules.IsQuickSlot(request.Source) && !QuickSlotEquipmentRules.IsQuickSlot(request.Destination)) return;
        foreach (var item in character.Inventory.Items)
            if (item.Core.Kind == ItemKind.Equipment && (item.Position == request.Source || item.Position == request.Destination ||
                QuickSlotEquipmentRules.IsQuickSlot(item.Position)))
                _ = catalog.Get(item.Core.ItemId);
    }

    public InventoryResult Move(Character character, InventoryMoveRequest request, bool loginWearRestatement = false,
        bool allowQuickSlotEquipmentChange = true) =>
        // 已选中装扮预设时，穿戴/卸下在同一事务内实时回写该预设。
        AvatarPresetService.SyncActive(InventoryMoveService.Plan(character, request, catalog,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), contracts, loginWearRestatement, allowQuickSlotEquipmentChange));
    public InventoryResult Sort(Character character, ItemKind kind, byte condition) => InventorySortService.Plan(character.Inventory, kind, condition, catalog, time.GetUtcNow().ToUnixTimeSeconds());
    public InventoryResult Sort(Character character, InventorySortRequest request) => InventorySortService.Plan(character.Inventory,
        request.Kind, request.Condition, catalog, time.GetUtcNow().ToUnixTimeSeconds(), request.List);
    public InventoryResult Grant(Character character, IReadOnlyList<InventoryReward> rewards) =>
        InventoryRewardGrantService.TryGrantBatch(character, rewards, catalog, goldLimits, pieces, contracts: contracts);
    public InventoryResult Delete(Character character, InventoryDeleteRequest request, bool preserveMaterials = false) =>
        InventoryDeleteService.PlanBatch(character.Inventory, request, preserveMaterials, time);
    public InventoryResult DecreaseDurability(Character character, byte slot)
    {
        // 类型31由实际疲劳消费统一扣能量；兼容客户端通用48上报，不重复扣或触发免费修理。
        if (slot == QuickSlotEquipmentDefinition.EquipmentType)
            return new(true, 0, new(character.Inventory, character.Inventory, "quick_slot_energy_fatigue_owned"));
        var source = character.Inventory.Find(new(InventoryListType.Equipment, slot));
        if (source is null || source.Core.Kind != ItemKind.Equipment)
            return InventoryResult.Reject();
        var now = time.GetUtcNow();
        var template = catalog.Get(source.Core.ItemId);
        var wear = InventoryDurabilityService.Plan(character, slot, template, now);
        if (template.Durability == 0 || template.EquipmentSlot is 13 or 24 or 30 ||
            wear.Plan is not { } wearPlan || wearPlan.After.Find(source.Position)?.Core.Durability != 0)
            return wear;
        var repaired = InventoryRepairService.Automatic(character with { Inventory = wearPlan.After }, catalog, now, time);
        if (repaired.Plan is not { } repairPlan) return wear;
        // 损耗、全部修理及契约次数一次提交，不能先存零耐久再独立扣次。
        return new(true, 0, new(character.Inventory, repairPlan.After, "equipment_durability_auto_repair",
            new(character, repairPlan.CharacterPrecondition!.QualificationExpiresAt, time, requirePosition: true)));
    }
    public (InventoryDiscardPlan? Plan, ushort Error) Discard(Character character, InventoryDiscardRequest request)
    {
        if (request.Slot > short.MaxValue || character.Inventory.Find(new((InventoryListType)request.List, (short)request.Slot)) is not { } source)
            return (null, 17);
        return InventoryDiscardPlan.Prepare(character, request, catalog.Get(source.Core.ItemId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
    public InventoryResult UseGuardianGem(Character character, GuardianGemUseRequest request) =>
        InventoryGuardianGemService.Plan(character, request, catalog, time.GetUtcNow().ToUnixTimeSeconds(), time);
    public InventoryResult Lock(Character character, InventorySlot slot, InventoryLockOperation operation) =>
        InventoryLockService.Plan(character.Inventory, slot, operation, catalog, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public static Character Commit(ICharacterRepository repository, AccountOwners.Lease owner, Character character, InventoryResult result) => owner.Execute(() =>
    {
        if (!result.Accepted || result.Plan is null || !result.Plan.Changed && result.Plan.CharacterPrecondition is null)
            return character;
        // Repository 内部重验 generation/revision，同一事务保存核心、槽位与审计；失败不发布规划快照。
        return repository.CommitInventory(owner, character, result.Plan);
    });
}
