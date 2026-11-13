using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Characters;

internal sealed record FatigueConsumablePlan(Character Expected, InventoryItem Source, FatigueConsumableDefinition Definition,
    InventoryPlan Inventory, CharacterFatigueState Fatigue);

internal sealed class FatigueConsumableService(ItemCatalog items, TimeProvider? clock = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public FatigueConsumablePlan Prepare(Character expected, InventorySlot slot)
    {
        var now = Clock.GetUtcNow();
        var source = expected.Inventory.Find(slot);
        if (slot.List != InventoryListType.Main || source is null || source.Uid <= 0 || source.Core.Count <= 0 ||
            source.Core.Kind != ItemKind.Consumable || source.Core.EquipmentLockId != 0 || source.IsExpired(now.ToUnixTimeSeconds()) ||
            !ItemSlotBoundService.Allows(expected.Inventory, slot, ItemKind.Consumable))
            throw new InvalidDataException("疲劳药剂源实例无效");
        var definition = items.FatigueConsumable(source.Core.ItemId);
        if (definition is null || !definition.Item.AllowsActivation(expected.Level, expected.Job))
            throw new InvalidDataException("疲劳药剂资源或等级资格不符");
        if (expected.Inventory.ConsumableStates.Any(state => state.Kind == ConsumableStateKind.Cooldown &&
            state.CoolGroup == definition.CoolGroup && state.Active(now.ToUnixTimeMilliseconds())))
            throw new InvalidDataException("疲劳药剂的持久冷却尚未结束");
        var fatigue = Recover(expected.Fatigue, definition.Amount);
        var deletion = InventoryDeleteService.Plan(expected.Inventory, slot, source.Core.Value, 1);
        if (!deletion.Accepted) throw new InvalidDataException("疲劳药剂不能消费一件");
        var cooldown = new RecoveryConsumableDefinition(definition.Item.Id, definition.Item.MinimumLevel, definition.CoolTime, definition.CoolGroup)
            { CooltimeMaintenance = definition.CooltimeMaintenance };
        var after = ConsumableStatePolicy.Activate(deletion.Plan!.After, cooldown, now.ToUnixTimeMilliseconds())
            ?? throw new InvalidDataException("疲劳药剂冷却状态超过容量");
        long? deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        if (definition.DailyLimit > 0)
        {
            // 复用按角色、STK模板记录的日次数账本；历史列名仍为daily_booster_usage。
            if (!DailyBoosterUsage.TryConsume(after.DailyBoosterUses, source.Core.ItemId, 1, definition.DailyLimit, now, out var uses))
                throw new InvalidDataException("疲劳药剂达到资源声明的每日使用次数");
            after = after with { DailyBoosterUses = uses };
            var nextDay = DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds();
            deadline = deadline is null ? nextDay : Math.Min(deadline.Value, nextDay);
        }
        return new(expected, source, definition, new(expected.Inventory, after, "use_fatigue_consumable",
            new(expected, deadline, Clock, requirePosition: true)), fatigue);
    }

    internal static CharacterFatigueState Recover(CharacterFatigueState? before, int amount)
    {
        if (before is null || amount <= 0) throw new InvalidDataException("疲劳恢复必须有实际owner及正增量");
        before.Validate();
        if (before.Used < amount || (long)before.Remaining + amount > before.Maximum)
            throw new InvalidDataException("疲劳药剂不能完整恢复，不扣物或截断恢复量");
        return before with { Used = checked((ushort)(before.Used - amount)), Remaining = checked((ushort)(before.Remaining + amount)) };
    }
}
