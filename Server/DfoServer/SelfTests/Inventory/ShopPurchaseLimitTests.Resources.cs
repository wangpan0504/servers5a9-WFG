using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class ShopPurchaseLimitTests
{
    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-product-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("product-quota", () => { });
            var current = repo.Create(owner, new("QuotaOne", towns.Room, towns.InitialReturn) { Level = 95 });
            var sibling = repo.Create(owner, new("QuotaTwo", towns.Room, towns.InitialReturn) { Level = 95 });
            var inventory = new InventoryService(resources.Items, resources.GoldLimits, resources.EpicPieces);
            current = InventoryService.Commit(repo, owner, current, inventory.Grant(current, [new(10306750, 1000)]));
            sibling = InventoryService.Commit(repo, owner, sibling, inventory.Grant(sibling, [new(10306750, 1000)]));
            var shop = new InventoryShopService(resources.Items, resources.Shops, resources.GoldLimits, resources.EpicPieces);
            var now = DateTimeOffset.UtcNow;
            foreach (var (itemId, maximum, unitCost) in new[] { (10309042, 12, 4), (490012239, 4, 50), (490012565, 3, 35) })
            {
                var template = resources.Items.Get(itemId);
                That(template.ShopPrice.PeriodicLimit is { AccountWide: true, Period: ItemPurchasePeriod.Weekly } limit && limit.Count == maximum &&
                    template.ShopPrice.Materials is [{ ItemId: 10306750, Count: var cost }] && cost == unitCost,
                    "漩涡真实商品按原声明保留账号周上限和反物质费用");
                var request = new BuyItemRequest(itemId, maximum, 274, 626);
                That(resources.Shops.Allows(626, 274, itemId), "漩涡真实攻坚商店包含限购商品");
                sibling = repo.List(owner).Single(row => row.Id == sibling.Id);
                var stale = shop.Buy(sibling, request with { Count = 1 }, out _, now);
                That(stale.Accepted, "另一个角色可在尚有账号额度时规划购买");
                var planned = shop.Buy(current, request, out _, now);
                That(planned.Accepted, "漩涡商品可一次购买到账号周上限");
                int materialBefore = Materials(current);
                if (itemId == 490012239)
                {
                    string before = JsonSerializer.Serialize(repo.List(owner));
                    Sql("CREATE TRIGGER fail_period_quota AFTER INSERT ON item_purchase_counts WHEN NEW.item_id=490012239 BEGIN SELECT RAISE(ABORT,'quota rollback'); END");
                    Reject(() => InventoryService.Commit(repo, owner, current, planned), "额度写入晚失败必须回滚材料、礼盒及角色状态");
                    That(JsonSerializer.Serialize(repo.List(owner)) == before && Query("SELECT count(*) FROM item_purchase_counts WHERE item_id=490012239") is 0L,
                        "限购写入失败不留下额外礼盒或已消耗额度");
                    Sql("DROP TRIGGER fail_period_quota");
                }
                current = InventoryService.Commit(repo, owner, current, planned);
                That(Materials(current) == materialBefore - maximum * unitCost &&
                    current.Inventory.Items.Where(row => row.Core.ItemId == itemId).Sum(row => row.Core.Count) == maximum,
                    "真实材料费用与完整礼盒数量在同一次购买提交");
                Reject(() => InventoryService.Commit(repo, owner, sibling, stale), "旧角色快照不能覆盖另一个角色已消费的账号额度");
                sibling = repo.List(owner).Single(row => row.Id == sibling.Id);
                That(shop.Buy(sibling, request with { Count = 1, NpcId = 624 }, out _, now) is { Accepted: false, Error: 0x85 },
                    "换角色、商人不能绕过真实账号周上限");
                That(new CharacterRepository(path).List(owner).Single(row => row.Id == current.Id).Inventory.ShopPurchases
                    .Any(row => row.ItemId == itemId && row.Count == maximum && row.Period == ItemPurchasePeriod.Weekly),
                    "新建仓储读取后保留实际商品周计数");
                var rows = shop.PurchaseCounts(current, 626, now)!;
                That(rows.Any(row => row.ItemId == itemId && row.Count == maximum && row.Period == ItemPurchasePeriod.Weekly),
                    "真实NPC查询包含新商品限购回显");
            }
            int today = ShopPurchaseCount.Today(now);
            var history = current with { Inventory = current.Inventory with { ShopPurchases =
            [
                .. current.Inventory.ShopPurchases.Where(row => row.ItemId != 10309042),
                new(0, 10309042, true, false, ShopPurchaseCount.PeriodDay(today, ItemPurchasePeriod.Weekly), 12) { Period = ItemPurchasePeriod.Weekly },
                new(0, 10309042, true, false, today, 9) { Period = ItemPurchasePeriod.Daily },
                new(0, 10309042, true, false, ShopPurchaseCount.PeriodDay(today, ItemPurchasePeriod.Monthly), 11) { Period = ItemPurchasePeriod.Monthly },
                new(0, 10309042, false, false, ShopPurchaseCount.PeriodDay(today, ItemPurchasePeriod.Weekly), 5) { Period = ItemPurchasePeriod.Weekly }
            ] } };
            var projectedHistory = shop.PurchaseCounts(history, 626, now)!;
            That(projectedHistory.Count(row => row.ItemId == 10309042) == 1 &&
                projectedHistory.Single(row => row.ItemId == 10309042) is { AccountWide: true, Period: ItemPurchasePeriod.Weekly, Count: 12 },
                "同item历史不同period及错误scope可保留于账本，但查询只回当前资源声明，不能合计或泄露角色额度");
            var nativeHistory = NativePurchaseCounts(DfoServer.Network.Builders.Inventory.ShopPackets.PurchaseCounts(projectedHistory));
            That(nativeHistory.Purchases.GetValueOrDefault(10309042) == 12 && nativeHistory.Sales.Count == 0,
                "真实周限购查询经四组原生消费者恢复购买缓存，不恢复历史period或销售缓存");
            using (var otherOwner = owners.Acquire("product-other", () => { }))
            {
                var other = repo.Create(otherOwner, new("QuotaOther", towns.Room, towns.InitialReturn) { Level = 95 });
                other = InventoryService.Commit(repo, otherOwner, other, inventory.Grant(other, [new(10306750, 100)]));
                That(shop.Buy(other, new(10309042, 12, 274, 626), out _, now).Accepted, "其他账号有独立的真实商品周额度");
            }
            That(shop.Buy(current, new(10309042, 1, 274, 626), out _, now.AddDays(7)).Accepted,
                "持久化上周记录不阻止下周重新购买");
            SchemaUpgrade(Path.Combine(root, "upgrade.db"));

            int Materials(Character role) => role.Inventory.Items.Where(row => row.Core.ItemId == 10306750).Sum(row => row.Core.Count);
            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql;
                return command.ExecuteScalar();
            }
            void Sql(string sql) => Query(sql);
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(root, true); }
    }

    static void SchemaUpgrade(string path)
    {
        _ = new CharacterRepository(path);
        using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "DROP TABLE item_purchase_counts; INSERT INTO shop_purchase_counts VALUES('legacy',0,626,1001,0,0,2); PRAGMA user_version=166;";
            command.ExecuteNonQuery();
        }
        _ = new CharacterRepository(path);
        using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT count FROM shop_purchase_counts WHERE account='legacy'";
            That(command.ExecuteScalar() is 2L, "v166升级保留旧式商店限购原记录");
            command.CommandText = "SELECT count(*) FROM item_purchase_counts";
            That(command.ExecuteScalar() is 0L, "v167不根据历史库存猜造商品限购次数");
        }
    }

    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or SqliteException) { rejected = true; }
        That(rejected, message);
    }
}
