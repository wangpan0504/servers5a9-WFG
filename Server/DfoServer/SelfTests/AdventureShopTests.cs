using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Auth;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AdventureShopTests
{
    public static void RunCore()
    {
        int checks = Count;
        var catalog = AdventureCoreTests.SyntheticCatalog();
        var now = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        That(AdventureRequests.Purchase([1, 0xE8, 3, 0, 0]) == (1, 1000), "商店保留u8类别和完整u32选择值");
        Reject<InvalidDataException>(() => AdventureRequests.Purchase([1, 0, 0, 0]), "商店不截断商品编号");
        // 周期列先对齐当前周四边界，才能断言同周期载入是零变化。
        var state = AdventureState.Initial("shop", now) with { CapsuleWeek = AdventurePeriod.CapsuleWeek(catalog, now),
            Purchases = new Dictionary<int, uint> { [1000] = 2 }.ToImmutableDictionary() };
        var information = AdventurePackets.Information(1, new(state, []), catalog, [], 2);
        That(BitConverter.ToUInt32(information, 18 + AdventurePackets.PurchasesOffset) == 1000 &&
            BitConverter.ToUInt32(information, 22 + AdventurePackets.PurchasesOffset) == 2, "7440B购买记录使用物品ID/次数而不是PVF序号");
        That(AdventurePeriod.Apply(state, catalog, now.AddMonths(1)).Purchases.IsEmpty &&
            ReferenceEquals(AdventurePeriod.Apply(state, catalog, now), state), "月限购只在新月份投影重置");
        // 官方改版公告：骁勇/荣耀结晶购买次数与结晶数量在每月1日06:00初始化，月初零点至05:59仍属上月。
        var beforeMonth = new DateTimeOffset(2026, 11, 1, 5, 59, 0, TimeSpan.FromHours(8));
        var atMonth = new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.FromHours(8));
        var cycle = state with { Points = [20, 40, 0, 0, 0], CapsuleWeek = AdventurePeriod.CapsuleWeek(catalog, beforeMonth) };
        That(ReferenceEquals(AdventurePeriod.Apply(cycle, catalog, beforeMonth), cycle), "商店月重置按UTC+8每月1日06:00切换，05:59仍是上月");
        var reset = AdventurePeriod.Apply(cycle, catalog, atMonth);
        That(reset.Month == 202611 && reset.Points.ToArray().SequenceEqual(new uint[] { 20, 0, 0, 0, 0 }) && reset.Purchases.IsEmpty,
            "06:00后清空荣耀结晶与类型3限购，保留骁勇结晶");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-adventure-shop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            new AuthenticationService(new AuthenticationRepository(path)).Create("shop", "fixture-password");
            using var owner = new AccountOwners().Acquire("shop", () => { });
            var actor = repo.Create(owner, new("ShopActor", new(1, 0, 10, 10), null));
            repo.InitializeAdventure(owner, catalog, now);
            using var db = Open(path);
            byte[] points = new byte[20]; BitConverter.GetBytes(10u).CopyTo(points, 0);
            using (var seed = db.CreateCommand())
            {
                seed.CommandText = "UPDATE account_adventures SET points=$points"; seed.Parameters.AddWithValue("$points", points); seed.ExecuteNonQuery();
            }
            var before = repo.ReadAdventure(owner, catalog, now);
            var inventory = new InventoryResult(true, 0, new(actor.Inventory, actor.Inventory with
                { Items = [new(0, new(InventoryListType.Main, 65), new(1000, ItemKind.Consumable, 1))] }, "adventure_shop_purchase"));
            var plan = new AdventureShopPlan(before, before.State with { Points = before.State.Points.SetItem(0, 9),
                Purchases = before.State.Purchases.Add(1000, 1) }, actor, inventory);
            string original = JsonSerializer.Serialize(actor);
            Execute(db, "CREATE TRIGGER reject_purchase BEFORE INSERT ON account_adventure_receipts BEGIN SELECT RAISE(ABORT,'late failure'); END");
            Reject<SqliteException>(() => repo.CommitAdventureShop(owner, plan, catalog, now), "购买晚期凭据失败回滚全部资产和费用");
            That(JsonSerializer.Serialize(repo.LoadCharacter(owner, actor.Slot)) == original && Query(db, "SELECT count(*) FROM account_adventure_purchases") is 0L &&
                Query(db, "SELECT revision FROM account_adventures") is 0L, "失败不留实体、限购或账号版本");
            Execute(db, "DROP TRIGGER reject_purchase");
            var committed = repo.CommitAdventureShop(owner, plan, catalog, now);
            That(committed.Character.Inventory.Items.Single().Uid > 0 && committed.Snapshot.State.Points[0] == 9 &&
                committed.Snapshot.State.Purchases[1000] == 1 && Query(db, "SELECT count(*) FROM account_adventure_receipts WHERE action='shop_purchase'") is 1L,
                "只使用实际提交UID，同时保存结晶、限购和审计");
            Reject<InvalidOperationException>(() => repo.CommitAdventureShop(owner, plan, catalog, now), "旧账号购买计划不能重复覆盖");
            _ = repo.Create(owner, new("ShopRep", new(1, 0, 10, 10), null) { Level = 95, GrowType = 33 });
            Execute(db, "UPDATE account_adventures SET month=202609; UPDATE account_adventure_purchases SET count=2");
            var projected = repo.ReadAdventure(owner, catalog, now);
            That(projected.State.Purchases.IsEmpty && Query(db, "SELECT count FROM account_adventure_purchases") is 2L,
                "只读月投影不取得writer或隐式删旧数据");
            var representative = new AdventureRepresentativeService(catalog).Prepare(projected, actor.Id, 0, 1, null)!;
            _ = repo.CommitAdventureRepresentative(owner, representative, catalog, now);
            That(Query(db, "SELECT count(*) FROM account_adventure_purchases") is 0L && Query(db, "SELECT month FROM account_adventures") is 202610L,
                "代表写入也保存完整月重置，不能只推进月份而保留旧限购");
            That(new CharacterRepository(path).ReadAdventure(owner, catalog, now).State.Purchases.IsEmpty &&
                Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L, "重开保留周期和完整资产外键");
            DatabaseSchemaTests.RewindRaidLifecycle(db);
            DatabaseSchemaTests.RemoveAdventurePurchases(db); Execute(db, "PRAGMA user_version=31; CREATE VIEW account_adventure_purchases AS SELECT 1 AS collision");
            Reject<SqliteException>(() => new CharacterRepository(path), "v32撞名迁移失败回滚");
            That(Query(db, "PRAGMA user_version") is 31L && Query(db, "SELECT count(*) FROM account_adventure_receipts") is 1L,
                "迁移失败不清理已有远征或购买凭据");
            Execute(db, "DROP VIEW account_adventure_purchases");
            _ = new CharacterRepository(path);
            That(Query(db, "SELECT count(*) FROM account_adventure_purchases") is 0L &&
                Query(db, "SELECT count(*) FROM character_inventory_items") is 1L, "v31升级仅补空限购表，不删实体或补历史购买");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - checks} 冒险团购买字节、周期、原子资产及增量迁移断言");
    }
    static SqliteConnection Open(string path) { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open(); return db; }
    static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    static object? Query(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
}
