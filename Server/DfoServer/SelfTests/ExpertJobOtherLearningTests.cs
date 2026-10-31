using DfoServer.Game.Accounts;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ExpertJobOtherLearningTests
{
    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var scripts = resources.ExpertJobs;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-expert-other-learning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool passed = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var database = new SqliteGameDatabase(path);
            using var owner = new AccountOwners().Acquire("other-learning", () => { });
            foreach (byte job in new byte[] { 2, 4 })
            {
                int basic = job == 2 ? 2600149 : 2600083;
                That(scripts.LearnedRecipes(job, 1, []).SequenceEqual(new[] { basic }),
                    "炼金/控偶只保留EXJ声明的基础自动配方");
                int id = scripts.UnlockedRecipes(job, 1).First(id => id != basic && scripts.TryLearningRecipe(id, out _));
                var character = repository.Create(owner, new("LearnOther" + job, towns.Room, towns.InitialReturn) { Level = 95 });
                Sql($"INSERT INTO character_expert_job(character_id,account,job_type) VALUES({character.Id},'other-learning',{job})");
                character = repository.LoadCharacter(owner, character.Slot);
                That(!scripts.IsLearned(job, 1, character.ExpertJobRecipes, id), "达到等级不能免费解锁全部图纸");
                var supplies = InventoryRewardGrantService.TryGrantBatch(character, [new(id, 1)], resources.Items,
                    resources.GoldLimits, resources.EpicPieces);
                That(supplies.Accepted && supplies.Plan is not null, "准备真实副职业图纸");
                character = repository.CommitInventory(owner, character, supplies.Plan!);
                var source = character.Inventory.Items.Single(item => item.Core.ItemId == id);
                var service = new ExpertJobLearning(scripts, resources.Items);
                That(service.Plan(character with { ExpertJobType = 1 }, source.Position, id) is null,
                    "炼金/控偶图纸不允许附魔师学习");
                var plan = service.Plan(character, source.Position, id)!;
                That(plan is not null && plan.Inventory.After.Find(source.Position) is null, "图纸学习消费真实源实例");
                Sql("CREATE TRIGGER fail_other_learning BEFORE UPDATE OF learned_recipes ON character_expert_job BEGIN SELECT RAISE(ABORT,'other learning failure'); END");
                Reject<SqliteException>(() => repository.CommitExpertJobLearning(owner, plan!), "学习末段SQL失败回滚扣图");
                var unchanged = repository.LoadCharacter(owner, character.Slot);
                That(unchanged.Inventory.Find(source.Position) == source && unchanged.Revision == character.Revision && unchanged.ExpertJobRecipes.IsEmpty,
                    "失败保持图纸UID、数量、版本和资格");
                Sql("DROP TRIGGER fail_other_learning");
                repository.CommitExpertJobLearning(owner, plan!);
                var saved = new CharacterRepository(path).LoadCharacter(owner, character.Slot);
                That(saved.Inventory.Find(source.Position) is null && saved.ExpertJobRecipes.SetEquals([id]) &&
                    scripts.LearnedRecipes(job, 1, saved.ExpertJobRecipes).Order().SequenceEqual(new[] { basic, id }.Order()),
                    "重开库恢复图纸资格且205只投影基础和已学图纸");
                Reject<InvalidOperationException>(() => repository.CommitExpertJobLearning(owner, plan!), "旧学习计划不能重放");
            }
            passed = true;
            void Sql(string sql)
            {
                using var db = database.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (passed) Directory.Delete(root, true);
            else Console.WriteLine("FAILED fixture retained: " + root);
        }
        Console.WriteLine($"PASS {Count - checks} 炼金与控偶图纸学习、回滚和重开断言");
    }
}
