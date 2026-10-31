using DfoServer.Game.Accounts;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ExpertJobGiveUpTests
{
    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-expert-giveup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool passed = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var database = new SqliteGameDatabase(path);
            using var lease = new AccountOwners().Acquire("expert-giveup", () => { });
            var created = repository.Create(lease, new("GiveUpTest", towns.Room, towns.InitialReturn)
            { Level = 50, CompletedQuests = [2702, 11007, 3145], Inventory = new([], Gold: 20000) });
            Sql($"INSERT INTO character_expert_job(character_id,account,job_type) VALUES({created.Id},'expert-giveup',1)");
            Sql($"INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) VALUES({created.Id},0,11019,1,'{Guid.NewGuid():N}')");
            var session = new GameSession(repository, towns, lease);
            session.Select(0); session.MarkReady();
            var supplies = InventoryRewardGrantService.TryGrantBatch(session.Selected!, [new(2600482, 1), new(2600483, 1)],
                resources.Items, resources.GoldLimits, resources.EpicPieces);
            That(supplies.Accepted && supplies.Plan is not null, "准备EXJ指定的基础工具与不清理的高级工具");
            repository.CommitInventory(lease, session.Selected!, supplies.Plan!);
            session.ReturnToRoster(); session.Select(0); session.MarkReady();
            var before = session.Selected!;
            That(ExpertJobCraft.TryPlanGiveUp(before, resources.ExpertJobs, out var oldWallet, out int oldState, out _),
                "真实资格可规划放弃");
            Sql($"UPDATE character_expert_job SET expertness=1 WHERE character_id={before.Id}");
            Reject<InvalidOperationException>(() => repository.CommitExpertJobGiveUp(lease, before, oldWallet, oldState,
                before.Learning, resources.ExpertJobs.ClearQuests(1)), "资产版本相同但资格变化时拒绝旧放弃计划");
            var staleRollback = repository.LoadCharacter(lease, 0);
            That(staleRollback.Revision == before.Revision && staleRollback.Inventory.Gold == before.Inventory.Gold &&
                staleRollback.Inventory.Items.SequenceEqual(before.Inventory.Items) && staleRollback.ExpertJobExperience == 1 &&
                staleRollback.CompletedQuests.SequenceEqual(before.CompletedQuests), "旧资格放弃整笔回滚费用、工具与清链");
            Sql($"UPDATE character_expert_job SET expertness=0 WHERE character_id={before.Id}");
            Sql("CREATE TRIGGER fail_giveup BEFORE DELETE ON character_completed_quests BEGIN SELECT RAISE(ABORT,'giveup transaction failure'); END");
            Reject<SqliteException>(() => session.GiveUpExpertJob(resources.ExpertJobs), "清任务末段失败整体回滚");
            var rollback = repository.LoadCharacter(lease, 0);
            That(rollback.Revision == before.Revision && rollback.Inventory.Gold == before.Inventory.Gold && rollback.ExpertJobType == 1 &&
                rollback.Inventory.Items.SequenceEqual(before.Inventory.Items) && rollback.CompletedQuests.SequenceEqual(before.CompletedQuests) &&
                rollback.ActiveMainQuests.SequenceEqual(before.ActiveMainQuests), "清链失败保留费用、工具、资格、版本及完整任务");
            Sql("DROP TRIGGER fail_giveup");
            var result = session.GiveUpExpertJob(resources.ExpertJobs);
            That(result.Error == 0 && session.Selected!.CompletedQuests.SequenceEqual(new[] { 3145 }) &&
                session.Selected.ActiveMainQuests.Length == 0,
                "放弃只清理EXJ声明的副职业活动及完成任务，保留普通主线");
            That(session.Selected!.Inventory.Items.All(item => item.Core.ItemId != 2600482) &&
                session.Selected.Inventory.Items.Any(item => item.Core.ItemId == 2600483), "只移除资源指定的初级工具，保留高级工具");
            session.ReturnToRoster(); session.Select(0); session.MarkReady();
            That(session.Selected!.ExpertJobType == 0 && session.Selected.CompletedQuests.SequenceEqual(new[] { 3145 }) &&
                session.Selected.ActiveMainQuests.Length == 0 && session.Selected.Inventory.Gold == 19000,
                "重登保留放弃费用、清链结果与普通主线历史");
            passed = true;
            void Sql(string sql) { using var db = database.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        }
        finally { SqliteConnection.ClearAllPools(); if (passed) Directory.Delete(root, true); else Console.WriteLine("FAILED fixture retained: " + root); }
        Console.WriteLine($"PASS {Count - checks} 副职业放弃清链与重登断言");
    }
}
