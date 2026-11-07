using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Creatures;

internal sealed class CreatureSealService(ItemCatalog items, EpicPieceCatalog pieces, TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public InventoryResult Plan(Character character, short petSlot, int petItemId, short sourceSlot)
    {
        var source = character.Inventory.Find(new(InventoryListType.Main, sourceSlot));
        if (source is null) return InventoryResult.Reject(19);
        int output = items.CreatureCapsule(source.Core.ItemId, petItemId);
        if (output <= 0) return InventoryResult.Reject(19);
        return Plan(character, petSlot, petItemId, sourceSlot, items.Get(source.Core.ItemId), items.Get(output), pieces, Clock);
    }

    internal static InventoryResult Plan(Character character, short petSlot, int petItemId, short sourceSlot,
        ItemTemplate tool, ItemTemplate capsule, EpicPieceCatalog pieces, TimeProvider clock)
    {
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var before = character.Inventory;
        var source = before.Find(new(InventoryListType.Main, sourceSlot));
        var pet = before.Find(new(InventoryListType.Pet, petSlot));
        if (petSlot is < 0 or > 139 || source is not { Uid: > 0, Core.Kind: ItemKind.Consumable } ||
            pet is not { Uid: > 0, Core.Kind: ItemKind.Creature, Creature: not null, Avatar: null } ||
            pet.Core.ItemId != petItemId || pet.Core.Value != pet.Uid || source.Uid == pet.Uid ||
            pet.Core.EquipmentLockId != 0 || source.Core.EquipmentLockId != 0 ||
            pet.ExpireTime != 0 || source.IsExpired(now) || source.Core.Count < 1 ||
            !ItemSlotBoundService.Allows(before, source.Position, source.Core.Kind) ||
            !ItemSlotBoundService.Allows(before, pet.Position, pet.Core.Kind) ||
            source.Core.ItemId != tool.Id || !tool.AllowsActivation(character.Level, character.Job) ||
            !InventoryMoveService.RequiredSkillSatisfied(character, tool) ||
            capsule.Kind != ItemKind.Consumable || capsule.StackLimit != 1 ||
            capsule.AttachType.Replace("`", "").Trim() != "[trade limit]")
            return InventoryResult.Reject(19);
        pet.ValidateDetails(allocating: false);
        var consumed = InventoryDeleteService.Plan(before, source.Position, source.Core.ItemId, 1);
        if (!consumed.Accepted) return consumed;
        var removed = consumed.Plan!.After with { Items = consumed.Plan.After.Items.Remove(pet) };
        // 资源产物是固定宠物蛋礼包；不能把成宠详情塞入普通堆叠物品，开封沿用礼包合同。
        var core = InventoryRewardGrantService.Create(capsule, utcNow: clock.GetUtcNow()) with { Attr = 32 };
        var granted = InventoryRewardGrantService.TryGrantCore(character with { Inventory = removed }, core, capsule, pieces, clock.GetUtcNow());
        if (!granted.Accepted) return granted;
        return new(true, 0, new(before, granted.Plan!.After, "creature_seal",
            new(character, source.ExpireTime == 0 ? null : source.ExpireTime, clock)));
    }
}
