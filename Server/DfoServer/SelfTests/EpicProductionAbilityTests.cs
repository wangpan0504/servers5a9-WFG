using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.EpicProduction;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicProductionAbilityTests
{
    public static void RunCore()
    {
        That(EpicProductionRequests.Ability([9,0,1,22])==(9,1,22),"属性请求精确读取背包槽/组/选项");
        foreach(byte[] body in new byte[][] {[],[9,0,1],[9,0,1,22,0],[255,255,1,22],[9,0,2,22],[9,0,0,0]})
            Reject<InvalidDataException>(()=>EpicProductionRequests.Ability(body),"属性请求截断/尾随/越界/未证组拒绝");
        const string text="""
            [custom ability type]
            `epic production`
            [group]
            [key]
            `1`
            [indexes]
            1
            [/indexes]
            [/group]
            [info]
            [index]
            1
            [material]
            [grade]
            `D`
            [list]
            3168 50
            [/list]
            [/material]
            [/info]
            """;
        That(new EpicProductionAbilityCatalog(text).Options[(0,1)].SequenceEqual([new ItemMaterialCost(3168,50)]),
            "原资源key1映射wire组0且数量取资源");
        foreach(string bad in new[]{text.Replace("`D`","`C`"),text.Replace("3168 50","3168 50 3168 1"),
            text.Replace("3168 50","3168 -1"),text.Replace("`1`","`3`"),text.Replace("[index]\n1","[index]\n2")})
            Reject<InvalidDataException>(()=>new EpicProductionAbilityCatalog(bad),"未知分档、重复材料、非法组或悬空选项不能静默接受");
    }
    public static void RunResources(CharacterRepository repo,AccountOwners.Lease owner,EpicProductionCatalog production,
        EpicProductionAbilityCatalog abilities,ItemResources items,Func<string,object?> query)
    {
        That(abilities.Options.Count==16 && abilities.Options.Keys.Count(x=>x.Group==0)==4 &&
            abilities.Options.Keys.Count(x=>x.Group==1)==12,"资源闭合4种属性和12种攻击特效");
        var current=repo.List(owner).Single();
        int weapon=production.Weapons["[swordman]"][0];
        var item=current.Inventory.Items.Single(x=>x.Core.ItemId==weapon);
        var marked=item with {Core=item.Core with {Extension114=new(3,0xAA550000)}};
        current=repo.CommitInventory(owner,current,new(current.Inventory,current.Inventory with
            {Items=current.Inventory.Items.Replace(item,marked)},"ability_test_marker"));
        var grant=InventoryRewardGrantService.TryGrantBatch(current,[new(3168,150),new(10163135,150),new(10099774,70)],
            items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow);
        That(grant.Accepted,"属性附加真实材料和灵魂钱包夹具可发放");
        current=repo.CommitInventory(owner,current,grant.Plan!);
        EpicProductionPlan? Apply(Character c,byte group,byte option)=>EpicProductionPlan.Ability(c,item.Position.Index,
            group,option,production,abilities,items.EpicPieces,DateTimeOffset.UtcNow);
        That(Apply(current,0,11) is null && Apply(current,1,1) is null && Apply(current,2,1) is null,
            "组别不能跨组引用或写未证第三组");
        foreach(var invalid in new[]{marked.Core with {EquipmentLockId=1},marked.Core with {IsRental=true},
            marked.Core with {ExpireTime=1},marked.Core with {ItemId=18000}})
        {
            var rejected=current with {Inventory=current.Inventory with {Items=current.Inventory.Items.Replace(marked,marked with {Core=invalid})}};
            That(Apply(rejected,0,1) is null,"锁定/租赁/过期/非制作史诗不可附加");
        }
        var plan=Apply(current,0,1)!;
        var updated=plan.Inventory.After.Find(item.Position)!;
        That(updated.Uid==item.Uid && updated.Core.Extension114==new ItemExtension(3,0xAA550001) &&
            plan.After==current.EpicProduction && !plan.Processed,"只改选定字节，保留UID/高位/Flag和制作状态，不推进任务");
        string before=System.Text.Json.JsonSerializer.Serialize(current);
        query("CREATE TRIGGER reject_ability BEFORE UPDATE ON character_epic_production BEGIN SELECT RAISE(ABORT,'rollback'); END");
        Reject<SqliteException>(()=>repo.CommitEpicProduction(owner,plan),"属性附加后段失败回滚装备和材料钱包");
        That(System.Text.Json.JsonSerializer.Serialize(repo.List(owner).Single())==before,"属性失败完整资产一致");
        query("DROP TRIGGER reject_ability");current=repo.CommitEpicProduction(owner,plan);
        Reject<InvalidOperationException>(()=>repo.CommitEpicProduction(owner,plan),"属性旧事务重放不再次扣料");
        That(Apply(current,0,1) is null,"同选项重复请求不扣费");
        var effect=Apply(current,1,11)!;
        current=repo.CommitEpicProduction(owner,effect);
        That(Apply(current,0,2) is null,"缺少对应属性材料时不借用另一属性材料或免费改造");
        var restored=repo.List(owner).Single().Inventory.Find(item.Position)!;
        That(restored.Core.Extension114==new ItemExtension(3,0xAA550B01) && restored.Uid==item.Uid,
            "第二组只改次字节且重读持久保留第一组和所有未改字段");
    }
}
