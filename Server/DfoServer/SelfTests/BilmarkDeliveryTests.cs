using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using OrdinaryFixture = DfoServer.QuestOrdinaryCompletionTests.Fixture;

namespace DfoServer;

internal static class BilmarkDeliveryTests
{
    public static void Run(TownCatalog towns,CharacterCatalog characters,ItemResources items,QuestCatalog quests,
        SkillLearningCatalog skills,CharacterGrowthCatalog growth,QuestRewardParameterCatalog parameters)
    {
        int checks = Count;
        var parent = quests.Get(3656); var child = quests.Get(3693);
        That(QuestRelationProgress.TerminalSeeking(parent,quests)?.Id == 3693 &&
            QuestRelationProgress.ZeroRewardChild(child) && child.IntData.SequenceEqual([10164767,1]),
            "真实比尔马克末子使用小盒子及严格0/0零奖励占位");
        foreach (var unsupported in new[]
        {
            child with {Rewards=[]}, child with {Rewards=[new(0,0,null),new(0,1,null)]},
            child with {Rewards=[new(0,10164767,null),new(0,0,null)]},
            child with {DeclaredGoldMultiple=1}, child with {Attributes=[]},
            child with {RewardType=null}
        })
        {
            var catalog = new QuestCatalog(new[] {parent,quests.Get(3692),unsupported}.ToDictionary(row => row.Id));
            That(QuestRelationProgress.TerminalSeeking(parent,catalog) is null,
                "空声明、非零金币、畸形物品或缺少禁经验标志不能借零奖励末子放宽交付");
        }
        foreach (int count in new[] {0,1,2})
        {
            using var f = Make(count);
            var before = f.Read();
            var refreshed = QuestInventoryProgress.Refresh(before.ActiveMainQuests,before.Inventory,f.Catalog,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),QuestEligibility.Completed(before));
            That(refreshed.Single(row => row.QuestId == 3656).ConditionState == (count == 0 ? 1u : 0u),
                "父进度随实际小盒子库存归零，末子仍未归档");
            if (count == 0)
            {
                That(f.Finish(ushort.MaxValue,3656).Outcome == QuestFinishOutcome.Rejected &&
                    QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Receipts == 0,
                    "旧父1子0不能绕过缺少小盒子，整笔交付拒绝");
                continue;
            }
            f.Sql("CREATE TRIGGER bilmark_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'bilmark rollback'); END");
            Reject<SqliteException>(() => f.Finish(ushort.MaxValue,3656),"零奖励末子合并父交付末段SQL失败");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()) && f.Receipts == 0,
                "小盒子、父装备金币经验、父子槽及完成记录同事务回滚");
            f.Sql("DROP TRIGGER bilmark_abort");
            var result = f.Finish(ushort.MaxValue,3656);
            var after = f.Read();
            var rewards = QuestFixedRewardDefinition.Parse(parent,true).ForJob(before.Job,before.GrowType);
            That(result.Receipt?.Commit is {Consumed:[{ItemId:10164767,Count:1}],ConditionType:44} commit &&
                commit.Rewards.Select(row => (row.Item.Core.ItemId,row.GrantedCount)).SequenceEqual(rewards.Select(row => (row.ItemId,row.Count))) &&
                after.Revision == before.Revision+1 && after.ActiveMainQuests.Length == 0 && Materials(after) == count-1 &&
                after.CompletedQuests.Contains(3656) && after.CompletedQuests.Contains(3693) && f.Receipts == 1,
                "单个父34扣一个小盒子，只发父真实职业奖励并同次归档父子，不把0/0解析为物品或金币");
            That(f.Finish(ushort.MaxValue,3656).Outcome == QuestFinishOutcome.AlreadyCompleted &&
                f.Finish(ushort.MaxValue,3693).Outcome == QuestFinishOutcome.AlreadyCompleted &&
                QuestCompletionRepositoryTests.Same(after,f.Read()),"重复父子34不再次消费或发奖");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(QuestCompletionRepositoryTests.Same(after,f.Session.Selected!),"重选恢复父子完成位、装备及剩余小盒子");
        }
        using (var f = Make(1))
        {
            That(f.Finish(ushort.MaxValue,3693).Receipt?.Commit is {Consumed:[{ItemId:10164767,Count:1}],Rewards.Length:0} &&
                f.Read().ActiveMainQuests is [{QuestId:3656,ConditionState:0}] && Materials(f.Read()) == 0,
                "显式子34仍只消耗小盒子并归档子任务，不发零占位奖励");
            That(f.Finish(ushort.MaxValue,3656).Receipt is not null && f.Receipts == 2 && Materials(f.Read()) == 0,
                "已有先子后父交付路径保留，父奖励不重复消费小盒子");
        }
        using (var f = Make(1,loseResult:true))
        {
            Reject<IOException>(() => f.Finish(ushort.MaxValue,3656),"比尔马克父交付返回丢失");
            var saved = f.Read();
            That(f.Finish(ushort.MaxValue,3656).Receipt?.Commit is {Duplicate:true,ProjectionCurrent:true} &&
                Materials(saved) == 0 && f.Receipts == 1 && QuestCompletionRepositoryTests.Same(saved,f.Read()),
                "恢复原接取合并回执不重复消耗零奖励末子材料");
        }
        using (var f = Make(1,predecessor:false))
        {
            var before = f.Read();
            That(f.Finish(ushort.MaxValue,3656).Outcome == QuestFinishOutcome.Rejected && QuestCompletionRepositoryTests.Same(before,f.Read()),
                "小盒子及末子槽不能绕过3692真实完成记录");
        }
        Console.WriteLine($"PASS {Count-checks} 比尔马克零奖励末子合并交付与回滚断言；不包含客户端验收");

        OrdinaryFixture Make(int count,bool loseResult=false,bool predecessor=true) => new(towns,characters,skills,growth,parameters,items,child,
            materialCount:0,definitions:[parent,quests.Get(3692),child,quests.Get(3655)],
            active:[new(0,3656,1),new(1,3693,0)],loseResult:loseResult,configure:candidate =>
            {
                var inventory = candidate.Inventory;
                if (count > 0)
                {
                    var template = items.Items.Get(10164767);
                    var range = ItemSlotBoundService.Range(template.Kind,inventory.Expansion);
                    short slot = Enumerable.Range(range.Start,range.End-range.Start+1).Select(value => checked((short)value))
                        .First(value => inventory.Find(new(InventoryListType.Main,value)) is null);
                    inventory = inventory with {Items=inventory.Items.Add(new(0,new(InventoryListType.Main,slot),InventoryRewardGrantService.Create(template,count)))};
                }
                return candidate with {Level=79,CompletedQuests=predecessor ? [3655,3692] : [3655],Inventory=inventory,
                    InitialLearning=SkillPointLedger.Synchronize(0,0,79,candidate.InitialLearning with
                        {Experience=growth.GetLevelStartExperience(79)},skills)};
            });
        static int Materials(Character character) => character.Inventory.Items.Where(row => row.Core.ItemId == 10164767).Sum(row => row.Core.Count);
    }
}
