using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryEquipmentUpgradeService(ItemCatalog items, EquipmentUpgradeCatalog upgrades,
    EpicPieceCatalog pieces)
{
    public void PrepareImprint(InventoryItem? source, EquipmentFusionRequest request)
    {
        _ = items.Get(request.SourceId);
        _ = items.Get(request.TargetId);
        if (source?.Core.ItemId != request.SourceId) return;
        foreach (int id in new[] { source.Core.EnchantCardId, source.Core.Emblem1, source.Core.Emblem2 })
            if (id != 0) _ = items.Get(id);
    }

    public InventoryResult Upgrade(Character character, EquipmentUpgradeRequest request,
        DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var before = character.Inventory;
        var source = before.Find(new(InventoryListType.Main, request.Slot));
        if (request.Slot < 0 || source is null || source.Uid <= 0 || source.Core.ItemId != request.ItemId ||
            source.Core.Kind != ItemKind.Equipment || source.Core.IsRental || source.IsExpired(now.ToUnixTimeSeconds()) ||
            !upgrades.Upgrades.TryGetValue(request.ItemId, out var route) || !route.Targets.Contains(request.TargetId) ||
            !upgrades.Groups.TryGetValue(route.Group, out var group)) return InventoryResult.Reject(1);
        if (group.InheritAbility) return Convert(character, request.Slot, request.ItemId, request.TargetId, now);
        if (source.Core.EquipmentLockId != 0) return InventoryResult.Reject(0xD6);
        if (request.Upgrade > source.Core.Upgrade || request.GenuineUpgrade > source.Core.GenuineUpgrade ||
            request.Amplify != (source.Core.AmplifyType != 0) ||
            items.Get(request.ItemId).EquipmentSlot != items.Get(request.TargetId).EquipmentSlot)
            return InventoryResult.Reject(1);
        var costs = EquipmentUpgradeCosts.Calculate(group, false, request.Upgrade, request.GenuineUpgrade,
            request.InheritEnchant && source.Core.EnchantCardId != 0 ? items.Get(source.Core.EnchantCardId).Rarity : null,
            request.InheritEmblems ? new[] { source.Core.Emblem1, source.Core.Emblem2 }.Where(id => id != 0)
                .Select(id => items.Get(id).Rarity).ToArray() : []);
        var paid = InventoryMaterialConsumptionService.Plan(before,
            costs.Where(cost => cost.ItemId != 0).ToArray(), pieces, now);
        long gold = costs.Where(cost => cost.ItemId == 0).Sum(cost => (long)cost.Count);
        if (!paid.Accepted || CurrencyWallet.Get(paid.Plan!.After, CurrencyKind.Gold) < gold)
            return InventoryResult.Reject(0x11);
        var after = CurrencyWallet.Set(paid.Plan.After, CurrencyKind.Gold,
            CurrencyWallet.Get(paid.Plan.After, CurrencyKind.Gold) - gold);
        var upgraded = source.Core with
        {
            ItemId = request.TargetId,
            Attr = (byte)((source.Core.Attr & 0xE0) | request.Upgrade),
            GenuineUpgrade = request.GenuineUpgrade,
            EnchantCardId = request.InheritEnchant ? source.Core.EnchantCardId : 0,
            EnchantUpgrade = request.InheritEnchant ? source.Core.EnchantUpgrade : (byte)0,
            EmblemSocketCount = request.InheritEmblems ? source.Core.EmblemSocketCount : (byte)0,
            Emblem1 = request.InheritEmblems ? source.Core.Emblem1 : 0,
            Emblem2 = request.InheritEmblems ? source.Core.Emblem2 : 0
        };
        after = after with { Items = after.Items.Replace(source, source with { Core = upgraded }) };
        return new(true, 0, new InventoryPlan(before, after, "equipment_upgrade"));
    }

    public InventoryResult Imprint(Character character, EquipmentFusionRequest request,
        DateTimeOffset? utcNow = null, bool weaponFusion = false)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var before = character.Inventory;
        var source = before.Find(new(InventoryListType.Main, request.SourceSlot));
        var target = before.Find(new(InventoryListType.Main, request.TargetSlot));
        if (request.SourceSlot < 0 || request.TargetSlot < 0 || source is null || target is null ||
            source.Uid <= 0 || target.Uid <= 0 || source.Uid == target.Uid ||
            source.Core.ItemId != request.SourceId || target.Core.ItemId != request.TargetId ||
            source.Core.Kind != ItemKind.Equipment || target.Core.Kind != ItemKind.Equipment ||
            source.Core.IsRental || target.Core.IsRental || source.IsExpired(now.ToUnixTimeSeconds()) ||
            target.IsExpired(now.ToUnixTimeSeconds()))
            return InventoryResult.Reject(1);
        EquipmentUpgradeGroup? group = null;
        if (weaponFusion)
        {
            if (!upgrades.WeaponImprints.TryGetValue(request.SourceId, out int targetId) || targetId != request.TargetId)
                return InventoryResult.Reject(1);
        }
        else if (!upgrades.Fusions.TryGetValue(request.SourceId, out var route) ||
            !route.Targets.Contains(request.TargetId) || !upgrades.Groups.TryGetValue(route.Group, out group))
            return InventoryResult.Reject(1);
        if (source.Core.EquipmentLockId != 0 || target.Core.EquipmentLockId != 0)
            return InventoryResult.Reject(0xD6);
        if (items.Get(request.SourceId).EquipmentSlot != items.Get(request.TargetId).EquipmentSlot)
            return InventoryResult.Reject(1);

        var costs = weaponFusion ? upgrades.WeaponImprintMaterials.ToArray() :
            EquipmentUpgradeCosts.Calculate(group!, true, source.Core.Upgrade, source.Core.GenuineUpgrade,
            source.Core.EnchantCardId == 0 ? null : items.Get(source.Core.EnchantCardId).Rarity,
            new[] { source.Core.Emblem1, source.Core.Emblem2 }.Where(id => id != 0)
                .Select(id => items.Get(id).Rarity).ToArray());
        var paid = InventoryMaterialConsumptionService.Plan(before,
            costs.Where(cost => cost.ItemId != 0).ToArray(), pieces, now);
        long gold = costs.Where(cost => cost.ItemId == 0).Sum(cost => (long)cost.Count);
        if (!paid.Accepted || CurrencyWallet.Get(paid.Plan!.After, CurrencyKind.Gold) < gold)
            return InventoryResult.Reject(0x11);
        var after = CurrencyWallet.Set(paid.Plan.After, CurrencyKind.Gold,
            CurrencyWallet.Get(paid.Plan.After, CurrencyKind.Gold) - gold);
        // 目标实例、品级和材质不变；打造逐字段覆盖，不能取高值，也不能复制源完整核心。
        var inherited = target.Core with
        {
            Attr = (byte)((target.Core.Attr & 0xE0) | source.Core.Upgrade),
            AmplifyType = source.Core.AmplifyType,
            AmplifyValue = source.Core.AmplifyValue,
            GenuineUpgrade = source.Core.GenuineUpgrade,
            EnchantCardId = source.Core.EnchantCardId,
            EnchantUpgrade = source.Core.EnchantUpgrade,
            EmblemSocketCount = source.Core.EmblemSocketCount,
            Emblem1 = source.Core.Emblem1,
            Emblem2 = source.Core.Emblem2
        };
        after = after with { Items = after.Items.Remove(source).Replace(target, target with { Core = inherited }) };
        return new(true, 0, new InventoryPlan(before, after, weaponFusion ? "equipment_weapon_fusion" : "equipment_imprint"));
    }

    // 权能转换使用 [inherit ability] 组；完整保留实例打造，只替换表内同部位物品编号。
    public InventoryResult Convert(Character character, short slot, int itemId, int targetId,
        DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var before = character.Inventory;
        var source = before.Find(new(InventoryListType.Main, slot));
        if (slot < 0 || source is null || source.Core.ItemId != itemId || source.Core.Kind != ItemKind.Equipment ||
            source.Core.IsRental || source.IsExpired(now.ToUnixTimeSeconds()) ||
            !upgrades.Upgrades.TryGetValue(itemId, out var route) || !route.Targets.Contains(targetId) ||
            !upgrades.Groups.TryGetValue(route.Group, out var group) || !group.InheritAbility)
            return InventoryResult.Reject(1);
        if (source.Core.EquipmentLockId != 0) return InventoryResult.Reject(0xD6);
        ItemTemplate original, replacement;
        try
        {
            original = items.Get(itemId);
            replacement = items.Get(targetId);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
        {
            return InventoryResult.Reject(1);
        }
        if (original.Kind != ItemKind.Equipment || replacement.Kind != ItemKind.Equipment ||
            original.EquipmentSlot != replacement.EquipmentSlot)
            return InventoryResult.Reject(1);

        var paid = InventoryMaterialConsumptionService.Plan(before,
            group.Materials.Where(cost => cost.ItemId != 0).ToArray(), pieces, now);
        if (!paid.Accepted) return InventoryResult.Reject(0x11);
        long gold = group.Materials.Where(cost => cost.ItemId == 0).Sum(cost => (long)cost.Count);
        var after = paid.Plan!.After;
        if (CurrencyWallet.Get(after, CurrencyKind.Gold) < gold) return InventoryResult.Reject(0x11);
        after = CurrencyWallet.Set(after, CurrencyKind.Gold, CurrencyWallet.Get(after, CurrencyKind.Gold) - gold);
        after = after with
        {
            Items = after.Items.Replace(source, source with { Core = source.Core with { ItemId = targetId } })
        };
        return new(true, 0, new InventoryPlan(before, after, "equipment_power_conversion"));
    }
}
