using System.Diagnostics;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Items;
using DfoServer.Network;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static class AccountDatabaseConcurrencyTests
{
    public static async Task RunAsync()
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-account-locks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var first = owners.Acquire("writer", () => { });
            using var other = owners.Acquire("reader", () => { });
            var location = new Location(1, 0, 10, 10);
            NewCharacter Role(string name) => new(name, location, location)
            {
                Inventory = new([new(0, new(InventoryListType.Main, 121), new(1001, ItemKind.Material, 3))], Gold: 100)
            };
            Character current = repository.Create(first, Role("Writer"));
            Character healthy = repository.Create(other, Role("Reader"));
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            await using var sender = new GameConnectionSender(other, stream, stop, 0, (_, _, _, _, _) => { });
            using var external = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            external.Open();
            foreach (int delay in new[] { 100, 250, 1000 })
            {
                using var writer = external.BeginTransaction(deferred: false);
                using var entered = new ManualResetEventSlim();
                var saving = Task.Run(() => first.Execute(() =>
                {
                    entered.Set();
                    return repository.Save(first, current, current.Position with { X = (ushort)(current.Position.X + 1) }, current.Return);
                }));
                TestCheck.That(entered.Wait(TimeSpan.FromSeconds(5)), "阻塞写入已持有该账号锁");
                var watch = Stopwatch.StartNew();
                try
                {
                    await Task.Run(() =>
                    {
                        TestCheck.That(JsonSerializer.Serialize(repository.LoadCharacter(other, healthy.Slot)) == JsonSerializer.Serialize(healthy), "另一账号读取已提交快照");
                        TestCheck.That(repository.ListRoster(other).Single().Id == healthy.Id, "另一账号名册不申请writer");
                    }).WaitAsync(TimeSpan.FromSeconds(2));
                    await sender.Send(1, 3, [1], "unrelated-reader").WaitAsync(TimeSpan.FromSeconds(2));
                    TestCheck.That(!saving.IsCompleted && first.IsCurrent && !stop.IsCancellationRequested,
                        "writer等待期间健康连接完成回复，原写入未提前报告成功");
                    watch.Stop();
                    await Task.Delay(delay);
                }
                finally { writer.Rollback(); }
                current = await saving.WaitAsync(TimeSpan.FromSeconds(6));
                TestCheck.That(repository.LoadCharacter(first, current.Slot).Position == current.Position, "writer释放后原操作正常提交");
                Console.WriteLine($"账号SQL竞争注入{delay}ms：另一账号读取+构帧发送{watch.Elapsed.TotalMilliseconds:F2}ms");
            }

            string original = JsonSerializer.Serialize(current);
            using (var command = external.CreateCommand())
            {
                command.CommandText = $"CREATE TRIGGER fail_first_audit BEFORE INSERT ON inventory_audit_log WHEN NEW.character_id={current.Id} BEGIN SELECT RAISE(ABORT,'account audit failure'); END";
                command.ExecuteNonQuery();
            }
            var failure = Task.Run(() =>
            {
                try
                {
                    repository.CommitInventory(first, current, new(current.Inventory,
                        current.Inventory with { Gold = 1, Items = current.Inventory.Items.RemoveAt(0) }, "failed-concurrent"));
                    return false;
                }
                catch (SqliteException) { return true; }
            });
            var success = Task.Run(() => repository.CommitInventory(other, healthy,
                new(healthy.Inventory, healthy.Inventory with { Gold = 50, Items = healthy.Inventory.Items.RemoveAt(0) }, "successful-concurrent")));
            await Task.WhenAll(failure, success).WaitAsync(TimeSpan.FromSeconds(10));
            TestCheck.That(await failure && JsonSerializer.Serialize(repository.LoadCharacter(first, current.Slot)) == original,
                "一个账号审计晚期失败回滚UID、余额与revision");
            healthy = await success;
            TestCheck.That(healthy.Inventory.Gold == 50 && healthy.Inventory.Items.IsEmpty &&
                repository.LoadCharacter(other, healthy.Slot).Revision == healthy.Revision, "另一账号竞争写入保持完整提交");
            using (var command = external.CreateCommand())
            {
                command.CommandText = "SELECT count(*) FROM inventory_audit_log WHERE action_name='failed-concurrent'";
                TestCheck.That((long)command.ExecuteScalar()! == 0, "失败事务不遗留审计");
                command.CommandText = "PRAGMA foreign_key_check";
                using var reader = command.ExecuteReader();
                TestCheck.That(!reader.Read(), "并发成功/回滚后外键完整");
            }
            using var replaced = owners.Acquire(first.Account, () => { });
            bool stale = false;
            try { repository.Save(first, current, location, location); }
            catch (InvalidOperationException) { stale = true; }
            TestCheck.That(stale && JsonSerializer.Serialize(repository.LoadCharacter(replaced, current.Slot)) == original,
                "接管后旧连接不能写库，新租约重载原已提交内容");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS {TestCheck.Count - before} 账号并发SQLite、writer等待与事务回滚断言");
    }
}
