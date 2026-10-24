using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using OrdinaryFixture = DfoServer.QuestOrdinaryCompletionTests.Fixture;

namespace DfoServer;

internal static class AlbertRequestTests
{
    public static void Run(TownCatalog towns,CharacterCatalog characters,ItemResources items,QuestCatalog quests,
        SkillLearningCatalog skills,CharacterGrowthCatalog growth,QuestRewardParameterCatalog parameters)
    {
        int checks = Count;
        var parent = quests.Get(3458); var hunt = quests.Get(3472); var seek = quests.Get(3473);
        That(parent is {MinimumLevel:56,MaximumLevel:99,Condition:"[quest clear]",SubType:2} &&
            parent.IntData.SequenceEqual([3472,3473]) && parent.Definition!.PrerequisiteGroups.Any(group => group.SequenceEqual([3457])) &&
            hunt is {MainQuest:3458,Condition:"[hunt monster]"} && hunt.IntData.SequenceEqual([158,-1,65031,25,158,-1,70176,1]) &&
            seek is {MainQuest:3458,Condition:"[seeking]",CompleteNpc:2} && seek.IntData.SequenceEqual([10164800,3]) &&
            !QuestRelationProgress.ZeroRewardChild(hunt) && !QuestRelationProgress.ZeroRewardChild(seek),
            "阿尔伯特真实资源保持56–99等级、3457前置、顺序猎杀子与有奖励寻物子");
        // 690原截图是3473的0/3，不是哈林后继。先复现95级跳过父任务却残留有奖励子槽。
        using(var orphan = Make(0,[new(0,3458,1),new(1,3473,1)],[3457,3472,6045],level:95))
        {
            That(QuestQuickClearPlan.TryCreateManual(orphan.Read(),Guid.NewGuid(),orphan.Catalog,
                System.Collections.Immutable.ImmutableDictionary<int,byte>.Empty,out var skipped,out string reason),
                "真实95级阿尔伯特父链手册规划："+reason);
            That(skipped!.CompletedAfter.Contains(3458) && !skipped.ActiveAfter.Any(row=>row.QuestId==3473),
                "手册完成3458时必须同步清理其有奖3473活动槽，不能留下0/3孤儿任务");
            var before=orphan.Read();
            orphan.Sql("CREATE TRIGGER orphan_skip_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'orphan rollback'); END");
            Reject<SqliteException>(()=>orphan.Repository.CommitQuestQuickClear(orphan.Owner,skipped),"父跳过和子槽清理晚期故障整批回滚");
            That(QuestCompletionRepositoryTests.Same(before,orphan.Read()),"回滚保留0/3原接取与完整资产");
            orphan.Sql("DROP TRIGGER orphan_skip_abort");
            var commit=orphan.Repository.CommitQuestQuickClear(orphan.Owner,skipped);
            That(commit.Character.ActiveMainQuests.Length==0 && !commit.Character.CompletedQuests.Contains(3473) &&
                Materials(commit.Character)==0 && commit.Character.Inventory.Gold==before.Inventory.Gold && commit.Character.Learning.Experience==before.Learning.Experience,
                "跳过只清有奖子槽，不伪造子完成、不扣料补料或补发金币经验");
            That(orphan.Repository.CommitQuestQuickClear(orphan.Owner,skipped).Duplicate,"同操作重试不重复清理或发奖");
        }
        using(var legacy=Make(0,[new(0,3473,1)],[3457,3472,3458,6045],level:95))
        {
            var before=legacy.Read();
            foreach(var excluded in new[] {seek with {Grade="[side]"},seek with {MainQuest=null},
                seek with {Rewards=[new(0,0,null),new(0,0,null)]},
                seek with {Definition=seek.Definition! with {AccountQuest=true}},
                seek with {Definition=seek.Definition! with {SelectRewards=[new(0,10164800,null),new(0,1,null)]}},
                seek with {Condition="[question]"},seek with {DeclaredGoldMultiple=1}})
            {
                var isolated=new QuestCatalog(new[] {parent,hunt,excluded,quests.Get(3457)}.ToDictionary(q=>q.Id));
                That(!QuestQuickClearPlan.TryCreateOrphanRepair(before,isolated,out _),
                    "旧档清理拒绝独立、零奖励已有恢复路径、账号、选择奖励、问答及金币子任务");
            }
            legacy.Session.ReturnToRoster();
            var reconnect=new GameSession(legacy.Repository,towns,legacy.Owner,questCatalog:legacy.Catalog);
            try
            {
                legacy.Sql("CREATE TRIGGER orphan_reconnect_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'orphan reconnect rollback'); END");
                Reject<SqliteException>(()=>reconnect.Select(0),"旧档选角清理失败不能发布半修复角色");
                That(reconnect.Selected is null && QuestCompletionRepositoryTests.Same(before,legacy.Read()),"旧档修复SQL回滚且选角未发布");
                legacy.Sql("DROP TRIGGER orphan_reconnect_abort");
                reconnect.Select(0);
                var after=legacy.Read();
                That(after.ActiveMainQuests.Length==0 && after.CompletedQuests.SequenceEqual(before.CompletedQuests) &&
                    Materials(after)==0 && after.Inventory.Gold==before.Inventory.Gold && after.Learning.Experience==before.Learning.Experience,
                    "旧档重选只清孤儿槽，不补有奖子完成位或资产");
                reconnect.ReturnToRoster(); reconnect.Select(0);
                That(QuestCompletionRepositoryTests.Same(after,legacy.Read()),"重复重选修复幂等不再升revision");
            }
            finally { reconnect.End(); }
        }
        using(var ordinary=Make(0,[new(0,3458,1),new(1,3473,1)],[3457,3472],level:95))
        {
            That(!QuestQuickClearPlan.TryCreateOrphanRepair(ordinary.Read(),ordinary.Catalog,out _) &&
                ordinary.Finish(ushort.MaxValue,3458).Outcome==QuestFinishOutcome.Rejected && ordinary.Read().ActiveMainQuests.Length==2,
                "父未完成的正常0/3仍等待收集，重选修复不免费代过任务");
        }
        using(var levelup=Make(0,[new(0,3458,1),new(1,3473,1)],[3457,3472,6045],level:94,levelTicket:true))
        {
            var before=levelup.Read();
            var source=new LevelUpTicketService(items.Items,growth).Prepare(before,before.Inventory.Items.Single(row=>row.Core.ItemId==490705250).Position);
            That(QuestQuickClearPlan.TryCreateLevelUpTicket(before,Guid.NewGuid(),levelup.Catalog,source,
                System.Collections.Immutable.ImmutableDictionary<int,byte>.Empty,out var plan,out string reason,towns),"实际94→95升级券父链规划："+reason);
            var after=levelup.Repository.CommitQuestQuickClear(levelup.Owner,plan!).Character;
            That(after.Level==95 && after.ActiveMainQuests.Length==0 && after.CompletedQuests.Contains(3458) &&
                !after.CompletedQuests.Contains(3473) && Materials(after)==0 && !after.Inventory.Items.Any(row=>row.Core.ItemId==490705250),
                "真实升级券原子扣券升级并清父子槽，不补发碎片或伪造子完成");
        }
        var reset = new CharacterProgressReset(characters,skills,growth);
        // 仅准备3472已达成的持久接取，不替代158副本26只怪物的完整猎杀验收。
        using (var f = Make(2,[new(0,3458,2),new(1,3472,0)],[3457]))
        {
            That(f.Read().Level == 56 && f.Read().AcceptedStoryLevel == 1 && Materials(f.Read()) == 2,"隔离角色从56级、story1和碎片2开始");
            foreach (byte level in new byte[] {57,58})
            {
                var before = f.Read();
                string response = DevelopmentGm.Execute(f.Repository,f.Owners,account => account == f.Owner.Account ? f.Session : null,
                    DevelopmentRequest.Parse("level Ordinary "+level,f.Owner.Account),progress:reset);
                var updated = f.Read();
                That(response.StartsWith("OK ",StringComparison.Ordinal) && updated.Level == level && updated.Revision == before.Revision+1 &&
                    updated.Learning.Experience == growth.GetLevelStartExperience(level) &&
                    updated.Learning.Trees.All(tree => tree.Sp == skills.TotalSp(level) && tree.Tp == skills.TotalTp(level)) &&
                    updated.AcceptedStoryLevel == 1 && updated.ActiveMainQuests.SequenceEqual(before.ActiveMainQuests) &&
                    updated.CompletedQuests.SequenceEqual(before.CompletedQuests) && JsonSerializer.Serialize(updated.Inventory) == JsonSerializer.Serialize(before.Inventory) &&
                    QuestCompletionRepositoryTests.Same(updated,f.Session.Selected!),
                    "实际在线GM升到"+level+"后安装等级、经验和双树，同时保留story1、任务接取身份及完整资产");
                That(f.Change(3473,QuestLifecycleAction.Accept)?.Commit is null && QuestCompletionRepositoryTests.Same(updated,f.Read()),
                    "GM升级不伪造3472正式完成，也不能跳接3473："+level);
            }
            var ready = f.Read();
            That(f.Session.FinishReadyQuestChildren(f.Catalog,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth) is null &&
                QuestCompletionRepositoryTests.Same(ready,f.Read()),"达成的有奖励3472不由零奖励自动闭合入口领奖");
            var first = f.Finish(ushort.MaxValue,3472);
            That(first.Receipt?.Commit is {ConditionType:2,ExperienceReward.Amount:0} commit &&
                commit.Rewards is [{GrantedCount:2,Item.Core.ItemId:10164800}] && Materials(f.Read()) == 4 &&
                f.Read().CompletedQuests.Contains(3472) && f.Read().ActiveMainQuests is [{QuestId:3458,ConditionState:1}] && f.Receipts == 1,
                "真实3472交付只发碎片2、清原子槽并把父剩余子数2降为1");
            var firstState = f.Read();
            That(f.Finish(ushort.MaxValue,3472).Outcome == QuestFinishOutcome.AlreadyCompleted &&
                QuestCompletionRepositoryTests.Same(firstState,f.Read()),"重复3472交付不再次发碎片2");
            That(f.Change(3473,QuestLifecycleAction.Accept)?.Character.ActiveMainQuests.Any(row => row.QuestId == 3473 && row.ConditionState == 0) == true,
                "58级实际库存4/3接取3473后初值为0，story1不阻止正式寻物入口");
            var second = f.Finish(ushort.MaxValue,3473);
            That(second.Receipt?.Commit is {ConditionType:0,ExperienceReward.Amount:0} complete &&
                complete.Consumed is [{ItemId:10164800,Count:3}] && complete.Rewards is [{GrantedCount:4,Item.Core.ItemId:10164800}] &&
                Materials(f.Read()) == 5 && f.Read().CompletedQuests.Contains(3473) &&
                f.Read().ActiveMainQuests is [{QuestId:3458,ConditionState:0}] && f.Receipts == 2,
                "真实3473一次事务扣3发4，库存4变5、原接取完成且父条件归零");
            var delivered = f.Read();
            That(f.Finish(ushort.MaxValue,3473).Outcome == QuestFinishOutcome.AlreadyCompleted &&
                QuestCompletionRepositoryTests.Same(delivered,f.Read()),"重复3473交付不能再扣3发4");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            using (var database = f.Open())
                That(QuestCompletionRepositoryTests.Same(f.Session.Selected!,new CharacterRepository(database.DataSource,f.Catalog).LoadCharacter(f.Owner,0)) &&
                    Materials(f.Read()) == 5 && f.Read().AcceptedStoryLevel == 1 && f.Read().ActiveMainQuests is [{QuestId:3458,ConditionState:0}] &&
                    f.Read().CompletedQuests.Contains(3472) && f.Read().CompletedQuests.Contains(3473),
                    "重选并重开仓储恢复story1、原父接取、两子完成及全部碎片，不等同于冷启动或客户端验收");
            var final = f.Finish(ushort.MaxValue,3458);
            That(final.Receipt?.Commit is {ConditionType:44,ExperienceReward.Amount:>0} && Materials(f.Read()) == 5 &&
                f.Read().ActiveMainQuests.Length == 0 && f.Read().CompletedQuests.Contains(3458) && f.Receipts == 3,
                "真实父34只交付父自身经验及声明，不重复领取两子碎片");
        }
        foreach (var scenario in new[] { (Active:Array.Empty<ActiveMainQuest>(),Done:new[] {3457,3472}),
            (Active:new ActiveMainQuest[] {new(0,3458,2),new(1,3472,0)},Done:new[] {3457}) })
        {
            using var f = Make(4,scenario.Active,scenario.Done); var before = f.Read();
            That(f.Change(3473,QuestLifecycleAction.Accept)?.Commit is null && QuestCompletionRepositoryTests.Same(before,f.Read()),
                "真实资源碎片4/3仍不能替代活动父任务或3472正式交付记录");
        }
        using (var f = Make(2,[new(0,3458,1)],[3457,3472]))
        {
            That(f.Change(3473,QuestLifecycleAction.Accept)?.Character.ActiveMainQuests.Any(row => row.QuestId == 3473 && row.ConditionState == 1) == true,
                "真实3473缺料接取scalar1，不伪造足额");
            var before = f.Read();
            That(f.Finish(ushort.MaxValue,3473).Outcome == QuestFinishOutcome.Rejected && QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Receipts == 0,
                "碎片2/3正式交付拒绝，材料、父子和领奖回执零变化");
        }
        foreach (int count in new[] { 2,3,4 })
        {
            // 保留旧存档父1/末子0，原生客户端只交父34，不能靠测试先交3473。
            using var f = Make(count,[new(0,3458,1),new(1,3473,0)],[3457,3472]);
            var before = f.Read();
            var refreshed = QuestInventoryProgress.Refresh(before.ActiveMainQuests,before.Inventory,f.Catalog,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),QuestEligibility.Completed(before));
            That(refreshed.Single(row => row.QuestId == 3458).ConditionState == (count < 3 ? 1u : 0u),
                "父进度按实际末子材料重算，足额归零但不提前标记3473完成");
            if (count == 2)
            {
                That(f.Finish(ushort.MaxValue,3458).Outcome == QuestFinishOutcome.Rejected &&
                    QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Receipts == 0,
                    "旧末子scalar0不能替代实际2/3库存，父34整体拒绝");
                continue;
            }
            if (count == 4)
            {
                f.Sql("CREATE TRIGGER albert_terminal_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'terminal rollback'); END");
                Reject<SqliteException>(() => f.Finish(ushort.MaxValue,3458),"末子扣料和父奖励末段SQL故障");
                That(QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Receipts == 0,
                    "末子扣3发4、父成长金币、两槽删除和两完成位同事务回滚");
                f.Sql("DROP TRIGGER albert_terminal_abort");
            }
            var result = f.Finish(ushort.MaxValue,3458);
            var after = f.Read();
            That(result.Receipt?.Commit is { ConditionType:44,Consumed:[{ItemId:10164800,Count:3}],Rewards:[{GrantedCount:4}] } &&
                after.Revision == before.Revision+1 && after.ActiveMainQuests.Length == 0 && Materials(after) == count+1 &&
                after.CompletedQuests.Contains(3458) && after.CompletedQuests.Contains(3473) && f.Receipts == 1,
                "单个原生父34原子交付末子3材料及4碎片奖励，并仅领取一次父成长金币");
            That(f.Finish(ushort.MaxValue,3458).Outcome == QuestFinishOutcome.AlreadyCompleted &&
                f.Finish(ushort.MaxValue,3473).Outcome == QuestFinishOutcome.AlreadyCompleted &&
                QuestCompletionRepositoryTests.Same(after,f.Read()),"父子迟到重复34都不再扣料或发奖");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(QuestCompletionRepositoryTests.Same(after,f.Session.Selected!),"父合并交付重选恢复两完成位与真实余额");
        }
        using (var f = Make(4,[new(0,3458,1),new(1,3473,0)],[3457,3472],loseResult:true))
        {
            Reject<IOException>(() => f.Finish(ushort.MaxValue,3458),"合并交付提交返回丢失");
            var saved = f.Read();
            That(f.Finish(ushort.MaxValue,3458).Receipt?.Commit is { Duplicate:true,ProjectionCurrent:true } &&
                Materials(saved) == 5 && f.Receipts == 1 && QuestCompletionRepositoryTests.Same(saved,f.Read()),
                "同父接取精确恢复合并回执，不重新扣末子材料或重复发奖");
        }
        using (var f = Make(4,[new(0,3458,1),new(1,3473,0)],[3457]))
        {
            var before = f.Read();
            That(f.Finish(ushort.MaxValue,3458).Outcome == QuestFinishOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(before,f.Read()),"末子活动槽不能绕过猎杀子3472正式完成");
        }
        Console.WriteLine($"PASS {Count-checks} 阿尔伯特父子交付、原生末子合并及保存回放资源断言；不包含客户端验收");

        OrdinaryFixture Make(int count,ActiveMainQuest[] active,int[] done,bool loseResult=false,byte level=56,bool levelTicket=false) => new(towns,characters,skills,growth,parameters,items,seek,
            materialCount:0,definitions:[parent,hunt,seek,quests.Get(3457)],active:active,loseResult:loseResult,configure:candidate =>
            {
                var template = items.Items.Get(10164800);
                var range = ItemSlotBoundService.Range(template.Kind,candidate.Inventory.Expansion);
                short slot = Enumerable.Range(range.Start,range.End-range.Start+1).Select(value => checked((short)value))
                    .First(value => candidate.Inventory.Find(new(InventoryListType.Main,value)) is null);
                if(levelTicket)
                {
                    var ticket=items.Items.Get(490705250);
                    var slots=ItemSlotBoundService.Range(ticket.Kind,candidate.Inventory.Expansion);
                    short target=Enumerable.Range(slots.Start,slots.End-slots.Start+1).Select(v=>(short)v)
                        .First(v=>candidate.Inventory.Find(new(InventoryListType.Main,v)) is null);
                    candidate=candidate with {Inventory=candidate.Inventory with {Items=candidate.Inventory.Items.Add(new(0,
                        new(InventoryListType.Main,target),InventoryRewardGrantService.Create(ticket,1)))}};
                }
                return candidate with {Level=level,AcceptedStoryLevel=1,CompletedQuests=done,
                    InitialLearning=SkillPointLedger.Synchronize(0,0,level,candidate.InitialLearning with
                        {Experience=growth.GetLevelStartExperience(level)},skills),
                    Inventory=count==0 ? candidate.Inventory : candidate.Inventory with {Items=candidate.Inventory.Items.Add(new(0,new(InventoryListType.Main,slot),
                        InventoryRewardGrantService.Create(template,count)))} };
            });
        static int Materials(Character character) => character.Inventory.Items.Where(item =>
            item.Position.List == InventoryListType.Main && item.Core.ItemId == 10164800).Sum(item => item.Core.Count);
    }
}
