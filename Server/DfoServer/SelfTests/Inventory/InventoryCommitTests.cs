using DfoServer.Infrastructure;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class InventoryCommitTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-inventory-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int before = TestCheck.Count;
        try
        {
            foreach (string field in new[] { "level", "job", "grow", "skill" })
                ChangedQualification(field);
            PreserveProgress();
            SeparateOwners();
        }
        finally
        {
            TestFiles.DeleteDirectory(root, recursive: true);
        }
        Console.WriteLine($"PASS {TestCheck.Count - before} 库存资格快照、同revision进度变更及提交恢复断言");

        void ChangedQualification(string field)
        {
            string path = Path.Combine(root, field + ".db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("commit", () => { });
            var character = Create(repository, owner);
            var request = Request(character);
            var plan = InventoryMoveService.Plan(character, request, itemResources.Items, 0).Plan!;
            switch (field)
            {
                case "level":
                    repository.UpdateProgress(owner, new(CharacterProgressField.Level, owner.Account, character.Name, 1, 0));
                    break;
                case "job":
                    Sql(path, "UPDATE characters SET job=1");
                    break;
                case "grow":
                    repository.UpdateProgress(owner, new(CharacterProgressField.Grow, owner.Account, character.Name, 1, 0));
                    break;
                case "skill":
                    repository.UpdateProgress(owner, new(CharacterProgressField.Skill, owner.Account, character.Name, 5, 0));
                    // 调用方原数组也被改写时，不能污染计划保存的前提。
                    character.Skills[0] = new(5, 0);
                    break;
            }
            var current = repository.List(owner).Single();
            long audit = Scalar(path, "SELECT count(*) FROM inventory_audit_log");
            Check(current.Revision == character.Revision, field + " fixture确实未改revision");
            Reject(() => repository.CommitInventory(owner, character, plan), field + "变化后拒绝旧计划");
            Reject(() => repository.CommitInventory(owner, current, plan), field + "刷新expected也不能重新绑定旧计划");
            Check(JsonSerializer.Serialize(repository.List(owner).Single()) == JsonSerializer.Serialize(current) &&
                Scalar(path, "SELECT count(*) FROM inventory_audit_log") == audit, field + "拒绝不改库存、进度、revision或审计");
            // 重新读取并重新规划可用操作，不能仅更换expected后重交旧结果。
            var fresh = InventoryMoveService.Plan(current, new(new(InventoryListType.Main, 9), current.Inventory.Items[0].Core.ItemId, 1,
                new(InventoryListType.Main, 10), 0, 0, 0, 0, 0), itemResources.Items, 0);
            Check(fresh.Accepted, field + "刷新后合法背包移动可重新规划");
            var committed = repository.CommitInventory(owner, current, fresh.Plan!);
            Check(committed.Revision == current.Revision + 1 && committed.Inventory.Items[0].Position.Index == 10 &&
                JsonSerializer.Serialize(committed) == JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()),
                field + "新计划提交及重开恢复一致");
        }

        void PreserveProgress()
        {
            string path = Path.Combine(root, "progress.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("commit", () => { });
            var character = Create(repository, owner);
            var plan = InventoryMoveService.Plan(character, Request(character), itemResources.Items, 0).Plan!;
            repository.UpdateProgress(owner, new(CharacterProgressField.Story, owner.Account, character.Name, 7, 0));
            repository.UpdateProgress(owner, new(CharacterProgressField.QuestAdd, owner.Account, character.Name, 3145, 0));
            var committed = repository.CommitInventory(owner, character, plan);
            Check(committed.AcceptedStoryLevel == 7 && committed.CompletedQuests.Contains(3145),
                "无关进度允许库存提交，返回最新进度而非旧expected");
            Check(committed.Inventory.Items[0].Position.List == InventoryListType.Equipment &&
                JsonSerializer.Serialize(committed) == JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()),
                "发布快照与重开后的持久状态一致");
        }

        void SeparateOwners()
        {
            string path = Path.Combine(root, "owners.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("read-owners", () => { });
            using var other = new AccountOwners().Acquire("read-other", () => { });
            var first = Create(repository, owner, "ReadFirst");
            var second = Create(repository, owner, "ReadSecond");
            var third = Create(repository, other, "ReadOther");
            var reopened = new CharacterRepository(path);
            var own = reopened.List(owner);
            Check(own.Length == 2 && own.Single(row => row.Id == first.Id).Inventory.Items.Single() == first.Inventory.Items.Single() &&
                own.Single(row => row.Id == second.Id).Inventory.Items.Single() == second.Inventory.Items.Single() &&
                reopened.List(other).Single().Inventory.Items.Single() == third.Inventory.Items.Single(),
                "完整角色冷读只取各自归属库存，同账号其他角色及其他账号不串入UID或Core");
        }

        Character Create(CharacterRepository repository, AccountOwners.Lease owner, string name = "CommitCase")
        {
            var weapon = characters.Get(0).Equipment.Single(item => item.Slot == 12).Template;
            return repository.Create(owner, new(name, towns.Room, towns.InitialReturn)
            {
                Level = 95, Skills = [new(5, 1)],
                Inventory = new([new(0, new(InventoryListType.Main, 9), InventoryRewardGrantService.Create(weapon))])
            });
        }
        static InventoryMoveRequest Request(Character character) => new(new(InventoryListType.Main, 9),
            character.Inventory.Items[0].Core.ItemId, 1, new(InventoryListType.Equipment, 12), 0, 0, 0, 0, 0);
    }

    static void Check(bool value, string message) => TestCheck.That(value, message);
    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Check(rejected, message);
    }
    static long Scalar(string path, string sql)
    {
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }
    static void Sql(string path, string sql)
    {
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
