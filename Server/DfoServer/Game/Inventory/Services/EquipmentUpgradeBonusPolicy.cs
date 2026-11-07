using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal readonly record struct EquipmentUpgradeBonus(double RelativeRate, double ProbabilityIncrease,
    double CostDiscount, long? ExpiresAt)
{
    public int SuccessWeight(int basis, int denominator)
    {
        if (denominator is not (10000 or 100000) || basis < 0 || basis > denominator)
            throw new ArgumentOutOfRangeException(nameof(basis));
        // 新词条按万分制相对加成；旧词条沿旧端直接减少十万分制失败权重，不能混成同一单位。
        double value = basis * (1 + RelativeRate / 10000) + ProbabilityIncrease * denominator / 100000;
        return (int)Math.Clamp(Math.Floor(value), 0, denominator);
    }

    public int GoldCost(int basis)
    {
        if (basis < 0) throw new ArgumentOutOfRangeException(nameof(basis));
        return checked((int)Math.Max(0, Math.Floor(basis * (1 - CostDiscount / 100) + 0.5)));
    }

    public int RefinementMaterials(int basis)
    {
        if (basis <= 0) throw new ArgumentOutOfRangeException(nameof(basis));
        return checked((int)Math.Max(1, Math.Floor(basis * (1 - CostDiscount / 100))));
    }
}

internal static class EquipmentUpgradeBonusPolicy
{
    static bool AttributeSlot(InventorySlot slot) =>
        slot.List == InventoryListType.Equipment && slot.Index is not (11 or 33) || ItemSlotBoundService.IsEquippedRune(slot);

    // 在owner门外准备当前快照；规划与提交只能读取已装载的模板。
    public static void Prepare(Character character, ItemCatalog items)
    {
        foreach (var item in character.Inventory.Items)
            if (AttributeSlot(item.Position) && !items.TryGetLoaded(item.Core.ItemId, out _))
                items.TryGet(item.Core.ItemId, out _);
    }

    public static EquipmentUpgradeBonus Capture(Character character, ItemCatalog items, EquipmentUpgradeKind kind,
        long now, bool includeCost = true)
    {
        double relative = 0, increase = 0, discount = 0;
        long? deadline = null;
        foreach (var item in character.Inventory.Items)
        {
            if (!AttributeSlot(item.Position)) continue;
            if (!items.TryGetLoaded(item.Core.ItemId, out var template))
            {
                if (items.Contains(item.Core.ItemId)) throw new InvalidDataException("改造加成的穿戴模板尚未预加载");
                continue;
            }
            if (template.Kind != item.Core.Kind || template.CreatureEgg || item.IsExpired(now) && !template.UsableExpired)
                continue;
            var bonus = template.UpgradeBonuses;
            double rate = kind switch
            {
                EquipmentUpgradeKind.Reinforcement => bonus.ReinforcementRate,
                EquipmentUpgradeKind.Amplification => bonus.AmplificationRate,
                _ => 0
            };
            double flat = kind == EquipmentUpgradeKind.Refinement ? 0 : bonus.ProbabilityIncrease;
            double cost = !includeCost ? 0 : kind == EquipmentUpgradeKind.Amplification
                ? bonus.AmplificationDiscount : bonus.ReinforcementDiscount;
            if (rate == 0 && flat == 0 && cost == 0) continue;
            relative += rate;
            increase += flat;
            discount += cost;
            if (!template.UsableExpired && item.ExpireTime > 0)
                deadline = Math.Min(deadline ?? long.MaxValue, item.ExpireTime);
        }
        return new(relative, increase, discount, deadline);
    }
}
