using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Pvp;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Pvp;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class PvpTrainingManualTests
{
    public static void Run(TownCatalog towns,CharacterCatalog characters,ItemResources items,
        SkillLearningCatalog learning,CharacterGrowthCatalog growth,QuestRewardParameterCatalog parameters,QuestCatalog quests)
    {
        int before=Count;var quest=quests.Get(2674);
        That(PvpTrainingProgress.Manual(quest),"2674真实原声明识别实体秘籍链");
        foreach(string field in new[]{"sub reward","substitutive names"})
            That(!PvpTrainingProgress.Manual(quest with {Definition=quest.Definition! with
                {Fields=quest.Definition.Fields.Where(row=>row.Name!=field).ToImmutableArray()}}),"缺失原目标或秘籍字段不能泛化放行");
        That(!PvpTrainingProgress.Manual(quest with {Id=2678}) && !PvpTrainingProgress.Manual(quest with {RewardType="[item]"}),
            "其它任务及奖励类型不能借用2674合同");
        using(var f=Create(262144,false))
        {
            var original=f.Read();var active=original.ActiveMainQuests.Single();
            var run=new PvpTrainingRun(Guid.NewGuid(),new(original,active,new PvpArcadeMode(80,0,2,99,[1201])),1,1)
                {Round=new(2,1202,false,true)};
            var plan=QuestLifecyclePlan.ObtainPvpTrainingManual(original,quest,run,items.Items,items.GoldLimits,items.EpicPieces)
                ?? throw new InvalidOperationException("原资源不能规划秘籍");
            That(plan.ActiveAfter.Single().ConditionState==0 &&
                plan.Inventory.After.Items.Single(item=>item.Core.ItemId==10158452).Core.Count==1,"秘籍与原通道一起冻结规划");
            That(QuestLifecyclePlan.ObtainPvpTrainingManual(original,quest,run with {Round=PvpArcadeRoundState.Initial},items.Items,items.GoldLimits,items.EpicPieces) is null,
                "没有全部胜利的原实例不能规划秘籍");
            var template=items.Items.Get(10158452);var range=ItemSlotBoundService.Range(template.Kind,original.Inventory.Expansion);
            var full=original with {Inventory=original.Inventory with {Items=original.Inventory.Items.Where(item=>
                item.Position.List!=InventoryListType.Main || item.Position.Index<range.Start || item.Position.Index>range.End).Concat(
                Enumerable.Range(range.Start,range.End-range.Start+1).Select(slot=>new InventoryItem(0,new(InventoryListType.Main,(short)slot),
                    InventoryRewardGrantService.Create(template,template.StackLimit)))).ToImmutableArray()}};
            That(QuestLifecyclePlan.ObtainPvpTrainingManual(full,quest,run with {Entry=run.Entry with {Expected=full}},items.Items,items.GoldLimits,items.EpicPieces) is null,
                "材料栏全满时不清第三通道，原胜利仍可在腾出空间后重试");
            f.Sql("CREATE TRIGGER manual_abort AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'manual rollback'); END");
            DfoServer.Testing.TestCheck.Reject<SqliteException>(()=>f.Repository.CommitQuestLifecycle(f.Owner,plan),"秘籍事务末段故障");
            That(Same(original,f.Read()),"秘籍和任务通道全部回滚");f.Sql("DROP TRIGGER manual_abort");
            var committed=f.Repository.CommitQuestLifecycle(f.Owner,plan);var saved=f.Read();
            That(saved.ActiveMainQuests.Single().ConditionState==0 && saved.Inventory.Items.Single(item=>item.Core.ItemId==10158452).Core.Count==1,
                "重试冻结规划只发一本秘籍");
            That(f.Repository.CommitQuestLifecycle(f.Owner,plan).Duplicate && Same(saved,f.Read()),"秘籍结果丢失重放不重复发物品");
        }
        using(var f=Create(0,false))
            That(f.Finish(ushort.MaxValue,2674).Outcome!=QuestFinishOutcome.Committed,"只有零通道但缺少实体秘籍不能领奖");
        foreach(bool lose in new[]{false,true})
        using(var f=Create(0,true,lose))
        {
            var original=f.Read();
            if(!lose)
            {
                f.Sql("CREATE TRIGGER manual_finish_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'manual finish rollback'); END");
                DfoServer.Testing.TestCheck.Reject<SqliteException>(()=>f.Finish(ushort.MaxValue,2674),"秘籍领奖末段故障");
                That(Same(original,f.Read()),"消耗秘籍、TP和完成位全部回滚");f.Sql("DROP TRIGGER manual_finish_abort");
            }
            else DfoServer.Testing.TestCheck.Reject<IOException>(()=>f.Finish(ushort.MaxValue,2674),"秘籍领奖提交后结果丢失");
            var result=f.Finish(ushort.MaxValue,2674);var saved=f.Read();
            That(result.Outcome==QuestFinishOutcome.Committed && saved.Learning.BonusTp==original.Learning.BonusTp+2 &&
                saved.Learning.Trees.Zip(original.Learning.Trees).All(pair=>pair.First.Tp==pair.Second.Tp+2) &&
                !saved.Inventory.Items.Any(item=>item.Core.ItemId==10158452),"只消耗一本秘籍且原双树各增加2TP");
            _=f.Finish(ushort.MaxValue,2674);That(Same(saved,f.Read()) && f.Receipts==1,"重登读库与重复交付不重新发TP或秘籍");
        }
        Console.WriteLine($"PASS {Count-before} 决斗秘籍原子发放、消耗、TP与重放断言");
        QuestOrdinaryCompletionTests.Fixture Create(uint scalar,bool book,bool lose=false)=>new(towns,characters,learning,growth,parameters,items,quest,
            materialCount:0,loseResult:lose,definitions:[quest,quests.Get(2673)],active:[new(0,2674,scalar)],resumeTown:true,configure:seed=>seed with
            {
                Level=60,AcceptedStoryLevel=60,CompletedQuests=[2673],Position=towns.Areas[(10,4)].Gate ?? new Location(10,4,200,200),
                InitialLearning=SkillPointLedger.Synchronize(0,0,60,seed.InitialLearning with {Experience=growth.GetLevelStartExperience(60)},learning),
                Inventory=seed.Inventory with {Items=book ? seed.Inventory.Items.Add(new(0,new(InventoryListType.Main,ItemSlotBoundService.Range(items.Items.Get(10158452).Kind,seed.Inventory.Expansion).Start),
                    InventoryRewardGrantService.Create(items.Items.Get(10158452)))) : seed.Inventory.Items}
            });
    }
}
