using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class ConsumableStatePolicy
{
    public static bool AllowsTownGift(DfoServer.Game.Characters.Character source, DfoServer.Game.Characters.Character target) =>
        target.Id != source.Id && target.Level < 70 && Math.Abs(source.Position.X - target.Position.X) <= 300 &&
        Math.Abs(source.Position.Y - target.Position.Y) <= 300;

    // 本版该道具没有独立效果标签，服务端行为按明确的物品编号绑定，不按名称或图标猜测。
    internal const int StaminaRecoveryItem = ConsumableSpecialItems.StaminaRecoveryItem;

    public static bool RecoversStamina(InventorySnapshot inventory, long now) => inventory.ConsumableStates.Any(row =>
        row.Kind == ConsumableStateKind.Effect && ConsumableSpecialItems.IsStaminaRecovery(row.ItemId) && row.Active(now));

    public static InventorySnapshot? Activate(InventorySnapshot inventory, RecoveryConsumableDefinition definition, long now, bool sessionCooldown = false)
    {
        if (definition.EffectMilliseconds <= 0 && (definition.CoolTime <= 0 || definition.CooltimeMaintenance == 0 && !sessionCooldown))
            return inventory;
        var rows = inventory.ConsumableStates.Where(row => row.Active(now)).ToList();
        // 四种神圣祝福共用一次资格；再次使用覆盖截止，不叠加次数或保留另一种药的旧资格。
        if (definition.RevivalBlessing)
            rows.RemoveAll(row => row.Kind == ConsumableStateKind.Effect && ConsumableSpecialItems.IsRevivalBlessing(row.ItemId));
        if (ConsumableSpecialItems.IsStaminaRecovery(definition.ItemId))
            rows.RemoveAll(row => row.Kind == ConsumableStateKind.Effect && row.ItemId != definition.ItemId &&
                ConsumableSpecialItems.IsStaminaRecovery(row.ItemId));
        // 固定同族药剂覆盖效果期限，不叠加倍率；家族成员只来自已验证的资源映射。
        if (definition.EpicDropBuff is { } epic)
            rows.RemoveAll(row => row.Kind == ConsumableStateKind.Effect && row.ItemId != definition.ItemId && epic.IsFamilyItem(row.ItemId));
        if (definition.EnhancementPotion is { } enhancement)
            rows.RemoveAll(row => row.Kind == ConsumableStateKind.Effect && row.ItemId != definition.ItemId &&
                enhancement.IsFamilyItem(row.ItemId));
        void Put(ConsumableStateKind kind, int duration, byte maintenance)
        {
            if (duration <= 0) return;
            long until = checked(now + duration);
            var old = rows.Find(row => row.Kind == kind && row.ItemId == definition.ItemId);
            if (old is not null) until = Math.Max(until, old.UntilMilliseconds);
            rows.RemoveAll(row => row.Kind == kind && (row.ItemId == definition.ItemId ||
                kind == ConsumableStateKind.Cooldown && row.CoolGroup == definition.CoolGroup));
            rows.Add(new(kind, definition.ItemId, until, maintenance, definition.CoolGroup));
        }
        Put(ConsumableStateKind.Effect, definition.EffectMilliseconds, definition.EffectMaintenance);
        if (definition.CooltimeMaintenance != 0 || sessionCooldown)
            Put(ConsumableStateKind.Cooldown, definition.CoolTime, definition.CooltimeMaintenance);
        var states = rows.OrderBy(row => row.Kind).ThenBy(row => row.ItemId).ToImmutableArray();
        if (states.GroupBy(row => row.Kind).Any(group => group.Count() > byte.MaxValue)) return null;
        ConsumableState.Validate(states);
        var after = inventory with { ConsumableStates = states };
        return RecoversStamina(after, now) && after.StaminaUntil != 0 ? after with { StaminaUntil = 0 } : after;
    }

    public static InventorySnapshot CrossBoundary(InventorySnapshot inventory, ConsumableMaintenance boundary, long now)
    {
        var states = inventory.ConsumableStates.Where(row => row.Active(now) && row.Preserves(boundary)).ToImmutableArray();
        return states.SequenceEqual(inventory.ConsumableStates) ? inventory : inventory with { ConsumableStates = states };
    }
}
