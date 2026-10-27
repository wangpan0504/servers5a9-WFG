using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestThresholdRelationTests
{
    public static void RunCore()
    {
        int checks = Count;
        var parent = Parent(200,[100,101,102,103],2);
        var children = parent.IntData.Select(id => Child(id,parent.Id)).ToArray();
        var catalog = Catalog(parent,children);
        var origin = new Character(1,0,"Threshold",1,new(38,0,450,250),null);
        var activity = Active(0,200,1);
        var selected = origin with { ActiveMainQuests = [activity] };
        That(QuestRelationProgress.ThresholdParent(parent,catalog) && QuestCoverage.HasOrdinaryDeclarations(parent),
            "完整sub4引用及最小阈值可以复用普通声明与关系模型");
        for (byte job = 0; job < 16; job++)
        {
            That(QuestEligibility.Evaluate(origin with { Job=job },parent,catalog) is { Eligible:true, Visible:false } &&
                !QuestEligibility.Visible(origin with { Job=job },catalog).Contains(parent.Id),
                "隐藏阈值父保留真实31资格，不加入普通21列表: " + job);
            That(children.All(child => QuestEligibility.Evaluate(selected with { Job=job },child,catalog) is { Eligible:true, Visible:false }),
                "所有职业可独立接取全部合格子项，不要求先完成资源首项: " + job);
        }
        That(children.All(child => !QuestEligibility.Evaluate(origin,child,catalog).Eligible && QuestRelationProgress.DialogueChild(child,catalog)),
            "隐藏子入口要求活动父及真实资源引用");
        That(!QuestEligibility.Evaluate(selected,children[1] with { MinimumLevel=20 },catalog).Eligible &&
            !QuestEligibility.Evaluate(selected,children[1] with { Definition=children[1].Definition! with { Job="[at priest]" } },catalog).Eligible &&
            !QuestEligibility.Evaluate(selected,children[1] with { Definition=children[1].Definition! with { PrerequisiteGroups=[[103]] } },catalog).Eligible &&
            !QuestEligibility.Evaluate(selected,children[1] with { Definition=children[1].Definition! with { Collisions=[200] } },catalog).Eligible,
            "阈值父不能覆盖子项自己的等级、职业、前置和冲突资格");
        That(QuestEligibility.Evaluate(selected with { Job=14 },children[1] with
            { Definition=children[1].Definition! with { Job="[at priest]" } },catalog).Eligible,
            "子项职业条件仍按真实角色匹配");
        That(QuestRelationProgress.Initial(parent,catalog,[]) == 1 &&
            QuestRelationProgress.Initial(parent,catalog,[100,100,999]) == 1 &&
            QuestRelationProgress.Initial(parent,catalog,[100,103]) == 0,
            "父采用兼容消费的正数1/达成0，仅计算去重的真实引用完成位");
        var one = parent with { Definition=parent.Definition! with { MinimumClearCount=1 } };
        That(QuestRelationProgress.Initial(one,Catalog(one,children),[103]) == 0,
            "最小阈值一项即可达成，不强制全部引用子完成");
        var account = selected with { CompletedQuests=[100],AccountCompletedQuests=[103] };
        That(QuestRelationProgress.Initial(parent,catalog,QuestEligibility.Completed(account)) == 0 &&
            QuestLifecyclePlan.SetCondition(account,parent,0).ActiveAfter.Single() == activity with { ConditionState=0 },
            "真实角色及账号完成集合共同达到阈值，条件更新保留接取身份和槽位");
        DfoServer.Testing.TestCheck.Reject<InvalidDataException>(() => QuestLifecyclePlan.SetCondition(selected,parent,0),"客户端0不能替代缺少的真实子完成记录");
        var refreshed = QuestRelationProgress.Refresh([activity,Active(1,100,1)],[100,101],catalog);
        That(refreshed[0] == activity with { ConditionState=0 } && refreshed[1].QuestId == 100 && refreshed[1].ConditionState == 1 &&
            QuestRelationProgress.Refresh([activity with { AcceptanceId=Guid.Empty }],[100,101],catalog)[0].ConditionState == 1,
            "汇总只改变有效活动父条件，不伪造子完成或接管旧无身份活动槽");
        foreach (var invalid in new[]
        {
            parent with { SubType=3 }, parent with { IntData=[100,100] }, parent with { IntData=[100,200] },
            parent with { IntData=[100,29999] }, parent with { Definition=parent.Definition! with { MinimumClearCount=0 } },
            parent with { Definition=parent.Definition! with { MinimumClearCount=5 } }
        })
        {
            var definitions = Catalog(invalid,children);
            That(!QuestRelationProgress.ThresholdParent(invalid,definitions) &&
                QuestRelationProgress.Refresh([activity with { ConditionState=17 }],[100,101,102,103],definitions)[0].ConditionState == 17,
                "未确认子类型、不完整引用或无效阈值不启用、不改写活动进度");
        }
        var wrongChild = children[1] with { MainQuest=201 };
        That(!QuestRelationProgress.ThresholdParent(parent,Catalog(parent,children.Select(row => row.Id==wrongChild.Id ? wrongChild : row))),
            "不能仅凭父IntData授权不属于该父的任务");
        var sequential = parent with { Grade="[epic]",Condition="[quest clear]",SubType=2,
            Definition=parent.Definition! with { MinimumClearCount=null } };
        var ordered = Catalog(sequential,children);
        That(QuestRelationProgress.Initial(sequential,ordered,[100]) == 3 &&
            !QuestEligibility.Evaluate(selected,children[1],ordered).Eligible,
            "原顺序父剩余计数和首未完成子资格保持原合同");
        That(children.All(child => !QuestRelationProgress.ZeroRewardChild(child)),
            "物品空声明不等于零经验子任务，阈值不会开启无奖励自动交付");

        var gifted = children[2] with { Definition=children[2].Definition! with
            { AcceptanceRewards=Rows(500,7,501,5).ToImmutableArray(),CannotGiveup=true } };
        var seeking = QuestEligibilityTests.Quest(300) with { Condition="[seeking]",IntData=[500,3] };
        var otherGift = QuestEligibilityTests.Quest(301) with
            { Definition=new([]) { Job="[all]",AcceptanceRewards=Rows(501,2).ToImmutableArray() } };
        var entries = new[] { parent,children[0],children[1],gifted,children[3],seeking,otherGift };
        catalog = new(entries.ToDictionary(row => row.Id));
        var staleCompleted = Active(1,100,0);
        var closing = selected with
        {
            CompletedQuests=[100,101],ActiveMainQuests=[activity,staleCompleted,Active(2,102,1),Active(3,103,1),Active(4,300,0),Active(5,301,1)],
            Inventory=new([Item(1,177,500,10),Item(2,178,501,6),Item(3,179,600,3),
                new(4,new(InventoryListType.Main,180),new(500,ItemKind.Consumable,8))])
        };
        string original = JsonSerializer.Serialize(closing);
        var closed = QuestLifecyclePlan.CloseThresholdChildren(closing,parent,catalog,DateTimeOffset.UtcNow);
        That(closed.Removed.Select(row => row.QuestId).SequenceEqual(new ushort[] {102,103}) &&
            closed.Removed.All(row => closing.ActiveMainQuests.Contains(row)) &&
            closed.ActiveAfter.Select(row => row.QuestId).SequenceEqual(new ushort[] {200,100,300,301}),
            "父交付只关闭未完成活动引用子，保留父、已完成子旧槽及无关活动；关闭返回原身份");
        That(closed.Inventory.After.Items.Single(row => row.Core.ItemId==500 && row.Core.Kind==ItemKind.Quest).Core.Count == 3 &&
            closed.Inventory.After.Items.Single(row => row.Core.ItemId==501).Core.Count == 2 &&
            closed.Inventory.After.Items.Single(row => row.Core.ItemId==600) == closing.Inventory.Items.Single(row => row.Core.ItemId==600) &&
            closed.Inventory.After.Items.Single(row => row.Core.Kind==ItemKind.Consumable) == closing.Inventory.Items.Single(row => row.Core.Kind==ItemKind.Consumable) &&
            closed.Inventory.After.Items.All(row => closing.Inventory.Items.Any(old => old.Uid==row.Uid)) &&
            original == JsonSerializer.Serialize(closing),
            "回收只处理任务物品，保留其它活动的需求和赠品量、无关Core/UID及原不可变角色快照");
        DfoServer.Testing.TestCheck.Reject<InvalidDataException>(() => QuestLifecyclePlan.Giveup(closing,gifted,catalog,DateTimeOffset.UtcNow),
            "手动放弃仍遵循CannotGiveup，父交付的真实自动子关闭不复用手动入口门禁");
        var installed = closing with { ActiveMainQuests=closed.ActiveAfter.ToArray(),Inventory=closed.Inventory.After };
        var replay = QuestLifecyclePlan.CloseThresholdChildren(installed,parent,catalog,DateTimeOffset.UtcNow);
        That(replay.Removed.Length == 0 && !replay.Inventory.Changed && replay.ActiveAfter.SequenceEqual(closed.ActiveAfter) &&
            installed.CompletedQuests.SequenceEqual([100,101]) && installed.Revision == closing.Revision &&
            installed.Learning == closing.Learning && installed.Level == closing.Level,
            "已关闭分支再次规划不回收、不发奖、不写其完成位或推进revision");
        Console.WriteLine($"PASS {Count-checks} 阈值父全部子资格、真实完成集合、关闭与任务物品回收纯合成断言");
    }

    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int checks = Count;
        var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf); var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var parent = Parent(200,[100,101,102,103],2) with { Attributes=["not give exp quest"],Rewards=Rows(2058,2) };
        var first = Child(100,200) with { Rewards=Rows(2058,1) };
        var second = Child(101,200) with { Rewards=Rows(2058,2) };
        var unused = Child(102,200) with { Definition=new([]) { Job="[all]",AcceptanceRewards=Rows(10165055,4).ToImmutableArray() } };
        var last = Child(103,200);
        var seeking = QuestEligibilityTests.Quest(300) with { Condition="[seeking]",IntData=[10165055,3],Attributes=["not give exp quest"] };
        var definitions = new[] { parent,first,second,unused,last,seeking };
        using (var f = Make(active:[]))
        {
            That(f.Change(200,QuestLifecycleAction.Accept)!.Commit is not null &&
                f.Read().ActiveMainQuests is [{ QuestId:200,ConditionState:1 }] && !QuestEligibility.Visible(f.Read(),f.Catalog).Contains(200),
                "合成隐藏阈值父真实接取保持正数初值及独立身份，不投影普通21");
            That(f.Change(102,QuestLifecycleAction.Accept)!.Commit is not null &&
                f.Change(101,QuestLifecycleAction.Accept)!.Commit is not null &&
                f.Change(100,QuestLifecycleAction.Accept)!.Commit is not null &&
                f.Change(300,QuestLifecycleAction.Accept)!.Commit is not null,
                "Session接受任意资源顺序的合法真实31及实际赠品，不代接或顺序等待");
            var before = f.Read(); var forged = before with
                { ActiveMainQuests=before.ActiveMainQuests.Select(row => row.QuestId==200 ? row with { ConditionState=0 } : row).ToArray() };
            That(QuestCompletionPlan.Prepare(forged,parent,ushort.MaxValue,f.Catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,
                growth,DateTimeOffset.UtcNow).Plan is null && f.Change(200,QuestLifecycleAction.Condition,new(null,0,0))!.Commit is null &&
                Same(before,f.Read()),"伪造父scalar0不能获得交付或33资格");
            f.Change(101,QuestLifecycleAction.Condition,new(null,0,0));
            var child1 = f.Finish(ushort.MaxValue,101).Receipt!.Commit;
            That(child1.ExperienceReward.Amount > 0 && child1.Rewards.Sum(row => row.GrantedCount)==2 &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId==200).ConditionState==1 && !f.Read().CompletedQuests.Contains(200),
                "第一条真实子完整发经验/资产，仅累计真实完成，不提前完成父");
            f.Change(100,QuestLifecycleAction.Condition,new(null,0,0));
            var child2 = f.Finish(ushort.MaxValue,100).Receipt!.Commit;
            That(child2.ExperienceReward.Amount > 0 && child2.Rewards.Sum(row => row.GrantedCount)==1 &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId==200).ConditionState==0 &&
                f.Read().CompletedQuests.Order().SequenceEqual([100,101]) && f.Receipts==2,
                "第二子完整领奖和父达阈值归零同事务，未选分支没有完成或奖励");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            var ready = f.Read(); var online = f.Session.Selected;
            That(ready.ActiveMainQuests.Single(row => row.QuestId==200).ConditionState==0 && Same(ready,f.Session.Selected!),
                "父达成和未选活动子在正常重新选角中恢复");
            f.Sql("CREATE TRIGGER threshold_abort AFTER INSERT ON character_quest_completion_receipts WHEN NEW.quest_id=200 BEGIN SELECT RAISE(ABORT,'threshold parent rollback'); END");
            DfoServer.Testing.TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue,200),"父交付末段SQL失败");
            That(Same(ready,f.Read()) && f.Receipts==2 && ReferenceEquals(online,f.Session.Selected),
                "父完成、奖励UID、未选子关闭、回收和revision整事务回滚");
            f.Sql("DROP TRIGGER threshold_abort");
            var finished = f.Finish(ushort.MaxValue,200).Receipt!.Commit;
            That(finished is { ConditionType:70,ExperienceReward.Amount:0 } && finished.Rewards.Sum(row => row.GrantedCount)==2 &&
                finished.Character.ActiveMainQuests is [{ QuestId:300 }] &&
                finished.Character.CompletedQuests.Order().SequenceEqual([100,101,200]) &&
                finished.Character.Inventory.Items.Single(row => row.Core.ItemId==10165055).Core.Count==3 &&
                finished.Character.Revision==ready.Revision+1 && f.Receipts==3,
                "父70正常奖励和未选子清槽/回收一次提交，保留其它活动需求，不伪造子完成");
            using (var db = f.Open())
            using (var command = db.CreateCommand())
            {
                command.CommandText="SELECT plan_snapshot FROM character_quest_completion_receipts WHERE quest_id=200";
                using var snapshot = JsonDocument.Parse((string)command.ExecuteScalar()!);
                var threshold = snapshot.RootElement.GetProperty("Threshold");
                var closed = threshold.GetProperty("ClosedChildren").EnumerateArray().ToArray();
                That(threshold.GetProperty("MinimumClearCount").GetInt32()==2 &&
                    threshold.GetProperty("RequiredChildren").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual([100,101,102,103]) &&
                    closed.Length==1 && closed[0].GetProperty("QuestId").GetUInt16()==102 &&
                    closed[0].GetProperty("AcceptanceId").GetGuid()==ready.ActiveMainQuests.Single(row => row.QuestId==102).AcceptanceId &&
                    threshold.GetProperty("ReclaimedItems").GetArrayLength()==1,
                    "父回执冻结真实阈值引用、未选子原接取身份及回收Core来源，未选子不另写领奖回执");
            }
            var receiptCount = f.Receipts;
            var tail = f.Change(102,QuestLifecycleAction.Giveup);
            That(tail is { Outcome:QuestLifecycleOutcome.Unchanged,Commit:null } && Same(finished.Character,f.Read()) && f.Receipts==receiptCount,
                "父ACK34后真实32尾包幂等确认已关闭子，不重复回收、增revision或发奖");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(finished.Character,f.Session.Selected!) && f.Finish(ushort.MaxValue,200).Outcome==QuestFinishOutcome.AlreadyCompleted &&
                f.Change(102,QuestLifecycleAction.Accept)!.Commit is null && f.Receipts==3,
                "断线式重登保留真实完成集合，已关闭未完成子不能脱离父重新接取");
        }
        using (var f = Make(loseResult:true,active:[new(0,200,0),new(1,102,1),new(2,300,0)],
            configure:candidate => candidate with { CompletedQuests=[100,101] }))
        {
            var original = f.Read(); var online = f.Session.Selected;
            DfoServer.Testing.TestCheck.Reject<IOException>(() => f.Finish(ushort.MaxValue,200),"父领奖、关闭与回收已提交但返回丢失");
            var saved = f.Read();
            That(saved.CompletedQuests.Order().SequenceEqual([100,101,200]) && saved.ActiveMainQuests is [{ QuestId:300 }] &&
                saved.Inventory.Items.Single(row => row.Core.ItemId==10165055).Core.Count==3 &&
                saved.Revision==original.Revision+1 && ReferenceEquals(online,f.Session.Selected),
                "数据库已保存整笔父奖励与子关闭，返回丢失保持原在线快照等待原计划恢复");
            var restored = f.Finish(ushort.MaxValue,200).Receipt!.Commit;
            That(restored is { Duplicate:true,ProjectionCurrent:true } && f.Lost!.SamePlan && Same(saved,f.Session.Selected!) && f.Receipts==1 &&
                f.Change(102,QuestLifecycleAction.Giveup) is { Outcome:QuestLifecycleOutcome.Unchanged,Commit:null } && Same(saved,f.Read()),
                "原冻结回执精确恢复奖励UID及关闭结果，后续32不新增写入");
        }
        using (var f = Make(active:[new(0,200,0),new(1,102,1),new(2,300,0)],
            configure:candidate => candidate with { CompletedQuests=[100,101] }))
        {
            var original = f.Read(); string path;
            using (var db = f.Open())
            {
                path = db.DataSource;
                DatabaseSchemaTests.RewindQuestSystemToV13(db);
            }
            var repository = new DfoServer.Persistence.CharacterRepository(path,f.Catalog);
            var migrated = repository.LoadCharacter(f.Owner,0);
            That(migrated.Fatigue?.ResetDay==0 && Same(original with {Fatigue=original.Fatigue! with {ResetDay=0}},migrated),
                "真实v13结构迁移不补造服务日，保存原疲劳余额、阈值任务、材料、技能及接取身份");
            var plan = QuestCompletionPlan.Prepare(migrated,parent,ushort.MaxValue,f.Catalog,parameters,
                items.Items,items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow).Plan!;
            var committed = repository.CommitQuestCompletion(f.Owner,plan);
            That(committed.ConditionType==70 && committed.Character.ActiveMainQuests is [{ QuestId:300 }] &&
                committed.Character.CompletedQuests.Order().SequenceEqual([100,101,200]) && committed.Character.Revision==original.Revision+1,
                "单次v14迁移后的实际type70回执、父领奖和子关闭正常提交，验证迁移与新库均支持");
        }
        Console.WriteLine($"PASS {Count-checks} 合成阈值任务Session/Repository领奖、回滚、32尾包与重登断言（非主线）");

        QuestOrdinaryCompletionTests.Fixture Make(bool loseResult=false,ActiveMainQuest[]? active=null,Func<NewCharacter,NewCharacter>? configure=null) =>
            new(towns,characters,learning,growth,parameters,items,first,materialCount:5,loseResult:loseResult,
                definitions:definitions,active:active,configure:configure);
    }

    static StoryQuest Parent(int id,int[] children,int minimum) => QuestEligibilityTests.Quest(id) with
    {
        Grade="[hidden]",Condition="[clear multi quest]",SubType=4,IntData=children,Npc=2,CompleteNpc=-1,
        Definition=new([]) { Job="[all]",MinimumClearCount=minimum }
    };
    static StoryQuest Child(int id,int parent) => QuestEligibilityTests.Quest(id) with
        { Grade="[sub]",MainQuest=parent,IntData=[2],Npc=2,CompleteNpc=2 };
    static QuestCatalog Catalog(StoryQuest parent,IEnumerable<StoryQuest> children) =>
        new(children.Prepend(parent).ToDictionary(row => row.Id));
    static ActiveMainQuest Active(byte slot,ushort id,uint scalar) => new(slot,id,scalar) { AcceptanceId=Guid.NewGuid() };
    static QuestRewardToken[] Rows(params int[] values) => values.Select(value => new QuestRewardToken(0,value,null)).ToArray();
    static InventoryItem Item(long uid,short slot,int id,int count) => new(uid,new(InventoryListType.Main,slot),new(id,ItemKind.Quest,count));
}
