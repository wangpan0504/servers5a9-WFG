using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class LevelUpTicketTests
{
    internal const int TicketId = 10006084;
    static readonly InventorySlot Slot = new(InventoryListType.Main,65);

    public static void RunCore()
    {
        int checks = Count;
        That(LevelUpTicketRequests.TryRead([65,0,0],out var slot) && slot==Slot &&
            !LevelUpTicketRequests.TryRead([65,0],out _) && !LevelUpTicketRequests.TryRead([65,0,0,0],out _),
            "418严格3字节，不复用44或515正文");
        That(LevelUpTicketRequests.TryRead([255,255,255],out slot) && slot.Index==-1 && (byte)slot.List==255,
            "非法槽位和容器保留交由业务拒绝");
        Bytes(LevelUpTicketPackets.Rejected(),[0,99,0],"418失败99对应无法使用该物品，不误报预约角色限制22");
        var template = new ItemTemplate(100,ItemKind.Consumable,0,1,0,100,0,2,1,"[trade]",ushort.MaxValue,0,"ticket");
        var raw = new StackableFile(100,"stackable/test.stk",new("[etc]",[1]),100,null,null,1,null,[])
        {
            ActionDeclaration=new("[level up ticket]",[]),DeclaredLevelUpRange=1,HasMaximumLevel=true,DeclaredMaximumLevel=94,
            DeclaredNeededMaterials=[],DeclaredUsableJobs=["[all]"],HasUsableJobs=true
        };
        That(LevelUpTicketDefinition.Parse(raw,template) is { MinimumLevel:1,MaximumLevel:94,Materials.IsEmpty:true } parsed &&
            parsed.Allows(94,15) && !parsed.Allows(95,0),"固定一级券按实际声明解释等级和职业");
        foreach (var invalid in new[] { raw with { DeclaredLevelUpRange=null },raw with { DeclaredLevelUpRange=2 },
            raw with { HasBoosterInfo=true },raw with { DeclaredNeededMaterials=[177] },
            raw with { DeclaredNeededMaterials=[177,0] },raw with { DeclaredMaximumLevel=null },
            raw with { ActionDeclaration=new("[quest clear]",[]) } })
            That(LevelUpTicketDefinition.Parse(invalid,template) is null,"未知、随机或坏定义不被降级为固定一级券");
        That(LevelUpTicketDefinition.Parse(raw with {DeclaredMinimumLevel=null},template)!.MinimumLevel==template.MinimumLevel &&
            LevelUpTicketDefinition.Parse(raw with {DeclaredMinimumLevel=null,HasMinimumLevel=true},template) is null,
            "最低等级缺省沿物品模板，坏声明不能变成缺省");
        Console.WriteLine($"PASS {Count-checks} 升级券原始声明及418协议断言");
    }

    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = SelfTestCatalogs.Growth(pvf,skills);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        var service = new LevelUpTicketService(items.Items,growth,items.EpicPieces);
        That(items.Items.LevelUpTicket(TicketId) is { MinimumLevel:1,MaximumLevel:85,Jobs:ushort.MaxValue } &&
            items.Items.LevelUpTicket(490705250) is { MinimumLevel:85,MaximumLevel:94 } &&
            items.Items.LevelUpTicket(490008526) is { Jobs:32768 } &&
            items.Items.LevelUpTicket(490001017) is { MinimumLevel:0,MaximumLevel:84,Materials:[{ItemId:490001023,Count:20}] } &&
            items.Items.LevelUpTicket(10004716) is { Jobs:16,Materials:[{ItemId:10004713,Count:5}] },
            "真实PVF全职业、枪剑士、圣职者和附加材料声明均按LST解析");
        using (var f = Make())
        {
            var before = f.Read();
            var source = service.Prepare(before,Slot);
            That(QuestQuickClearPlan.TryCreateLevelUpTicket(before,Guid.NewGuid(),f.Catalog,source,
                ImmutableDictionary<int,byte>.Empty,out var plan,out string reason,towns),"升级后沿手册规则准备主线: "+reason);
            That(plan!.TargetLevel==4 && plan.CutoffLevel==3 && plan.ScopeQuestIds.SequenceEqual([100,101]) &&
                !plan.ScopeQuestIds.Contains(102) && !plan.ScopeQuestIds.Contains(103),
                "升级到4后只完成低一级主线，不额外清普通支线或当前等级任务");
            var gain = checked((uint)(growth.GetLevelStartExperience(4)-before.Learning.Experience));
            Bytes(LevelUpTicketPackets.Used(source),TcpPeer.Build(w=>
            { w.Write((byte)1);w.Write((ushort)65);w.Write((byte)0);w.Write(gain);w.Write((ushort)growth.GetSpDelta(3,4));w.Write((ushort)0); }),
                "418完整12字节含真实差额，不重复使用绝对SP");
            var committed = f.Repository.CommitQuestQuickClear(f.Owner,plan);
            That(committed.Character.Level==4 && committed.Character.Learning.Experience==growth.GetLevelStartExperience(4) &&
                committed.Character.Inventory.Find(Slot) is null && committed.Character.Inventory.Gold==before.Inventory.Gold &&
                committed.Character.Learning.BonusSp==before.Learning.BonusSp && committed.Character.Learning.BonusTp==before.Learning.BonusTp &&
                committed.Character.Revision==before.Revision+1 && Same(committed.Character,f.Read()),
                "扣一张、等级经验、两树额外点数、完成位同事务且只加一次revision");
            That(f.Repository.CommitQuestQuickClear(f.Owner,plan) is {Duplicate:true,ProjectionCurrent:true} &&
                Same(committed.Character,f.Read()),"精确回执重放不重复加经验或扣券");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(f.Session.Selected!,committed.Character),"重新装载保留真实经验、两树、任务和耗尽源");
        }
        using (var f = Make())
        {
            var before=f.Read(); var source=service.Prepare(before,Slot);
            QuestQuickClearPlan.TryCreateLevelUpTicket(before,Guid.NewGuid(),f.Catalog,source,
                ImmutableDictionary<int,byte>.Empty,out var plan,out _,towns);
            f.Sql("CREATE TRIGGER level_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'level rollback'); END");
            TestCheck.Reject<SqliteException>(()=>f.Repository.CommitQuestQuickClear(f.Owner,plan!),"回执末段真实SQL失败");
            That(Same(before,f.Read()) && Same(before,f.Session.Selected!),"末段失败回滚源、成长、技能、任务和revision");
            f.Sql("DROP TRIGGER level_abort");
            That(f.Repository.CommitQuestQuickClear(f.Owner,plan!).Character.Level==4,"明确回滚后原冻结计划可以重试一次");
        }
        using (var f=Make(quests:BranchQuests()))
        {
            var before=f.Read(); var first=f.Session.UseLevelUpTicket(Slot,service,f.Catalog)!;
            That(first.Prepared is {PendingQuestions:[100]} && Same(before,f.Read()),"遇到问题先等待真实答案，不能先升级扣券");
            var rejected=f.Session.AnswerManualQuestSkip([new(100,2)],f.Catalog)!;
            That(rejected.Commit is null && Same(before,f.Read()),"无效回答不授予等级或默认分支");
            var result=f.Session.AnswerManualQuestSkip([new(100,1)],f.Catalog)!;
            That(result.Commit is not null && result.Character.Level==4 && result.Character.QuestAnswers[100]==1 &&
                result.Character.CompletedQuests.Contains(102) && !result.Character.CompletedQuests.Contains(101),
                "原生1431沿升级券上下文提交，只完成所选主线分支");
            f.Session.MarkQuestTicketAcknowledged(result);
            That(f.Session.AnswerManualQuestSkip([new(100,1)],f.Catalog) is {Commit.Duplicate:true,TicketAcknowledged:true},
                "重复最终回答不重放418增量ACK");
            f.Session.ReturnToRoster();f.Session.Select(0);f.Session.MarkReady();
            That(f.Session.AnswerManualQuestSkip([new(100,1)],f.Catalog)!.Commit is null,"换角清除旧升级问题上下文");
        }
        foreach (byte level in new byte[] {49,94})
        using (var f=Make(level,level==49 ? TicketId : 490705250))
        {
            // 通用低等级券覆盖TP门槛；85~94券只用于94上界。
            var before=f.Read();
            var source=service.Prepare(before,Slot);
            That(source.Growth.TargetLevel==level+1 && source.Growth.Learning.Trees.Zip(before.Learning.Trees)
                .All(pair=>pair.First.Tp-pair.Second.Tp==growth.GetTpDelta(level,level+1)),"跨50和95的TP差额及等级上界按统一成长规则");
        }
        using(var f=Make(61,10004716,job:4,materialCount:6))
        {
            var result=f.Session.UseLevelUpTicket(Slot,service,f.Catalog)!;
            That(result.Commit is not null && result.Character.Level==62 && result.Character.Inventory.Find(Slot) is null &&
                result.Character.Inventory.Items.Single(row=>row.Core.ItemId==10004713).Core.Count==1 && Same(result.Character,f.Read()),
                "职业券附加五份材料与升级和扣券同事务提交");
        }
        foreach(var (job,count) in new (byte,int)[] {(0,6),(4,4)})
        using(var f=Make(61,10004716,job:job,materialCount:count))
        {
            var before=f.Read();
            TestCheck.Reject<InvalidDataException>(()=>service.Prepare(before,Slot),"职业不符或附加材料不足拒绝");
            That(Same(before,f.Read()),"失败不消耗源或材料");
        }
        using (var f=Make())
        {
            var before=f.Read();var item=before.Inventory.Find(Slot)!;
            foreach(var core in new[] {item.Core with {EquipmentLockId=1},item.Core with {ExpireTime=1},item.Core with {Value=0}})
                TestCheck.Reject<InvalidDataException>(()=>service.Prepare(before with {Inventory=before.Inventory with
                    {Items=before.Inventory.Items.Select(row=>row.Position==Slot ? row with {Core=core}:row).ToImmutableArray()}},Slot),
                    "锁定、到期和空数量不消费");
            TestCheck.Reject<InvalidDataException>(()=>service.Prepare(before with {Level=95},Slot),"满级不消耗升级券");
            var source=service.Prepare(before,Slot);
            QuestQuickClearPlan.TryCreateLevelUpTicket(before,Guid.NewGuid(),f.Catalog,source,ImmutableDictionary<int,byte>.Empty,out var stale,out _,towns);
            f.Repository.Save(f.Owner,before,before.Position with {X=checked((ushort)(before.Position.X+1))},before.Return);
            TestCheck.Reject<InvalidOperationException>(()=>f.Repository.CommitQuestQuickClear(f.Owner,stale!),"旧revision不能消费旧升级计划");
            using var newer=f.Owners.Acquire(f.Owner.Account,()=>{});
            TestCheck.Reject<InvalidOperationException>(()=>f.Repository.CommitQuestQuickClear(f.Owner,stale!),"旧generation不能消费升级券");
        }
        Console.WriteLine($"PASS {Count-checks} 升级券资源、成长、主线、事务及owner断言");

        QuestOrdinaryCompletionTests.Fixture Make(byte level=3,int ticket=TicketId,StoryQuest[]? quests=null,byte job=0,int materialCount=0)
        {
            var definitions=quests ?? PlainQuests();
            return new(towns,characters,skills,growth,parameters,items,definitions[0],materialCount:0,definitions:definitions,active:[],
                configure:candidate=>
                {
                    if(job!=0) candidate=CharacterCreation.Prepare(candidate.Name,characters.Get(job),towns.Room,towns.InitialReturn,skills.Get);
                    var inventory=candidate.Inventory.Items.Add(new(0,Slot,new(ticket,ItemKind.Consumable,1)));
                    if(materialCount>0)
                    {
                        var template=items.Items.Get(10004713);
                        short materialSlot=checked((short)(DfoServer.Game.Inventory.Services.ItemSlotBoundService.Range(template.Kind,candidate.Inventory.Expansion).Start+1));
                        inventory=inventory.Add(new(0,new(InventoryListType.Main,materialSlot),
                            DfoServer.Game.Inventory.Services.InventoryRewardGrantService.Create(template with {Expiration=new()},materialCount)));
                    }
                    return candidate with
                    {
                        Level=level,InitialLearning=SkillPointLedger.Synchronize(job,0,level,candidate.InitialLearning with
                            {Experience=growth.GetLevelStartExperience(level)+1,BonusSp=17,BonusTp=5},skills),
                        Inventory=candidate.Inventory with {Items=inventory}
                    };
                });
        }
    }

    internal static StoryQuest[] PlainQuests() =>
    [
        QuestEligibilityTests.Quest(100),
        QuestEligibilityTests.Quest(101) with {MinimumLevel=3,Definition=new([]) {Job="[all]",PrerequisiteGroups=[[100]]}},
        QuestEligibilityTests.Quest(102) with {MinimumLevel=4},
        QuestEligibilityTests.Quest(103) with {Grade="[normal]"}
    ];

    internal static StoryQuest[] BranchQuests() =>
    [
        QuestEligibilityTests.Quest(100) with {Condition="[question]",Definition=new([
            new("question info",[new(6,0,"title"),new(6,0,"question"),new(6,0,"first"),new(6,0,"second")],[])]) {Job="[all]"}},
        QuestEligibilityTests.Quest(101) with {Definition=new([]) {Job="[all]",RequiredAnswers=[new(100,0)]}},
        QuestEligibilityTests.Quest(102) with {Definition=new([]) {Job="[all]",RequiredAnswers=[new(100,1)]}}
    ];
}
