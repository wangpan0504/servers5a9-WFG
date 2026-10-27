using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class OperationHopeEquipmentTests
{
    public static void Run(PvfArchive pvf,ItemResources items,OperationHopeCatalog hope)
    {
        var catalog=new OperationHopeRewardCatalog(pvf,items.Items);
        var policy=new OperationHopeRewardPolicy(catalog,items.Items);
        That(catalog.Equipment.Length==1116 && catalog.UnbiasedWeapons.Length==62 &&
            catalog.ProfessionWeight==12 && catalog.SealingPermyriad==240 &&
            catalog.HiddenNumerator==26 && catalog.HiddenDenominator==100,"本版名册与用户确认的兼容参数完整装载");
        var pool=policy.Pool(0,1);
        int before=0,biased=0;
        foreach(var row in pool.Entries)
        {
            int weight=row.CumulativeWeight-before;before=row.CumulativeWeight;
            bool current=catalog.Professions[(0,1)].Contains(row.Template.Id);
            That(weight==(current?12:1),"每一装备按本人转职名册赋权，不按可穿职业代替专属BUFF");
            if(current) biased++;
        }
        That(biased==18 && pool.TotalWeight==1376 && policy.Pool(0,0).TotalWeight==1178 &&
            ReferenceEquals(policy.Pool(0,17),pool),"未转职没有加权，觉醒位归一化不额外扩大缓存");
        foreach(int roll in new[]{0,239,240,9999})
        {
            var item=policy.CreateSealingItem(_=>roll,DateTimeOffset.UtcNow);
            That(roll<240 ? item is {ItemId:490011160,Count:1} : item is null,"黄金碎片240/10000分支首尾边界不漂移");
        }
        Reject<InvalidDataException>(()=>policy.Create(0,1,_=>10000,DateTimeOffset.UtcNow),"装备随机源越界必须拒绝");
        Reject<InvalidDataException>(()=>policy.CreateSealingItem(_=>10000,DateTimeOffset.UtcNow),"黄金碎片随机源越界必须拒绝");
        foreach(int roll in new[]{0,73,74,99})
        {
            int maze=OperationHopePlan.ChooseMaze(hope,bound=>bound==100?roll:0);
            That(hope.Mazes[maze].HiddenBoss==(roll>=74),"先按26/100决定隐藏，不因隐藏迷宫数量改变出现率");
        }
        Reject<InvalidDataException>(()=>OperationHopePlan.ChooseMaze(hope,_=>-1),"希望迷宫随机源下界拒绝");
        var card=new DungeonCardReward(0,DfoServer.Game.Inventory.Services.InventoryRewardGrantService.Create(items.Items.Get(10307438),130),
            OperationHopeRewardPolicy.Rule,OperationHopeRewardCatalog.PoolPath,DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            { HopeEquipment=policy.Create(0,1,_=>999,DateTimeOffset.UtcNow),HopeSealingItem=policy.CreateSealingItem(_=>0,DateTimeOffset.UtcNow) };
        var restored=JsonSerializer.Deserialize<DungeonCardReward>(JsonSerializer.Serialize(card))!;
        restored.Validate();
        That(restored.Items().SequenceEqual(card.Items()),"材料与装备完整核心一起进入持久回执，恢复不重新抽取");
        That(DfoServer.Network.Builders.DungeonSettlementPackets.CardReward(new(0,0,0,0,"hope-fixture","hope-fixture"),0,card).Length>0,
            "材料与装备同时经过真实结果页构造，不仅验证规划结果");
        VerifyExchange(items);
    }

    static void VerifyExchange(ItemResources items)
    {
        var shop=new InventoryShopService(items.Items,items.Shops,items.GoldLimits,items.EpicPieces);
        var booster=new InventoryBoosterService(items.Items,items.Boosters,items.GoldLimits,items.EpicPieces);
        var character=new Character(1,0,"HopeExchange",0,new(54,4,40,200),null){Level=95,Job=0,GrowType=1};
        foreach(int id in new[]{10307547,10307549,10307548,10307684,10307685,10307918,490011161})
        {
            var template=items.Items.Get(id);
            That(template.ShopPrice.Materials.Any(m=>m.ItemId is 10307438 or 490011160),"希望成品盒按真实碎片收费 "+id);
            var funded=InventoryRewardGrantService.TryGrantBatch(character,
                template.ShopPrice.Materials.Select(m=>new InventoryReward(m.ItemId,m.Count)).ToArray(),
                items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow);
            That(funded.Accepted,"希望兑换材料可进入真实背包 "+id);
            var buyer=character with {Inventory=funded.Plan!.After with {Gold=(uint)template.ShopPrice.Gold}};
            var bought=shop.Buy(buyer,new(id,1,100000000,611),out var receipt);
            That(bought.Accepted && receipt is not null && template.ShopPrice.Materials.All(m=>
                bought.Plan!.After.Items.Where(i=>i.Core.ItemId==m.ItemId).Sum(i=>i.Core.Count)==0),"赛贝琳真实商店扣净材料并发放成品盒 "+id);
            var definition=items.Boosters.Get(id);
            That(definition.Available && definition.Selection is {SelectionCount:1},"希望装备自选盒具有可消费规则 "+id+": "+definition.PendingRule);
            var category=definition.Selection!.Categories.First(c=>!c.Rewards.IsEmpty);
            int equipment=category.Rewards[0].ItemId;
            var opened=booster.Open(character with {Inventory=bought.Plan!.After},
                new(receipt!.Position.Index,1,new(category.Category,category.Subcategory,[equipment])),out var openedReceipt);
            That(opened.Accepted && openedReceipt!.Rewards.Any(r=>r.ItemId==equipment) &&
                opened.Plan!.After.Items.Any(i=>i.Core.ItemId==equipment && i.Core.Kind==ItemKind.Equipment) &&
                opened.Plan.After.Items.All(i=>i.Core.ItemId!=id),"希望成品盒沿用正式选择服务消费并落袋 "+id);
            That(!shop.Buy(character,new(id,1,100000000,611),out _).Accepted,"材料不足不能免费兑换 "+id);
        }
    }
}
