using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryExpansionService(ItemCatalog items, TimeProvider? clock = null)
{
    // 静态注册未发现430专用错误消费者，保留公共拒绝形状，不杜撰业务错误码。
    public const ushort Rejection = 0;
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public InventoryResult Plan(Character character, ushort slot)
    {
        var source = slot <= short.MaxValue ? character.Inventory.Find(new(InventoryListType.Main, (short)slot)) : null;
        InventoryExpansionDefinition? definition = source is null ? null : items.InventoryExpansion(source.Core.ItemId);
        return Plan(character, slot, definition, clock.GetUtcNow().ToUnixTimeSeconds(), clock);
    }

    internal static InventoryResult Plan(Character character, ushort slot, InventoryExpansionDefinition? definition,
        long now, TimeProvider? clock = null)
    {
        var before = character.Inventory;
        if (slot > short.MaxValue || before.Expansion is not (0 or 8 or 16) || definition is null)
            return InventoryResult.Reject(Rejection);
        var position = new InventorySlot(InventoryListType.Main, (short)slot);
        var source = before.Find(position);
        var template = definition.Item;
        if (source is null || source.Uid <= 0 || source.Core.Kind != ItemKind.Consumable || source.Core.Count <= 0 ||
            source.Core.EquipmentLockId != 0 || source.IsExpired(now) || source.Core.ItemId != template.Id ||
            template.Kind != source.Core.Kind || !ItemSlotBoundService.Allows(before, position, source.Core.Kind) ||
            !template.AllowsActivation(character.Level, character.Job) || !InventoryMoveService.RequiredSkillSatisfied(character, template))
            return InventoryResult.Reject(Rejection);
        var consumed = InventoryDeleteService.Plan(before, position, source.Core.ItemId, 1);
        if (!consumed.Accepted || consumed.Plan is null)
            return InventoryResult.Reject(Rejection);
        var after = consumed.Plan.After with { Expansion = checked((ushort)(before.Expansion + 8)) };
        return new(true, 0, new(before, after, "inventory_expansion_coupon",
            new(character, source.ExpireTime > 0 ? source.ExpireTime : null, clock)));
    }
}
