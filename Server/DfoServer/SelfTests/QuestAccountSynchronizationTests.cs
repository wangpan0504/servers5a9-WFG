using System.Collections.Immutable;
using DfoServer.Infrastructure;
using DfoServer.Game.Skills;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestAccountSynchronizationTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items, SkillLearningCatalog learning,
        CharacterGrowthCatalog growth, QuestRewardParameterCatalog parameters, QuestCatalog quests)
    {
        int before = Count;
        var first = quests.Get(8709); var next = quests.Get(8710);
        That(quests.All.Where(QuestAccountPolicy.PaidSynchronization).Select(q => q.Id).Order().SequenceEqual(new[] { 8709, 8710 }),
            "新版PVF带奖账号会面只覆盖两项，不开放活动或无奖励marker");
        foreach (var quest in new[] { first, next })
        {
            That(!quest.Definition!.AccountQuest && QuestAccountPolicy.SharedCompletion(quest) && QuestCoverage.HasOrdinaryDeclarations(quest),
                "保留原声明，但合法同步会面采用账号一次交付");
            That(!QuestAccountPolicy.PaidSynchronization(quest with { Grade = "[event]" }) &&
                !QuestAccountPolicy.PaidSynchronization(quest with { Condition = "[custom multi condition]" }) &&
                !QuestAccountPolicy.PaidSynchronization(quest with { Definition = quest.Definition with
                { Fields = quest.Definition.Fields.Select(field => field.Name == "synchronization account quest"
                    ? field with { Values = [new(0, 2, null)] } : field).ToImmutableArray() } }),
                "活动、未实现玩法及未知同步枚举不能借账号策略开放");
        }
        NewCharacter Candidate(string name) => At50(CharacterCreation.Prepare(name, characters.Get(0), towns.Room, towns.InitialReturn, learning.Get));
        NewCharacter At50(NewCharacter source) => source with { Level = 50,
            InitialLearning = SkillPointLedger.Synchronize(source.Job, source.GrowType, 50,
                source.InitialLearning with { Experience = growth.GetLevelStartExperience(50) }, learning) };
        using (var f = new QuestOrdinaryCompletionTests.Fixture(towns, characters, learning, growth, parameters, items, first,
            materialCount: 0, definitions: [first, next], active: [new(0, 8709, 0), new(1, 8710, 0)], configure: At50))
        {
            var alternate = f.Repository.Create(f.Owner, Candidate("SyncAlt") with { ActiveMainQuests = [new(0, 8709, 0), new(1, 8710, 0)] });
            var original = f.Read();
            f.Sql("CREATE TRIGGER sync_abort AFTER INSERT ON account_completed_quests BEGIN SELECT RAISE(ABORT,'sync rollback'); END");
            DfoServer.Testing.TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue, 8709), "账号同步写入失败必须回滚原奖励");
            That(Same(original, f.Read()) && Same(alternate, f.Repository.LoadCharacter(f.Owner, 1)) && f.Receipts == 0,
                "金币、经验、原接取、其它角色槽及revision一起回滚");
            f.Sql("DROP TRIGGER sync_abort");
            var firstResult = f.Finish(ushort.MaxValue, 8709);
            That(firstResult.Outcome == QuestFinishOutcome.Committed && firstResult.Receipt!.Commit.GoldAmount == 100 &&
                f.Read().Inventory.Gold == original.Inventory.Gold + 100 && f.Read().AccountCompletedQuests.SequenceEqual([8709]),
                "首个8709合法完成者得到原100金币并建立此任务账号完成");
            var other = f.Repository.LoadCharacter(f.Owner, 1);
            That(other.Inventory.Gold == alternate.Inventory.Gold && other.Learning.Experience == alternate.Learning.Experience &&
                other.AccountCompletedQuests.SequenceEqual([8709]) && other.ActiveMainQuests.Select(row => row.QuestId).SequenceEqual(new ushort[] { 8710 }),
                "其它角色只同步完成并移除同任务槽，不误删下一项或发经验金币");
            f.Session.ReturnToRoster(); f.Session.Select(1); f.Session.MarkReady();
            That(f.Finish(ushort.MaxValue, 8709).Outcome == QuestFinishOutcome.AlreadyCompleted &&
                f.Change(8709, QuestLifecycleAction.Accept)!.Commit is null, "重登换角色不能重领或重接同任务");
            var secondResult = f.Finish(ushort.MaxValue, 8710);
            var second = f.Repository.LoadCharacter(f.Owner, 1);
            That(secondResult.Outcome == QuestFinishOutcome.Committed && secondResult.Receipt!.Commit.GoldAmount == 100 &&
                second.Inventory.Gold == alternate.Inventory.Gold + 100 && second.AccountCompletedQuests.SequenceEqual([8709, 8710]),
                "8710独立计账，可由另一个角色成为该任务首领者，不是全账号所有任务共领一次");
            That(f.Read().Inventory.Gold == original.Inventory.Gold + 100 && f.Read().ActiveMainQuests.Length == 0 && f.Receipts == 2,
                "两项各发一次并原子清除其它角色对应活动槽");
            f.Session.ReturnToRoster();
            f.Repository.Delete(f.Owner, f.Repository.LoadDeletionTarget(f.Owner, 0));
            var recreated = f.Repository.Create(f.Owner, Candidate("SyncNew"));
            That(recreated.AccountCompletedQuests.SequenceEqual([8709, 8710]) &&
                !QuestEligibility.Evaluate(recreated, first, f.Catalog).Eligible && !QuestEligibility.Evaluate(recreated, next, f.Catalog).Eligible,
                "删除领取角色再建角色也不清账号完成及领奖限制");
        }
        using (var f = new QuestOrdinaryCompletionTests.Fixture(towns, characters, learning, growth, parameters, items, first,
            materialCount: 0, active: [new(0, 8709, 0)], configure: At50))
        {
            var other = f.Repository.Create(f.Owner, Candidate("SyncRace") with { ActiveMainQuests = [new(0, 8709, 0)] });
            var source = f.Read();
            QuestCompletionPlan Plan(Character character) => QuestCompletionPlan.Prepare(character, first, ushort.MaxValue, f.Catalog,
                parameters, items.Items, items.GoldLimits, items.EpicPieces, growth, DateTimeOffset.UtcNow).Plan
                ?? throw new InvalidOperationException("同步会面规划不应被拒绝");
            var plans = new[] { Plan(source), Plan(other) };
            var results = Task.WhenAll(plans.Select(plan => Task.Run(() =>
            {
                try { f.Repository.CommitQuestCompletion(f.Owner, plan); return true; }
                catch (InvalidOperationException) { return false; }
            }))).GetAwaiter().GetResult();
            var saved = new[] { f.Repository.LoadCharacter(f.Owner, 0), f.Repository.LoadCharacter(f.Owner, 1) };
            That(results.Count(result => result) == 1 && f.Receipts == 1 && saved.Sum(c => (long)c.Inventory.Gold) ==
                (long)source.Inventory.Gold + other.Inventory.Gold + 100 && saved.All(c => c.AccountCompletedQuests.SequenceEqual([8709])),
                "同账号两个角色并发旧规划只有一笔发奖，其余按实际共享快照拒绝");
        }
        Console.WriteLine($"PASS {Count-before} 真实每周任务账号一次奖励、并发、回滚、跨角色及删角色断言");
    }
}
