using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Trade;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PlayerTradeRepositoryTests
{
    public static void RunCore()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-player-trade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var database = new SqliteGameDatabase(path);
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var firstOwner = owners.Acquire("player-trade-a", () => { });
            using var secondOwner = owners.Acquire("player-trade-b", () => { });
            var first = repository.Create(firstOwner, new("TradeA", new(38, 1, 450, 234, 0), null)
            { Inventory = new([new(0, new(InventoryListType.Main, 121), new(1001, ItemKind.Material, 3))], Gold: 1000) });
            var second = repository.Create(secondOwner, new("TradeB", new(38, 1, 450, 234, 0), null)
            { Inventory = new([new(0, new(InventoryListType.Main, 122), new(1002, ItemKind.Material, 4))], Gold: 200) });
            var firstPlan = new InventoryPlan(first.Inventory, first.Inventory with { Gold = 900 }, "player_trade");
            var secondPlan = new InventoryPlan(second.Inventory, second.Inventory with { Gold = 300 }, "player_trade");
            var unbalanced = new InventoryPlan(second.Inventory, second.Inventory with { Gold = 301 }, "player_trade");
            Reject<InvalidDataException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, first, second, firstPlan, unbalanced),
                "不守恒的金币交易不能提交");
            var injected = new InventoryPlan(second.Inventory, secondPlan.After, "player_trade") { ExpBonus = (100, 1100) };
            Reject<InvalidDataException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, first, second, firstPlan, injected),
                "交易计划不能携带无关经验变更");
            Sql($"CREATE TRIGGER fail_second_trade BEFORE UPDATE ON characters WHEN NEW.id={second.Id} BEGIN SELECT RAISE(ABORT, 'second trade write'); END");
            Reject<SqliteException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, first, second, firstPlan, secondPlan),
                "第二方写入失败必须回滚第一方");
            var unchangedFirst = repository.LoadCharacter(firstOwner, first.Slot);
            var unchangedSecond = repository.LoadCharacter(secondOwner, second.Slot);
            That(unchangedFirst.Inventory.Gold == 1000 && unchangedFirst.Revision == first.Revision &&
                unchangedSecond.Inventory.Gold == 200 && unchangedSecond.Revision == second.Revision,
                "第二方失败后双方金币和版本不变");
            Sql("DROP TRIGGER fail_second_trade");
            var (savedFirst, savedSecond) = repository.CommitPlayerTrade(firstOwner, secondOwner, first, second, firstPlan, secondPlan);
            That(savedFirst.Inventory.Gold == 900 && savedSecond.Inventory.Gold == 300 &&
                repository.LoadCharacter(firstOwner, first.Slot).Inventory.Gold == 900 &&
                repository.LoadCharacter(secondOwner, second.Slot).Inventory.Gold == 300,
                "双方金币在同一事务中落库");
            Reject<InvalidOperationException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, first, second, firstPlan, secondPlan),
                "旧角色快照不能重放交易");

            var firstItem = savedFirst.Inventory.Items.Single();
            var secondItem = savedSecond.Inventory.Items.Single();
            var exchangeFirst = new InventoryPlan(savedFirst.Inventory,
                savedFirst.Inventory with { Items = [secondItem with { Position = firstItem.Position }] }, "player_trade");
            var exchangeSecond = new InventoryPlan(savedSecond.Inventory,
                savedSecond.Inventory with { Items = [firstItem with { Position = secondItem.Position }] }, "player_trade");
            var firstTransfers = new HashSet<long> { firstItem.Uid };
            var secondTransfers = new HashSet<long> { secondItem.Uid };
            Reject<InvalidDataException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, savedFirst, savedSecond,
                exchangeFirst, exchangeSecond), "未授权物品不能转移");
            Sql($"CREATE TRIGGER fail_second_item_trade BEFORE UPDATE ON characters WHEN NEW.id={second.Id} BEGIN SELECT RAISE(ABORT, 'second trade write'); END");
            Reject<SqliteException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, savedFirst, savedSecond,
                exchangeFirst, exchangeSecond, firstTransfers, secondTransfers), "物品交易第二方写入失败必须回滚");
            That(repository.LoadCharacter(firstOwner, first.Slot).Inventory.Items.Single() == firstItem &&
                repository.LoadCharacter(secondOwner, second.Slot).Inventory.Items.Single() == secondItem,
                "物品交易失败后双方槽位、实例与所有权不变");
            Sql("DROP TRIGGER fail_second_item_trade");
            var (exchangedFirst, exchangedSecond) = repository.CommitPlayerTrade(firstOwner, secondOwner, savedFirst, savedSecond,
                exchangeFirst, exchangeSecond, firstTransfers, secondTransfers);
            That(exchangedFirst.Inventory.Items.Single().Uid == secondItem.Uid &&
                exchangedSecond.Inventory.Items.Single().Uid == firstItem.Uid &&
                repository.LoadCharacter(firstOwner, first.Slot).Inventory.Items.Single() == exchangedFirst.Inventory.Items.Single() &&
                repository.LoadCharacter(secondOwner, second.Slot).Inventory.Items.Single() == exchangedSecond.Inventory.Items.Single(),
                "双方物品实例在同一事务中交换");
            var splitItem = exchangedFirst.Inventory.Items.Single();
            var ticket = new TradeTicket(new(exchangedFirst.Id, firstOwner.Generation, 1), new(exchangedSecond.Id, secondOwner.Generation, 1),
                new(10, [new(3, splitItem, 2)]), new(0, []));
            var splitPlan = TradeSettlement.Plan(exchangedFirst, exchangedSecond, ticket)!;
            var forged = new InventoryPlan(splitPlan.Second.Before, splitPlan.Second.After with { Gold = splitPlan.Second.After.Gold + 1 }, "player_trade");
            Reject<InvalidDataException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, exchangedFirst, exchangedSecond,
                splitPlan.First, forged, ticket: ticket), "冻结报价不能替换为伪造的成交计划");
            Sql($"CREATE TRIGGER fail_split_trade BEFORE UPDATE ON characters WHEN NEW.id={second.Id} BEGIN SELECT RAISE(ABORT, 'split trade write'); END");
            Reject<SqliteException>(() => repository.CommitPlayerTrade(firstOwner, secondOwner, exchangedFirst, exchangedSecond,
                splitPlan.First, splitPlan.Second, ticket: ticket), "拆分交易第二方写入失败必须回滚");
            That(repository.LoadCharacter(firstOwner, first.Slot).Inventory.Items.Single() == splitItem &&
                repository.LoadCharacter(secondOwner, second.Slot).Inventory.Items.Single().Uid == firstItem.Uid,
                "拆分交易失败后双方实例和槽位不变");
            Sql("DROP TRIGGER fail_split_trade");
            var (splitFirst, splitSecond) = repository.CommitPlayerTrade(firstOwner, secondOwner, exchangedFirst, exchangedSecond,
                splitPlan.First, splitPlan.Second, ticket: ticket);
            That(splitFirst.Inventory.Items.Single().Core.Count == 2 &&
                splitSecond.Inventory.Items.Single(item => item.Core.ItemId == splitItem.Core.ItemId).Core.Count == 2 &&
                splitSecond.Inventory.Items.Single(item => item.Core.ItemId == splitItem.Core.ItemId).Uid != 0,
                "拆分交易提交后两侧数量守恒且新实例已分配 UID");

            void Sql(string statement)
            {
                using var connection = database.Open();
                using var command = connection.CreateCommand();
                command.CommandText = statement;
                command.ExecuteNonQuery();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
