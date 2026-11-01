using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 仅规划源消费、冷却和日次数，具体余额或目标效果由所属玩法合入同一InventoryPlan。
internal static class InventoryActionConsumableService
{
    public static InventoryResult Plan(Character character, InventorySlot position, ActionConsumableDefinition definition,
        string place, TimeProvider clock, DateTimeOffset? utcNow = null, int count = 1, bool ignoreCooldown = false)
    {
        var now = utcNow ?? clock.GetUtcNow();
        var source = character.Inventory.Find(position);
        if (position.List != InventoryListType.Main || source is not { Uid: > 0, Core.Kind: ItemKind.Consumable } ||
            source.Core.ItemId != definition.Item.Id || count <= 0 || source.Core.Count < count || source.Core.EquipmentLockId != 0 ||
            source.IsExpired(now.ToUnixTimeSeconds()) || !ItemSlotBoundService.Allows(character.Inventory, position, source.Core.Kind) ||
            !definition.Item.AllowsUse(character.Level, character.Job) || !InventoryMoveService.RequiredSkillSatisfied(character, definition.Item) ||
            !definition.AllowsPlace(place) || !ignoreCooldown && character.Inventory.ConsumableStates.Any(row => row.Kind == ConsumableStateKind.Cooldown &&
                row.CoolGroup == definition.CoolGroup && row.Active(now.ToUnixTimeMilliseconds())))
            return InventoryResult.Reject(19);
        var removed = InventoryDeleteService.Plan(character.Inventory, position, source.Core.ItemId, count);
        if (!removed.Accepted) return removed;
        var after = ConsumableStatePolicy.Activate(removed.Plan!.After,
            new(definition.Item.Id, definition.Item.MinimumLevel, definition.CoolTime, definition.CoolGroup)
                { CooltimeMaintenance = definition.Maintenance, EffectMilliseconds = definition.EffectMilliseconds,
                    EffectMaintenance = definition.EffectMaintenance }, now.ToUnixTimeMilliseconds(), sessionCooldown: true);
        if (after is null) return InventoryResult.Reject(19);
        long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        if (definition.DailyLimit > 0)
        {
            if (!DailyBoosterUsage.TryConsume(after.DailyBoosterUses, source.Core.ItemId, 1, definition.DailyLimit, now, out var uses))
                return InventoryResult.Reject(19);
            after = after with { DailyBoosterUses = uses };
            deadline = Math.Min(deadline ?? long.MaxValue, DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds());
        }
        return new(true, 0, new(character.Inventory, after, "action_consumable_source", new(character, deadline, clock, requirePosition: true))
        { UsedItems = [new(source.Core.ItemId, 1)] });
    }
}
