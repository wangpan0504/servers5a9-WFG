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
        // 同族资格按本次合法使用覆盖；不叠加倍率、次数或旧剩余时长。
        rows.RemoveAll(row => row.Kind == ConsumableStateKind.Effect && row.ItemId != definition.ItemId &&
            ReplacesEffect(definition, row.ItemId));
        void Put(ConsumableStateKind kind, int duration, byte maintenance)
        {
            if (duration <= 0) return;
            long until = checked(now + duration);
            var old = rows.Find(row => row.Kind == kind && row.ItemId == definition.ItemId);
            if (old is not null && kind == ConsumableStateKind.Cooldown) until = Math.Max(until, old.UntilMilliseconds);
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

    internal static bool ReplacesEffect(RecoveryConsumableDefinition definition, int itemId) =>
        definition.ItemId == itemId || definition.RevivalBlessing && ConsumableSpecialItems.IsRevivalBlessing(itemId) ||
        ConsumableSpecialItems.IsStaminaRecovery(definition.ItemId) && ConsumableSpecialItems.IsStaminaRecovery(itemId) ||
        definition.EpicDropBuff?.IsFamilyItem(itemId) == true || definition.HellLegendEpicDropBuff?.IsFamilyItem(itemId) == true ||
        definition.VillageMoveSpeedBuff?.IsFamilyItem(itemId) == true || definition.EnhancementPotion?.IsFamilyItem(itemId) == true ||
        definition.TreasureHunterPotion is not null && TreasureHunterPotionDefinition.FamilyItems.Contains(itemId);

    internal static int RemainingSeconds(long until, long now) => until <= now ? 0 :
        (int)Math.Min(int.MaxValue / 1000, (until - now - 1) / 1000 + 1);

    internal static ImmutableArray<ConsumableStateUpdate> RefreshEffects(RecoveryConsumableDefinition definition,
        IEnumerable<ConsumableState> before, ConsumableState? effect, long now, bool explicitTimeRefresh)
    {
        if (effect is null || definition.EffectMaintenance == 0) return [];
        var replaced = before.Where(row => row.Kind == ConsumableStateKind.Effect && row.Active(now) &&
            ReplacesEffect(definition, row.ItemId)).ToArray();
        if (replaced.Length == 0) return [];
        var updates = replaced.Where(row => row.ItemId != effect.ItemId && (row.Maintenance & 8) == 0)
            .Select(row => row.ItemId).Distinct().Select(id => new ConsumableStateUpdate(id, 0)).ToImmutableArray();
        // 成功ACK已按新编号建立独立计时，旧项0秒不会删掉新项；免扣错误0才需要显式起算。
        return explicitTimeRefresh
            ? updates.Add(new(effect.ItemId, RemainingSeconds(effect.UntilMilliseconds, now))) : updates;
    }
}
