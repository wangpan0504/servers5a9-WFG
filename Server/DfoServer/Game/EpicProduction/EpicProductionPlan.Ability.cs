using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.EpicProduction;

internal sealed partial record EpicProductionPlan
{
    public static EpicProductionPlan? Ability(Character character,short slot,byte group,byte option,
        EpicProductionCatalog production,EpicProductionAbilityCatalog abilities,EpicPieceCatalog pieces,DateTimeOffset now)
    {
        var before=character.EpicProduction;
        before.Validate();
        if(character.Level<production.MinimumLevel || !abilities.Options.TryGetValue((group,option),out var costs)) return null;
        var item=character.Inventory.Find(new(InventoryListType.Main,slot));
        if(item is null || item.Core.Kind!=ItemKind.Equipment || item.Core.IsRental || item.Core.EquipmentLockId!=0 ||
            item.IsExpired(now.ToUnixTimeSeconds()) || !production.Weapons.Values.Any(ids=>ids.Contains(item.Core.ItemId))) return null;
        // 027F87B1把wire114映射至核心+BC，027EDDD0以group读其中一字节；其余字节/Flag不得覆盖。
        int shift=group*8;
        if(((item.Core.Extension114.Value>>shift)&255)==option) return null;
        var spent=InventoryMaterialConsumptionService.Plan(character.Inventory,costs,pieces,now);
        if(!spent.Accepted) return null;
        uint value=(item.Core.Extension114.Value&~(255u<<shift))|((uint)option<<shift);
        var updated=item with { Core=item.Core with { Extension114=item.Core.Extension114 with {Value=value} } };
        var inventory=spent.Plan!.After with { Items=spent.Plan.After.Items.Replace(item,updated) };
        return new(character,before,before,new(character.Inventory,inventory,"epic_production_ability"),false,false,false);
    }
}
