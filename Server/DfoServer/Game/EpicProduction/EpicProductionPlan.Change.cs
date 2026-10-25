using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.EpicProduction;

internal sealed partial record EpicProductionPlan
{
    // 013675C0 选择一整套换型材料；013690EE 发送材料ID与实际槽，不发送扣除数量。
    // 成功消费者00CBAC36只更新武器，保留已积累进度、周次数和催化剂衰减。
    public static EpicProductionPlan? Change(Character character,int weaponId,
        IReadOnlyList<(int ItemId,short Slot)> materials,EpicProductionCatalog production,
        EpicPieceCatalog pieces,DateTimeOffset now)
    {
        var before=character.EpicProduction;
        before.Validate();
        if (character.Level<production.MinimumLevel || character.Job>=Jobs.Length || before.WeaponId==0 ||
            before.WeaponId==weaponId || before.Charge>=production.MaximumCharge ||
            !production.Weapons.TryGetValue(Jobs[character.Job],out var choices) ||
            !choices.Contains(before.WeaponId) || !choices.Contains(weaponId) || materials.Count==0 ||
            materials.Select(row=>row.ItemId).Distinct().Count()!=materials.Count ||
            materials.Select(row=>row.Slot).Distinct().Count()!=materials.Count) return null;
        var recipe=Matches(production.SpecialChangeItems)?production.SpecialChangeItems:
            Matches(production.ChangeItems)?production.ChangeItems:[];
        if (recipe.Length==0) return null;
        foreach (var row in materials)
        {
            if (CurrencyWallet.RewardKind(row.ItemId) is { } currency)
            {
                if (ItemSlotBoundService.CurrencySlot(currency)!=row.Slot) return null;
                continue;
            }
            var item=character.Inventory.Find(new(InventoryListType.Main,row.Slot));
            if (item is null || item.Core.ItemId!=row.ItemId || item.Core.IsRental ||
                item.Core.EquipmentLockId!=0 || item.IsExpired(now.ToUnixTimeSeconds())) return null;
        }
        // 数量只取资源定义；共用材料扣除支持合法分堆，不相信请求中的槽就是总余额。
        var cost=InventoryMaterialConsumptionService.Plan(character.Inventory,recipe,pieces,now);
        if (!cost.Accepted) return null;
        return new(character,before,before with { WeaponId=weaponId },
            new(character.Inventory,cost.Plan!.After,"epic_production_change"),false,false,false);

        bool Matches(IReadOnlyList<ItemMaterialCost> costs)=>costs.Count==materials.Count &&
            costs.All(cost=>materials.Any(row=>row.ItemId==cost.ItemId));
    }
}
