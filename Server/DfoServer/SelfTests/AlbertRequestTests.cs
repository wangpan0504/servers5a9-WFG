using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
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
        Console.WriteLine($"PASS {Count-checks} 阿尔伯特GM56→57→58、实际父子交付及保存回放资源断言；不包含完整猎杀或客户端验收");

        OrdinaryFixture Make(int count,ActiveMainQuest[] active,int[] done) => new(towns,characters,skills,growth,parameters,items,seek,
            materialCount:0,definitions:[parent,hunt,seek,quests.Get(3457)],active:active,configure:candidate =>
            {
                var template = items.Items.Get(10164800);
                var range = ItemSlotBoundService.Range(template.Kind,candidate.Inventory.Expansion);
                short slot = Enumerable.Range(range.Start,range.End-range.Start+1).Select(value => checked((short)value))
                    .First(value => candidate.Inventory.Find(new(InventoryListType.Main,value)) is null);
                return candidate with {Level=56,AcceptedStoryLevel=1,CompletedQuests=done,
                    InitialLearning=SkillPointLedger.Synchronize(0,0,56,candidate.InitialLearning with
                        {Experience=growth.GetLevelStartExperience(56)},skills),
                    Inventory=candidate.Inventory with {Items=candidate.Inventory.Items.Add(new(0,new(InventoryListType.Main,slot),
                        InventoryRewardGrantService.Create(template,count)))} };
            });
        static int Materials(Character character) => character.Inventory.Items.Where(item =>
            item.Position.List == InventoryListType.Main && item.Core.ItemId == 10164800).Sum(item => item.Core.Count);
    }
}
