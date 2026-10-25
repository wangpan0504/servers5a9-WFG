using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.EpicProduction;

internal sealed partial record EpicProductionPlan
{
    public int CraftedLiquidId { get; init; }
    public int CraftedCount { get; init; }

    // 随机样本由服务端规划阶段提供；不接受客户端自报产量。普通/中级未满一份的材料余量持久化。
    public static EpicProductionPlan? Craft(Character character,int liquidId,
        IReadOnlyList<(int ItemId,short Slot,int Count)> materials,IReadOnlyList<int> outputRolls,
        EpicProductionCatalog production,ItemCatalog items,GoldLimitCatalog gold,EpicPieceCatalog pieces,DateTimeOffset now)
    {
        var before=character.EpicProduction;before.Validate();
        if (!production.Liquids.TryGetValue(liquidId,out var liquid) || character.Level<liquid.MinimumLevel ||
            materials.Count==0 || materials.Select(row=>row.Slot).Distinct().Count()!=materials.Count) return null;
        var inventory=character.Inventory;
        long units=liquid.Group switch { 1=>before.IntermediateMaterialRemainder,2=>before.OrdinaryMaterialRemainder,_=>0 };
        long bonus=0;int randomIndex=0;
        foreach (var input in materials)
        {
            var item=inventory.Find(new(InventoryListType.Main,input.Slot));
            if (item is null || item.Core.ItemId!=input.ItemId || input.Count<=0 || input.Count>item.Core.Count ||
                item.Core.IsRental || item.Core.EquipmentLockId!=0 || item.IsExpired(now.ToUnixTimeSeconds())) return null;
            var template=items.Get(input.ItemId);
            if (liquid.AllowedItems.Length>0 && !liquid.AllowedItems.Contains(input.ItemId) ||
                liquid.ItemType=="equipment" && template.Kind!=ItemKind.Equipment ||
                liquid.ItemType=="material" && template.Kind!=ItemKind.Material ||
                liquid.Parts=="weapon" && template.EquipmentSlot!=12 ||
                liquid.Grade=="unique" && template.Rarity!=3 ||
                template.MinimumLevel<liquid.MinimumItemLevel || template.MinimumLevel>liquid.MaximumItemLevel ||
                liquid.RequiresSealable && !template.AttachType.Contains("[sealing]",StringComparison.OrdinalIgnoreCase)) return null;
            int value=1;
            if (liquid.UsesMaterialPoints && !production.MaterialPoints.TryGetValue(template.Rarity,out value)) return null;
            units+=(long)input.Count*value;
            if (template.Kind==ItemKind.Equipment)
            {
                if (input.Count!=1) return null;
                // CB80AE–CB8252 将强化/增幅与锻造的资源区间附加数量相加，而非取最大值。
                bonus+=production.UpgradeOutput.FirstOrDefault(row=>item.Core.Upgrade>=row.Minimum && item.Core.Upgrade<=row.Maximum)?.Value??0;
                bonus+=production.GenuineUpgradeOutput.FirstOrDefault(row=>item.Core.GenuineUpgrade>=row.Minimum && item.Core.GenuineUpgrade<=row.Maximum)?.Value??0;
                if (liquid.MaximumOutput>liquid.MinimumOutput)
                {
                    if (randomIndex>=outputRolls.Count || outputRolls[randomIndex]<0 || outputRolls[randomIndex]>=production.OutputMaximumRate) return null;
                    if (outputRolls[randomIndex++]<liquid.MaximumOutputRate) bonus+=liquid.MaximumOutput-liquid.MinimumOutput;
                }
            }
            if (!InventoryDeleteService.TryDelete(item,item.Core.Value,input.Count,out var replacement)) return null;
            inventory=inventory with { Items=replacement is null?inventory.Items.Remove(item):inventory.Items.Replace(item,replacement) };
        }
        if (randomIndex!=outputRolls.Count) return null;
        long recipes=units/liquid.NeededCount,remainder=units%liquid.NeededCount;
        if (liquid.Group==0 && remainder!=0) return null;
        long output=recipes*liquid.MinimumOutput+bonus;
        if (output>int.MaxValue || recipes>int.MaxValue) return null;
        var after=liquid.Group switch
        {
            1=>before with { IntermediateMaterialRemainder=(int)remainder },
            2=>before with { OrdinaryMaterialRemainder=(int)remainder },
            _=>before
        };
        if (liquid.RequiredItems.Any(cost=>(long)cost.Count*recipes>int.MaxValue)) return null;
        var required=liquid.RequiredItems.Select(cost=>new ItemMaterialCost(cost.ItemId,(int)(cost.Count*recipes))).Where(cost=>cost.Count>0).ToArray();
        var consumption=InventoryMaterialConsumptionService.Plan(inventory,required,pieces,now);
        if (!consumption.Accepted) return null;
        inventory=consumption.Plan!.After;
        if (output>0)
        {
            var reward=InventoryRewardGrantService.TryGrantBatch(character with { Inventory=inventory },[new(liquidId,(int)output)],items,gold,pieces,now);
            if (!reward.Accepted) return null;
            inventory=reward.Plan!.After;
        }
        return new(character,before,after,new(character.Inventory,inventory,"epic_production_catalyst"),false,false,false)
            { CraftedLiquidId=liquidId,CraftedCount=(int)output };
    }
}
