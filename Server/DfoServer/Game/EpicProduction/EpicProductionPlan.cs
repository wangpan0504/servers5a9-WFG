using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.EpicProduction;

// 选择与成品领取只产生计划；材料、角色状态及任务变化由同一角色事务提交后才可发布。
internal sealed partial record EpicProductionPlan(Character Expected, EpicProductionState Before, EpicProductionState After,
    InventoryPlan Inventory, bool ProducedWeapon, bool Processed, bool BigChance)
{
    static readonly string[] Jobs = ["[swordman]","[fighter]","[gunner]","[mage]","[priest]","[at gunner]","[thief]",
        "[at fighter]","[at mage]","[demonic swordman]","[creator mage]","[at swordman]","[knight]","[demonic lancer]","[at priest]","[gun blader]"];

    public static EpicProductionPlan? Start(Character character, EpicProductionState before, int weaponId, int week,
        EpicProductionCatalog production, ItemCatalog items, GoldLimitCatalog gold, EpicPieceCatalog pieces,
        DateTimeOffset now)
    {
        before.Validate();
        if (character.EpicProduction != before) return null;
        if (week < before.Week || character.Level < production.MinimumLevel || character.Job >= Jobs.Length ||
            !production.Weapons.TryGetValue(Jobs[character.Job],out var choices) || !choices.Contains(weaponId)) return null;
        var current = before.InWeek(week);
        if (current.WeaponId == 0)
            return new(character,before,current with { WeaponId = weaponId },
                new(character.Inventory,character.Inventory,"epic_production_select"),false,false,false);
        return null;
    }

    // 成品结算是服务端内部操作，不把未确认的客户端请求解释为领取命令。
    public static EpicProductionPlan? Complete(Character character,EpicProductionCatalog production,
        ItemCatalog items,GoldLimitCatalog gold,EpicPieceCatalog pieces,DateTimeOffset now)
    {
        var current=character.EpicProduction; current.Validate();
        int weaponId=current.WeaponId;
        if (current.Charge != production.MaximumCharge || !production.Weapons.Values.Any(ids=>ids.Contains(weaponId))) return null;
        var reward = InventoryRewardGrantService.TryGrantBatch(character,[new(weaponId,1)],items,gold,pieces,now);
        if (!reward.Accepted) return null;
        // Neople制作说明：特殊催化剂衰减只在成品完成时清零，不随周刷新或换角清零。
        return new(character,current,current with { WeaponId = 0,Charge = 0,SpecialUses=0 },
            new(character.Inventory,reward.Plan!.After,"epic_production_complete"),true,false,false);
    }
    // greatSuccess 必须来自服务器已冻结的随机结果，不接受请求体中的进度、暴击或数量。
    public static EpicProductionPlan? Process(Character character,IReadOnlyList<(int ItemId,short Slot)> catalysts,
        int week,bool greatSuccess,EpicProductionCatalog production,ItemCatalog items,GoldLimitCatalog gold,
        EpicPieceCatalog pieces,DateTimeOffset now)
    {
        var before=character.EpicProduction; before.Validate();
        if (character.Level<production.MinimumLevel || week<before.Week || before.WeaponId<=0 ||
            before.Charge>=production.MaximumCharge || catalysts.Count is <1 or >3 ||
            catalysts.Select(row=>row.Slot).Distinct().Count()!=catalysts.Count ||
            !production.Weapons.Values.Any(ids=>ids.Contains(before.WeaponId))) return null;
        var state=before.InWeek(week);
        if (state.WeeklyUses>=Math.Min(byte.MaxValue,(long)production.WeeklyLimit+state.AdditionalWeeklyUses)) return null;
        var inventory=character.Inventory;
        var groups=new HashSet<int>();
        long point=0; bool special=false;
        foreach (var input in catalysts)
        {
            if (!production.Liquids.TryGetValue(input.ItemId,out var liquid) || !groups.Add(liquid.Group)) return null;
            var slot=new InventorySlot(InventoryListType.Main,input.Slot);
            var item=inventory.Find(slot);
            if (item is null || item.Core.ItemId!=input.ItemId || item.Core.IsRental ||
                item.Core.EquipmentLockId!=0 || item.IsExpired(now.ToUnixTimeSeconds()) ||
                !InventoryDeleteService.TryDelete(item,item.Core.Value,production.MaterialCount,out var replacement)) return null;
            inventory=inventory with { Items=replacement is null?inventory.Items.Remove(item):inventory.Items.Replace(item,replacement) };
            int penalty=0;
            if (liquid.AffectsTryCount)
            {
                special=true;
                var range=production.TryCountAdjustments.FirstOrDefault(row=>state.SpecialUses+1>=row.Minimum && state.SpecialUses+1<=row.Maximum);
                if (range is null) return null;
                penalty=range.Value;
            }
            point+=Math.Max(0,liquid.Charge-penalty);
        }
        var cost=InventoryMaterialConsumptionService.Plan(inventory,production.RequiredItems,pieces,now);
        if (!cost.Accepted || point<=0) return null;
        inventory=cost.Plan!.After;
        if (greatSuccess) point*=production.BigChanceMultiplier;
        int charge=checked((int)Math.Min(production.MaximumCharge,(long)state.Charge+point));
        state=state with { Charge=charge,WeeklyUses=state.WeeklyUses+1,
            SpecialUses=checked(state.SpecialUses+(special?1:0)),SinceBigChance=greatSuccess?0:checked(state.SinceBigChance+1) };
        bool completed=charge==production.MaximumCharge;
        if (completed)
        {
            var finish=Complete(character with { EpicProduction=state,Inventory=inventory },production,items,gold,pieces,now);
            if (finish is null) return null;
            inventory=finish.Inventory.After;state=finish.After;
        }
        return new(character,before,state,new(character.Inventory,inventory,"epic_production_process"),completed,true,greatSuccess);
    }

}

internal sealed record EpicProductionCommit(EpicProductionPlan Plan,Character Character,long Generation,long SelectionEpoch,
    InventoryResult Inventory);
