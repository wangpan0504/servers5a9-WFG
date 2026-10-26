using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.EpicProduction;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Network.Parsers;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicProductionTests
{
    public static void RunCore()
    {
        EpicProductionChangeTests.RunCore();
        EpicProductionAbilityTests.RunCore();
        That(EpicProductionRequests.Start([0x88,0x13,0,0])==5000,"开始制作请求精确读取单个u32");
        Reject<InvalidDataException>(()=>EpicProductionRequests.Start([1,0,0]),"截断开始请求拒绝");
        Reject<InvalidDataException>(()=>EpicProductionRequests.Start([1,0,0,0,0]),"开始请求拒绝尾随字节");
        var input=EpicProductionRequests.Process([1,0,0x88,0x13,0,0,48,0,0,0]);
        That(input is [{ ItemId:5000,Slot:48 }],"加工行的第二个u32解释为真实背包槽");
        Reject<InvalidDataException>(()=>EpicProductionRequests.Process([0,0]),"空加工材料拒绝");
        Reject<InvalidDataException>(()=>EpicProductionRequests.Process([1,0,1,0,0,0,255,255,255,255]),"负数编码槽不能回绕");
        Reject<InvalidDataException>(()=>EpicProductionRequests.Process([2,0,1,0,0,0,48,0,0,0,2,0,0,0,48,0,0,0]),"重复槽整包拒绝");
        Bytes(EpicProductionPackets.StartedOrFinished(5000,123),[1,0x88,0x13,0,0,123,0,0,0],"开始/成品结果两个完整u32");
        Bytes(EpicProductionPackets.Processed(123,7,true),[1,123,0,0,0,7,1],"加工结果保留绝对进度及两个独立字节");
        Bytes(EpicProductionPackets.Rejected(0x1234),[0,0x34,0x12],"失败保留u16码，不伪造成功");
        var craft=EpicProductionRequests.Craft([1,0,0,0,1,0,2,0,0,0,48,0,0,0,3,0,0,0]);
        That(craft.LiquidId==1 && craft.Materials is [{ItemId:2,Slot:48,Count:3}],"催化剂制作三字段不误读为加工的两字段");
        Reject<InvalidDataException>(()=>EpicProductionRequests.Craft([1,0,0,0,1,0,2,0,0,0,48,0,0,0]),"制作材料行截断拒绝");
        Reject<InvalidDataException>(()=>EpicProductionRequests.Craft([1,0,0,0,1,0,2,0,0,0,48,0,0,0,0,0,0,0]),"制作材料零数量拒绝");
        var policy=new EpicProductionCompatibility();int draws=0;
        That(policy.DrawGreatSuccess(max=>{That(max==1000000,"独立加工概率使用固定百万分母");draws++;return 79999;}) &&
            !policy.DrawGreatSuccess(_=>{draws++;return 80000;}) && draws==2,"默认8%严格边界且每次独立只取一次样本");
        That(!(policy with { GreatSuccessPercent=0 }).DrawGreatSuccess(_=>0) &&
            (policy with { GreatSuccessPercent=100 }).DrawGreatSuccess(_=>999999),"0%和100%边界可配置且不越界");
        Reject<InvalidDataException>(()=>(policy with { GreatSuccessPercent=-1 }).Validate(),"拒绝负概率");
        Reject<InvalidDataException>(()=>(policy with { GreatSuccessPercent=101 }).Validate(),"拒绝超百分之百");
        Reject<InvalidDataException>(()=>policy.DrawGreatSuccess(_=>1000000),"拒绝越界随机源");
        var state = new EpicProductionState(1,10,7,6,4,2,3);
        That(state.InWeek(7) == state,"史诗制作同周不重置任何次数");
        That(state.InWeek(8) == state with { Week=8,WeeklyUses=0,AdditionalWeeklyUses=0 },"换周仅重置周次数及本周扩展，保留进度和累计状态");
        Reject<InvalidOperationException>(() => state.InWeek(6),"拒绝周期倒退");
        Reject<InvalidDataException>(() => (state with { WeaponId=0 }).Validate(),"没有武器不得残留进度");
        Reject<InvalidDataException>(() => (state with { WeeklyUses=256 }).Validate(),"周计数不能溢出客户端字节");
    }

    public static void RunResources(PvfArchive pvf,ItemResources items)
    {
        int checks = Count;
        var catalog = new EpicProductionCatalog(pvf);
        That(catalog.MinimumLevel == 55 && catalog.WeeklyLimit == 7 && catalog.MaximumCharge == 1380000 && catalog.MaterialCount == 1,
            "史诗制作等级、周次数与满进度来自本版资源");
        That(catalog.Weapons.Count == 16 && catalog.Weapons.Values.SelectMany(x=>x).Distinct().Count() == 37 && catalog.Liquids.Count == 5,
            "完整职业武器与五级催化剂目录，不用史诗图鉴配方替代");
        That(catalog.RequiredItems.SequenceEqual([new ItemMaterialCost(10163135,1)]) && catalog.Liquids[10163134].Charge == 20000,
            "根材料与催化剂嵌套材料各自解析，不混为同一费用");
        RunCatalystRules(catalog,items);
        int weapon = catalog.Weapons["[swordman]"][0];
        string root = Path.Combine(Path.GetTempPath(),"s5a9-production-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path=Path.Combine(root,"game.db");
            var quests = DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
            var repo = new CharacterRepository(path,quests); var owners = new AccountOwners();
            using var owner=owners.Acquire("production",()=>{});
            var towns=TownCatalog.Load(pvf);
            var current=repo.Create(owner,new("Produce",towns.Room,towns.InitialReturn));
            EpicProductionPlan? Select(Character c,int id,int week=7) => EpicProductionPlan.Start(c,c.EpicProduction,id,week,
                catalog,items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow);
            That(Select(current,weapon) is null,"未到资源等级不能选择制作武器");
            Query("UPDATE characters SET level=55 WHERE id="+current.Id);
            current=repo.List(owner).Single();
            var initial=Select(current,weapon)!;
            That(initial.After.WeaponId==weapon && initial.After.Charge==0 && !initial.Inventory.Changed,"开始制作不赠送成品或进度");
            Query("CREATE TRIGGER reject_production BEFORE INSERT ON character_epic_production BEGIN SELECT RAISE(ABORT,'rollback'); END");
            Reject<SqliteException>(()=>repo.CommitEpicProduction(owner,initial),"后段状态失败整笔撤销revision");
            That(System.Text.Json.JsonSerializer.Serialize(repo.List(owner).Single())==System.Text.Json.JsonSerializer.Serialize(current),"失败后角色和空状态完全保留");
            Query("DROP TRIGGER reject_production");
            current=repo.CommitEpicProduction(owner,initial);
            That(Select(current,weapon) is null && Select(current,catalog.Weapons["[swordman]"][1]) is null,"未完成不能重复开始或免费换武器");
            Reject<InvalidOperationException>(()=>repo.CommitEpicProduction(owner,initial),"旧计划不可重放覆盖当前状态");
            That(new CharacterRepository(path).List(owner).Single().EpicProduction==current.EpicProduction,"重开仓储恢复原制作武器和进度");
            That(QuestEpicProductionProgress.Supports(quests.Get(10024)) && QuestEpicProductionProgress.Supports(quests.Get(10026)),
                "两条真实加工任务均使用同一成功事件合同");
            Query($"INSERT INTO character_active_main_quests VALUES({current.Id},0,10024,1,'11111111111111111111111111111111')");
            current=repo.List(owner).Single();
            var funding=InventoryRewardGrantService.TryGrantBatch(current,[new(10163130,3),new(10163131,3),new(10163134,3),new(10163135,3)],
                items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow);
            That(funding.Accepted,"加工夹具通过统一物品发放建立真实材料实例");
            current=repo.CommitInventory(owner,current,funding.Plan!);
            var materials=new[] {10163130,10163131,10163134}.Select(id=>(id,current.Inventory.Items.Single(row=>row.Core.ItemId==id).Position.Index)).ToArray();
            EpicProductionPlan? Process(Character c,(int,short)[] input,bool great=false) => EpicProductionPlan.Process(c,input,7,great,catalog,
                items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow);
            That(Process(current,[]) is null && Process(current,[materials[0],materials[0]]) is null &&
                Process(current,[(10163134,materials[0].Index)]) is null,"空材料、重复槽、伪造物品槽绑定整批拒绝");
            var process=Process(current,materials)!;
            That(process.After.Charge==23000 && process.After.WeeklyUses==1 && process.Processed && !process.ProducedWeapon &&
                process.Inventory.After.Items.All(row=>row.Core.Count==2),"三组催化剂按原点数与固定碳晶扣量规划");
            string beforeProcess=System.Text.Json.JsonSerializer.Serialize(current);
            Query("CREATE TRIGGER reject_process_quest BEFORE UPDATE ON character_active_main_quests WHEN NEW.quest_id=10024 BEGIN SELECT RAISE(ABORT,'rollback'); END");
            Reject<SqliteException>(()=>repo.CommitEpicProduction(owner,process),"任务后段失败必须回滚加工材料、周次数及进度");
            That(System.Text.Json.JsonSerializer.Serialize(repo.List(owner).Single())==beforeProcess,"加工失败全角色快照与材料资产保持");
            Query("DROP TRIGGER reject_process_quest");current=repo.CommitEpicProduction(owner,process);
            That(current.ActiveMainQuests.Single().ConditionState==0 && current.ActiveMainQuests.Single().AcceptanceId==Guid.ParseExact("11111111111111111111111111111111","N"),
                "成功加工同事务只归零原接取目标，保留槽位身份并未直接领奖");
            Reject<InvalidOperationException>(()=>repo.CommitEpicProduction(owner,process),"重复旧加工计划不能再次扣料或推进");
            That(Process(current,materials,true)!.After.Charge==69000,"大成功仅把催化剂本次效率翻倍");
            var nearFull=current with { EpicProduction=current.EpicProduction with { Charge=catalog.MaximumCharge-500,WeeklyUses=6 } };
            var final=Process(nearFull,[materials[0]])!;
            That(final.ProducedWeapon && final.After.Charge==0 && final.After.WeaponId==0 && final.After.WeeklyUses==7 &&
                final.Inventory.After.Items.Count(row=>row.Core.ItemId==weapon)==1,"满进度加工一次计划合并成品发放且不恢复周次数");
            That(Process(current with { EpicProduction=current.EpicProduction with { WeeklyUses=7 } },materials) is null,"七次上限拒绝不消费任何材料");
            // 专门构造成品领取前置快照；不把此夹具当加工业务验收。
            Query("UPDATE character_epic_production SET charge=1380000,weekly_uses=7 WHERE character_id="+current.Id);
            current=repo.List(owner).Single(); var claim=EpicProductionPlan.Complete(current,catalog,items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow)!;
            That(claim.ProducedWeapon && claim.After.WeeklyUses==7 && claim.After.WeaponId==0 && claim.After.Charge==0,
                "领取清空本次武器但不能恢复周加工次数");
            Query("CREATE TRIGGER reject_claim BEFORE UPDATE ON character_epic_production BEGIN SELECT RAISE(ABORT,'rollback'); END");
            Reject<SqliteException>(()=>repo.CommitEpicProduction(owner,claim),"领取后段失败撤销成品及资产revision");
            That(Query("SELECT count(*) FROM character_inventory_items") is 4L && repo.List(owner).Single().EpicProduction.Charge==1380000,
                "回滚没有成品且保留可领取进度");
            Query("DROP TRIGGER reject_claim"); current=repo.CommitEpicProduction(owner,claim);
            That(current.Inventory.Items.Count(x=>x.Core.ItemId==weapon)==1 && current.Inventory.Items.Single(row=>row.Core.ItemId==weapon).Uid>0,
                "领取通过统一库存提交使用真实持久UID");
            Reject<InvalidOperationException>(()=>repo.CommitEpicProduction(owner,claim),"重复旧领取不再发成品");
            Query("UPDATE characters SET level=86 WHERE id="+current.Id);current=repo.List(owner).Single();
            var craftFunding=InventoryRewardGrantService.TryGrantBatch(current,[new(3137,26),new(3141,25),new(10099774,500)],
                items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow);
            That(craftFunding.Accepted,"混合中级催化剂夹具材料可正常发放");
            current=repo.CommitInventory(owner,current,craftFunding.Plan!);
            var craftRows=new[] {3137,3141}.Select(id=>{var item=current.Inventory.Items.Single(row=>row.Core.ItemId==id);return(id,item.Position.Index,item.Core.Count);}).ToArray();
            var crafting=EpicProductionPlan.Craft(current,10163131,craftRows,[],catalog,items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow)!;
            That(crafting.CraftedCount==1 && crafting.After.IntermediateMaterialRemainder==1 && !crafting.Processed,
                "混合材料按资源五十份制作一份，余一份不丢失且不冒充武器加工");
            string craftBefore=System.Text.Json.JsonSerializer.Serialize(current);
            Query("CREATE TRIGGER reject_catalyst BEFORE UPDATE ON character_epic_production BEGIN SELECT RAISE(ABORT,'rollback'); END");
            Reject<SqliteException>(()=>repo.CommitEpicProduction(owner,crafting),"余量保存失败回滚催化剂及输入材料");
            That(System.Text.Json.JsonSerializer.Serialize(repo.List(owner).Single())==craftBefore,"制作催化剂失败不吞原材料");
            Query("DROP TRIGGER reject_catalyst");current=repo.CommitEpicProduction(owner,crafting);
            That(new CharacterRepository(path).List(owner).Single().EpicProduction.IntermediateMaterialRemainder==1 &&
                current.EpicProduction.InWeek(8).IntermediateMaterialRemainder==1,"材料余量重连及换周均保持");
            Reject<InvalidOperationException>(()=>repo.CommitEpicProduction(owner,crafting),"催化剂制作旧事务不可重放");
            var walletCraft=EpicProductionPlan.Craft(current,10163130,[(10099774,363,500)],[],catalog,
                items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow)!;
            string walletBefore=System.Text.Json.JsonSerializer.Serialize(current);
            Query("CREATE TRIGGER reject_wallet_catalyst BEFORE UPDATE ON character_epic_production BEGIN SELECT RAISE(ABORT,'rollback'); END");
            Reject<SqliteException>(()=>repo.CommitEpicProduction(owner,walletCraft),"钱包制作后段失败整笔回滚");
            That(System.Text.Json.JsonSerializer.Serialize(repo.List(owner).Single())==walletBefore,
                "失败保留账号钱包、产物、制作余量及所有revision");
            Query("DROP TRIGGER reject_wallet_catalyst");current=repo.CommitEpicProduction(owner,walletCraft);
            That(CurrencyWallet.Get(repo.List(owner).Single().Inventory,CurrencyKind.UniqueSoul)==0,
                "普通制作钱包费用同事务持久化，重读不能恢复材料");
            Reject<InvalidOperationException>(()=>repo.CommitEpicProduction(owner,walletCraft),"旧钱包制作计划不能重复发产物");
            var session=new GameSession(repo,towns,owner);session.Select(0);
            That(session.PlanAndCommitEpicProduction(c=>Select(c,weapon)) is null,"未准备会话不能开始加工");
            session.MarkReady();
            var started=session.PlanAndCommitEpicProduction(c=>Select(c,weapon))!;
            That(started.Character==session.Selected && session.CanPublishEpicProduction(started) &&
                ReferenceEquals(started.Inventory.Receipt!.Character,session.Selected),"提交后发布回执绑定真实选角及资产快照");
            var changed=session.PlanAndCommitEpicProduction(c=>EpicProductionPlan.Process(c,materials,8,false,catalog,
                items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow))!;
            That(changed.Character.EpicProduction.WeeklyUses==1 && !session.CanPublishEpicProduction(started) &&
                session.CanPublishEpicProduction(changed),"新周加工保留正确新状态，旧快照回执不能覆盖新结果");
            current=session.Selected!;
            EpicProductionChangeTests.RunResources(current,repo,owner,session,catalog,items,Query);
            EpicProductionAbilityTests.RunResources(repo,owner,catalog,new EpicProductionAbilityCatalog(pvf),items,Query);
            using var takeover=owners.Acquire(owner.Account,()=>{});
            Reject<InvalidOperationException>(()=>repo.CommitEpicProduction(owner,started.Plan),"旧账号代际不能提交新制作");
            That(Query("PRAGMA integrity_check") is "ok","史诗制作事务数据库完整");
            object? Query(string sql)
            {
                using var db=new SqliteConnection("Data Source="+path+";Pooling=False");db.Open();
                using var command=db.CreateCommand();command.CommandText=sql;return command.ExecuteScalar();
            }
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine($"PASS {Count-checks} 史诗制作资源、选择及加工及成品结算事务断言；网络和客户端尚另验");
    }

    static void RunCatalystRules(EpicProductionCatalog catalog,ItemResources items)
    {
        var now=DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var source=new Character(1,0,"Catalyst",0,new(1,0,0,0),null) { Level=75 };
        var funding=InventoryRewardGrantService.TryGrantBatch(source,
            [new(3012,751),new(3168,626),new(10099774,501),new(3137,51),new(3141,25)],
            items.Items,items.GoldLimits,items.EpicPieces,now);
        That(funding.Accepted,"催化剂规则夹具包含普通、稀有实体材料及神器灵魂钱包");
        source=source with { Inventory=funding.Plan!.After };
        That(items.Items.Get(3012).Rarity==0 && items.Items.Get(3168).Rarity==2 && items.Items.Get(10099774).Rarity==3,
            "750/625/500消耗边界对应匹配PVF的三种稀有度");
        var white=source.Inventory.Items.Single(item=>item.Core.ItemId==3012);
        var rare=source.Inventory.Items.Single(item=>item.Core.ItemId==3168);
        var cloth=source.Inventory.Items.Single(item=>item.Core.ItemId==3137);
        var leather=source.Inventory.Items.Single(item=>item.Core.ItemId==3141);
        EpicProductionPlan? Craft(Character c,int id,params (int,short,int)[] rows)=>
            EpicProductionPlan.Craft(c,id,rows,[],catalog,items.Items,items.GoldLimits,items.EpicPieces,now);
        foreach(var (id,slot,count) in new (int,short,int)[]{(3012,white.Position.Index,750),(3168,rare.Position.Index,625),(10099774,363,500)})
        {
            var plan=Craft(source with { Level=55 },10163130,(id,slot,count));
            That(plan is { CraftedCount:1,After.OrdinaryMaterialRemainder:0 },"普通催化剂按稀有度积分恰好制作一份");
            That(plan!.Inventory.After.Items.Single(item=>item.Core.ItemId==10163130).Core.Count==1,
                "产物由库存发放入口生成真实计划");
            long remaining=id==10099774?CurrencyWallet.Get(plan.Inventory.After,CurrencyKind.UniqueSoul):
                plan.Inventory.After.Items.Single(item=>item.Core.ItemId==id).Core.Count;
            That(remaining==1,"实体或钱包材料只扣所需数量，保留余一件");
        }
        var mixed=Craft(source,10163130,(3012,white.Position.Index,300),(10099774,363,300));
        That(mixed is { CraftedCount:1,After.OrdinaryMaterialRemainder:0 } &&
            CurrencyWallet.Get(mixed.Inventory.After,CurrencyKind.UniqueSoul)==201 &&
            mixed.Inventory.After.Find(white.Position)!.Core.Count==451,"不同稀有度实体和钱包材料允许混合使用");
        var partial=Craft(source,10163130,(3012,white.Position.Index,749))!;
        That(partial.CraftedCount==0 && partial.After.OrdinaryMaterialRemainder==74900,"不足一份的积分保存且不造产物");
        var continued=Craft(source with { Inventory=partial.Inventory.After,EpicProduction=partial.After },10163130,(10099774,363,1));
        That(continued is { CraftedCount:1,After.OrdinaryMaterialRemainder:50 },"累计积分与钱包材料相加后保留准确余量");
        That(Craft(source with { Level=54 },10163130,(3012,white.Position.Index,750)) is null &&
            Craft(source with { Level=74 },10163131,(3137,cloth.Position.Index,50)) is null,"55和75级制作门槛不放宽");
        That(Craft(source,10163131,(3137,cloth.Position.Index,26),(3141,leather.Position.Index,25)) is
            { CraftedCount:1,After.IntermediateMaterialRemainder:1 },"中级催化剂按总数50混合制作并保留数量余量");
        That(Craft(source,10163131,(3012,white.Position.Index,50)) is null &&
            Craft(source,10163130,(10099774,362,500)) is null &&
            Craft(source,10163130,(10099774,363,502)) is null &&
            Craft(source,10163130,(3012,white.Position.Index,752)) is null &&
            Craft(source,10163130,(3015,white.Position.Index,750)) is null &&
            Craft(source,10163130,(10099774,363,250),(10099774,363,250)) is null,
            "表外材料、错钱包槽、余额不足、实体不足、错ID及重复槽均整笔拒绝");
        foreach(var core in new[]{white.Core with { EquipmentLockId=1 },white.Core with { IsRental=true },
            white.Core with { ExpireTime=checked((int)now.ToUnixTimeSeconds()) }})
            That(Craft(source with { Inventory=source.Inventory with { Items=source.Inventory.Items.Replace(white,white with { Core=core }) } },
                10163130,(3012,white.Position.Index,750)) is null,"实体锁、租赁和到期边界仍拒绝");
        That(CurrencyWallet.Get(source.Inventory,CurrencyKind.UniqueSoul)==501 && source.Inventory.Find(white.Position)!.Core.Count==751,
            "成功或失败规划均不修改原角色资产");
    }
}
