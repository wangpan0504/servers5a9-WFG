using static DfoServer.Testing.TestCheck;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static class RepositoryBoundaryTests
{
    public static void Run()
    {
        int checks = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-repository-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("boundary", () => { });
            var location = new Location(1, 0, 10, 10);
            var original = repository.Create(owner, new("Boundary", location, location));
            var planned = original.Inventory with { Items = [new(0, new(InventoryListType.Main, 121), new(1001, ItemKind.Material, 3))] };
            var plan = new InventoryPlan(original.Inventory, planned, "synthetic_grant");
            var committed = repository.CommitInventory(owner, original, plan);
            TestCheck.That(committed.Revision == original.Revision + 1 && committed.Inventory.Items.Single().Uid > 0 && planned.Items.Single().Uid == 0,
                "合成资产提交递增版本并返回持久 UID");
            Reject<InvalidOperationException>(() => repository.CommitInventory(owner, original, plan), "旧 revision 不可重复提交");
            Reject<InvalidOperationException>(() => repository.Save(owner, original, location with { X = 11 }, location), "位置保存拒绝旧 revision");
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_asset_audit BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'audit failure'); END";
                command.ExecuteNonQuery();
                var item = committed.Inventory.Items.Single();
                var after = committed.Inventory with { Items = [item with { Core = item.Core with { Value = 4 } }], Gold = 1 };
                Reject<SqliteException>(() => repository.CommitInventory(owner, committed, new(committed.Inventory, after, "synthetic_fault")),
                    "审计失败使资产与金币同事务回滚");
            }
            string expected = JsonSerializer.Serialize(committed);
            TestCheck.That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == expected, "失败后重开库恢复原资产和版本");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => repository.Save(owner, committed, location, location), "旧 generation 无法保存");
            owner.Dispose();
            TestCheck.That(JsonSerializer.Serialize(repository.List(replacement).Single()) == expected, "旧 owner 释放不影响新会话");
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
        Console.WriteLine($"PASS {TestCheck.Count - checks} resource-free repository boundary assertions");
    }

}
