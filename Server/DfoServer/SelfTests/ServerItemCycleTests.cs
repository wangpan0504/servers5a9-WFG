using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ServerItemCycleTests
{
    public static void RunResource(PvfArchive pvf)
    {
        var rules=ServerItemUsageCatalog.Load(pvf);
        That(rules.Limits.Any(row=>row is {Type:3,ItemId:0,Maximum:20}) &&
            rules.Limits.Any(row=>row is {Type:0,ItemId:10003202,Maximum:1}),"当前PVF普通寄信和经验秘药全局限次已装载");
        That(rules.Refills.Any(row=>row is {ItemId:2680755,Count:0,MinimumLevel:0}) &&
            rules.Mails.Any(row=>row.ItemId==490000321) && rules.Resets.Any(row=>row.ItemId==2682556),
            "当前PVF零数量补给、历史每日邮件及定时清理完整读取，不因活动期限已结束跳过解析");
    }

    public static void RunCore()
    {
        int checks = Count;
        string[] strings = ["[reset item]", "[refill item]", "[/refill item]", "[account refill item]", "[/account refill item]",
            "[charac daily item mail]", "[/charac daily item mail]", "[Limit Item Usage Info]", "2030-05-02 06:00:00", "2030-05-04 06:00:00", "[/reset item]"];
        PvfToken[] tokens = [new(3,0),new(0,9004),new(6,8),new(3,10),
            new(3,1),new(0,9001),new(0,3),new(6,9),new(0,0),new(0,9005),new(0,0),new(6,9),new(0,0),new(3,2),
            new(3,3),new(0,9002),new(0,2),new(6,9),new(0,10),new(3,4),
            new(3,5),new(0,9003),new(0,2),new(0,1),new(0,1281),new(0,1282),new(0,10),new(0,99),new(6,9),new(3,6),
            new(3,7),new(0,0),new(0,9001),new(0,2),new(0,2),new(0,9002),new(0,1),new(0,3),new(0,0),new(0,2)];
        string Resolve(int id) => strings[id];
        var rules = ServerItemUsageCatalog.Parse(new(tokens, Resolve), Resolve);
        That(rules.Limits.Length == 3 && rules.Resets is [{ ItemId: 9004 }] && rules.Refills.Length == 2 &&
            rules.AccountRefills is [{ ItemId: 9002, Count: 2, MinimumLevel: 10 }] && rules.Mails is [{ IntervalDays: 1, SubjectCode: 1281 }],
            "五类资源声明完整解析，账号补给和每日邮件不被忽略");
        var old = tokens.Take(14).Where((_, index) => index is not (8 or 12)).ToArray();
        That(ServerItemUsageCatalog.Parse(new(old,Resolve),Resolve).Refills.Length == 2, "旧三列补给仍兼容，不吞掉下行物品ID");
        Reject<InvalidDataException>(() => ServerItemUsageCatalog.Parse(new([.. tokens,new(0,1)],Resolve),Resolve), "限次行截断不静默解析");

        var zone = TimeZoneInfo.CreateCustomTimeZone("server-items-test", TimeSpan.FromHours(8), "test", "test");
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2030-05-01T22:00:00Z"), zone);
        int day = FatigueDailyReset.Day(clock.GetUtcNow(),zone);
        var usage = ServerItemUsageState.Empty;
        foreach (var (type,id,limit) in new[] { (0,9001,2),(2,9002,1),(3,0,2) })
        {
            That(ServerItemUsageState.TryConsume(usage,rules,(byte)type,id,limit,clock.GetUtcNow(),zone,out var full) &&
                !ServerItemUsageState.TryConsume(full,rules,(byte)type,id,1,clock.GetUtcNow(),zone,out _) &&
                !ServerItemUsageState.TryConsume(full,rules,(byte)type,id,1,clock.GetUtcNow().AddDays(-1),zone,out _) &&
                ServerItemUsageState.TryConsume(full,rules,(byte)type,id,1,clock.GetUtcNow().AddDays(1),zone,out var next) && next.Uses.Single().Used == 1,
                "使用、购买、普通寄信各自每日额度，跨日恢复、回拨拒绝 " + type);
            var utcBefore = DateTimeOffset.Parse("2030-05-02T05:59:59Z");
            That(ServerItemUsageState.TryConsume(usage,rules,(byte)type,id,limit,utcBefore,TimeZoneInfo.Utc,out var beforeLocalReset) &&
                !ServerItemUsageState.TryConsume(beforeLocalReset,rules,(byte)type,id,1,utcBefore,TimeZoneInfo.Utc,out _) &&
                ServerItemUsageState.TryConsume(beforeLocalReset,rules,(byte)type,id,1,utcBefore.AddSeconds(1),TimeZoneInfo.Utc,out var afterLocalReset) &&
                afterLocalReset.Uses.Single().Used == 1 && ShopPurchaseCount.Today(utcBefore) == ShopPurchaseCount.Today(utcBefore.AddSeconds(1)),
                "全局物品限次在非东八区本地六点重置，不改变既有商店日界 " + type);
        }
        var template = new ItemTemplate(9001,ItemKind.Consumable,0,1,0,100,0,0,0,"[trade]",ushort.MaxValue,0,"补给");
        var items = new ItemCatalog(null!,new Dictionary<int,string>(),new Dictionary<int,string>(),0);
        items.PrefillTemplates(Enumerable.Range(9001,6).Select(id => new KeyValuePair<int,ItemTemplate>(id,template with
            { Id=id, AttachType=id==9002 ? "[account]" : "[trade]" })));
        var service = new ServerItemCycleService(rules,items,new("[gold limit from level]\n0 1000000\n[/gold limit from level]"),
            new("[piece list]\n90001 90002\n[/piece list]"),clock);
        string root = Path.Combine(Path.GetTempPath(),"server-items-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root,"game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("server-items",()=>{});
            Character Create(string name) => repository.Create(owner,new(name,new(1,0,10,10),null) { Level=10,
                Inventory=new([new(0,new(InventoryListType.Main,65),new(9001,ItemKind.Consumable,1) { EnchantCardId=123,ExpireTime=2000000000 }),
                    new(0,new(InventoryListType.Main,66),new(9004,ItemKind.Consumable,1)),new(0,new(InventoryListType.Main,67),new(9005,ItemKind.Consumable,1))]) });
            var first = Create("CycleFirst");
            var second = Create("CycleSecond");
            var utcClock = new DailyBoosterTestClock(DateTimeOffset.Parse("2030-05-02T05:59:59Z"), TimeZoneInfo.Utc);
            var utcService = new ServerItemCycleService(rules,items,new("[gold limit from level]\n0 1000000\n[/gold limit from level]"),
                new("[piece list]\n90001 90002\n[/piece list]"),utcClock);
            var utcBefore = utcService.Maintain(first,repository.ReadAccountItemRefills(owner),true).Plan!;
            utcClock.Advance(TimeSpan.FromSeconds(1));
            var utcAfter = utcService.Maintain(first,repository.ReadAccountItemRefills(owner),true).Plan!;
            That(utcBefore.After.ServerItems.RefillDay == day - 1 && utcAfter.After.ServerItems.RefillDay == day &&
                utcBefore.AccountRefillClaims.Single().After.Day == day - 1 && utcAfter.AccountRefillClaims.Single().After.Day == day,
                "非东八区角色及账号补给按本机六点各自记录服务日");
            var plan = service.Maintain(first,repository.ReadAccountItemRefills(owner),true).Plan!;
            var source = first.Inventory.Find(new(InventoryListType.Main,65))!;
            That(plan.After.Find(source.Position) == source with { Core=source.Core with { Value=3 } } &&
                !plan.After.Items.Any(item=>item.Core.ItemId is 9004 or 9005) && plan.DailyItemMails.Length==1,
                "补给按目标数量赋值、保留UID和期限，零数量与到期清理生效，邮件计划不依赖背包容量");
            using var sql = new SqliteConnection("Data Source="+path); sql.Open();
            void Execute(string text) { using var command=sql.CreateCommand(); command.CommandText=text; command.ExecuteNonQuery(); }
            long Scalar(string text) { using var command=sql.CreateCommand(); command.CommandText=text; return Convert.ToInt64(command.ExecuteScalar()); }
            Execute("CREATE TRIGGER fail_daily_mail BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'daily mail rollback'); END");
            Reject<SqliteException>(()=>repository.CommitServerItemCycle(owner,first,plan),"每日邮件末段失败");
            var rolled=repository.LoadCharacter(owner,first.Slot);
            That(rolled.Revision==first.Revision && rolled.Inventory.Items.SequenceEqual(first.Inventory.Items) &&
                Scalar("SELECT count(*) FROM account_server_item_refills")==0 && Scalar("SELECT count(*) FROM character_server_item_usage")==0,
                "背包补给、删除、账号归属和邮件日戳全部回滚");
            Execute("DROP TRIGGER fail_daily_mail");
            first=repository.CommitServerItemCycle(owner,first,plan).Character;
            That(first.Inventory.Items.Single(item=>item.Core.ItemId==9002).Core.Count==2 &&
                repository.ReadAccountItemRefills(owner)[9002] == new AccountItemRefillState(day,first.Id,true) &&
                Scalar("SELECT count(*) FROM mailbox_messages")==1,"首个上线角色获得账号绑定模板补给，全部提交后才记领取日");
            var unchanged=service.Maintain(first,repository.ReadAccountItemRefills(owner),true).Plan!;
            That(!unchanged.Changed && unchanged.DailyItemMails.IsEmpty,"同日重登不补给、不重复寄信、不续期限");
            var otherPlan=service.Maintain(second,repository.ReadAccountItemRefills(owner),true).Plan!;
            second=repository.CommitServerItemCycle(owner,second,otherPlan).Character;
            That(!second.Inventory.Items.Any(item=>item.Core.ItemId==9002) && second.Inventory.Items.Single(item=>item.Core.ItemId==9001).Core.Count==3 &&
                Scalar("SELECT count(*) FROM mailbox_messages")==2,"同账号第二角色只享有自身补给和邮件，不再领取账号补给");
            var oldMail=repository.DeliverMail(new("reset-item-fixture",new(first.Id,owner.Account,first.Name,first.Job,first.GrowType,first.Level),
                "系统","清理样品",0,[new(0,new(InventoryListType.Main,0),InventoryRewardGrantService.Create(items.Get(9004)))],clock.GetUtcNow().ToUnixTimeSeconds()));
            That(oldMail.Accepted,"旧期限目标的测试邮件正常投递");
            var purged=repository.CommitServerItemCycle(owner,first,service.Maintain(first,repository.ReadAccountItemRefills(owner),true).Plan!);
            first=purged.Character;
            That(purged.MailboxChanged && Scalar("SELECT count(*) FROM mailbox_attachments WHERE mail_id="+oldMail.MailId)==0 &&
                Scalar("SELECT count(*) FROM mailbox_messages")==3,"已到期清理规则也回收邮件目标附件，保留信件文字及其他补给邮件");
            Reject<InvalidOperationException>(()=>repository.CommitServerItemCycle(owner,rolled,plan),"旧revision不能重放补给");
            var counted=service.ApplyUsage(first,new(true,0,new(first.Inventory,first.Inventory,"test_use")),0,9001,1);
            var countedRole=repository.CommitInventory(owner,first,counted.Plan!);
            That(new CharacterRepository(path).LoadCharacter(owner,first.Slot).Inventory.ServerItems.Uses is [{ Used:1 }],"限次随物品计划提交并在重开库后保存");
            var crossing = service.ApplyUsage(countedRole,new(true,0,new(countedRole.Inventory,countedRole.Inventory,"test")),0,9001,1).Plan!;
            clock.Advance(TimeSpan.FromDays(1));
            Reject<InventoryQualificationExpiredException>(()=>repository.CommitInventory(owner,countedRole,crossing),"跨06:00的旧日使用额度计划拒绝而非计入新日");
            var full = countedRole with { Inventory=countedRole.Inventory with
                { Items=Enumerable.Range(65,56).Select(slot=>new InventoryItem(slot,new(InventoryListType.Main,(short)slot),new(9006,ItemKind.Consumable,1))).ToImmutableArray() } };
            var reserved = service.Maintain(full,repository.ReadAccountItemRefills(owner),true).Plan!;
            That(reserved.AccountRefillClaims is [{ After.Completed:false }] && reserved.AccountRefillClaims[0].After.CharacterId==first.Id,
                "账号补给满包保留首个角色归属，不靠切角绕过");
            var accountDays = repository.ReadAccountItemRefills(owner).SetItem(9002,reserved.AccountRefillClaims[0].After);
            That(service.Maintain(second,accountDays,true).Plan!.AccountRefillClaims.IsEmpty,"另一角色不能接走当日未完成账号补给");
            var available=full with { Inventory=reserved.After with { Items=[] } };
            var retry=service.Maintain(available,accountDays).Plan!;
            That(retry.AccountRefillClaims is [{ After.Completed:true }] && retry.After.Items.Any(item=>item.Core.ItemId==9002) &&
                retry.After.Items.Single(item=>item.Core.ItemId==9001).Core.Count==3,"首个角色腾出空间后可完成原补给，未成功的角色补给也可重试");
            var consumed=available with { Inventory=retry.After with { Items=retry.After.Items.Where(item=>item.Core.ItemId!=9001).ToImmutableArray() } };
            That(!service.Maintain(consumed,accountDays.SetItem(9002,retry.AccountRefillClaims[0].After),true).Plan!.After.Items.Any(item=>item.Core.ItemId==9001),
                "已成功补给随后使用完的物品不因另一条失败重试或重登重新补充");
            using var otherOwner=new AccountOwners().Acquire("server-items-other",()=>{});
            var other=repository.Create(otherOwner,new("OtherAccount",new(1,0,10,10),null) {Level=10});
            other=repository.CommitServerItemCycle(otherOwner,other,service.Maintain(other,repository.ReadAccountItemRefills(otherOwner),true).Plan!).Character;
            That(other.Inventory.Items.Single(item=>item.Core.ItemId==9002).Core.Count==2 &&
                repository.ReadAccountItemRefills(otherOwner)[9002].CharacterId==other.Id &&
                repository.ReadAccountItemRefills(owner)[9002].CharacterId==first.Id,"不同账号补给归属及日戳独立");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
        Console.WriteLine($"PASS {Count-checks} 全局物品限次、补给、邮件、账号归属及真实事务断言");
    }
}
