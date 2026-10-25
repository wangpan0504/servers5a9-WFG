using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.EpicProduction;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicProductionChangeTests
{
    public static void RunCore()
    {
        var request=EpicProductionRequests.Change([1,0,0,0,1,0,2,0,0,0,121,0,0,0]);
        That(request.WeaponId==1 && request.Materials is [{ItemId:2,Slot:121}],"换型请求按u32目标和材料ID/槽读取");
        foreach (byte[] invalid in new byte[][]
        {
            [],[1,0,0,0,0,0],[1,0,0,0,1,0,2,0,0,0,121,0,0],
            [1,0,0,0,1,0,2,0,0,0,255,255,255,255],
            [1,0,0,0,2,0,2,0,0,0,121,0,0,0,2,0,0,0,122,0,0,0],
            [1,0,0,0,2,0,2,0,0,0,121,0,0,0,3,0,0,0,121,0,0,0]
        }) Reject<InvalidDataException>(()=>EpicProductionRequests.Change(invalid),"换型截断/空/越界/重复整包拒绝");
        Bytes(EpicProductionPackets.Changed(0x12345678),[1,0x78,0x56,0x34,0x12],"换型成功只回新武器，不重置进度");
    }

    public static void RunResources(Character current,CharacterRepository repo,AccountOwners.Lease owner,
        GameSession session,EpicProductionCatalog catalog,ItemResources items,Func<string,object?> query)
    {
        That(catalog.ChangeItems.SequenceEqual([new ItemMaterialCost(10158124,30),new ItemMaterialCost(10163135,100)]) &&
            catalog.SpecialChangeItems.SequenceEqual([new ItemMaterialCost(10163199,1)]),"换型完整普通和特殊配方来自PVF");
        var funding=InventoryRewardGrantService.TryGrantBatch(current,[new(10158124,60),new(10163135,200),new(10163199,2)],
            items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow);
        That(funding.Accepted,"换型夹具含实际钱包史诗灵魂与两种实体材料");
        current=repo.CommitInventory(owner,current,funding.Plan!);
        int target=catalog.Weapons["[swordman]"][1];
        (int ItemId,short Slot) Row(int id)=>CurrencyWallet.RewardKind(id) is { } currency
            ?(id,ItemSlotBoundService.CurrencySlot(currency)!.Value)
            :(id,current.Inventory.Items.Single(i=>i.Core.ItemId==id).Position.Index);
        var normal=new[]{Row(10158124),Row(10163135)};
        var special=new[]{Row(10163199)};
        EpicProductionPlan? Change(Character c,int id,(int,short)[] rows)=>
            EpicProductionPlan.Change(c,id,rows,catalog,items.EpicPieces,DateTimeOffset.UtcNow);
        That(Change(current,target,[normal[0],special[0]]) is null && Change(current,target,[normal[0]]) is null &&
            Change(current,target,[special[0],normal[0],normal[1]]) is null,"不混合配方或省略、额外提交材料");
        That(Change(current,current.EpicProduction.WeaponId,normal) is null &&
            Change(current,catalog.Weapons["[fighter]"][0],normal) is null &&
            Change(current with {Level=1},target,normal) is null,"同武器、跨职业、低等级不能换型扣料");
        That(Change(current,target,[(10158124,normal[1].Slot),normal[1]]) is null &&
            Change(current,target,[normal[0],(10163135,special[0].Slot)]) is null,"虚拟货币槽和实际材料ID均绑定");
        var plan=Change(current,target,normal)!;
        That(plan.After==current.EpicProduction with {WeaponId=target} && !plan.Processed && !plan.ProducedWeapon,
            "换型保留全部进度计数和余量，不能冒充任务加工事件");
        var before=System.Text.Json.JsonSerializer.Serialize(current);
        query("CREATE TRIGGER reject_change BEFORE UPDATE ON character_epic_production BEGIN SELECT RAISE(ABORT,'rollback'); END");
        Reject<SqliteException>(()=>repo.CommitEpicProduction(owner,plan),"换型状态写入失败回滚钱包和背包材料");
        That(System.Text.Json.JsonSerializer.Serialize(repo.List(owner).Single())==before,"失败后完整角色资产未改变");
        query("DROP TRIGGER reject_change");
        current=repo.CommitEpicProduction(owner,plan);
        That(current.EpicProduction==plan.After && CurrencyWallet.Get(current.Inventory,CurrencyKind.EpicSoul)==30,
            "普通换型持久扣30灵魂且保留原加工状态");
        Reject<InvalidOperationException>(()=>repo.CommitEpicProduction(owner,plan),"旧换型事务不能重复扣料");
        var restored=repo.List(owner).Single();
        That(restored.EpicProduction==plan.After,"重读角色恢复换型武器与进度");
        var ticket=Change(current,plan.Before.WeaponId,special)!;
        That(ticket.Inventory.After.AccountWallet==current.Inventory.AccountWallet,"特殊换型券不额外扣普通配方钱包");
        current=repo.CommitEpicProduction(owner,ticket);
        That(current.Inventory.Items.Single(i=>i.Core.ItemId==10163199).Core.Count==1,"特殊配方只扣一个换型券");
        session.ReturnToRoster();session.Select(0);session.MarkReady();
        var receipt=session.PlanAndCommitEpicProduction(c=>Change(c,target,special))!;
        That(session.CanPublishEpicProduction(receipt),"换型提交回执绑定当前角色");
        session.ReturnToRoster();session.Select(0);session.MarkReady();
        That(!session.CanPublishEpicProduction(receipt),"换角后旧换型回执不能发布");
        var stale=session.PlanAndCommitEpicProduction(c=>
        {
            var pending=Change(c,plan.Before.WeaponId,normal);
            session.ReturnToRoster();session.Select(0);session.MarkReady();
            return pending;
        });
        That(stale is null,"规划期间换角拒绝旧epoch换型计划");
    }
}
