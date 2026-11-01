using DfoServer.Infrastructure;
using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class CurrencyTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = 0;
        void Check(bool value, string name) { checks++; if (!value) throw new InvalidOperationException("FAIL currency: " + name); }
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception e) when (e is SqliteException or InvalidDataException or InvalidOperationException) { rejected = true; }
            Check(rejected, name);
        }
        string root = Path.Combine(Path.GetTempPath(), "s5a9-currency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("wallet", () => { });
            var session = new GameSession(repo, towns, owner);
            var current = session.CreateCharacter("WalletOne", characters.Get(0));
            var inventory = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
            var currency = new CurrencyService(itemResources.GoldLimits);
            Check(currency.CarryLimit(current) == 100_000 && currency.CarryLimit(current with { Level = 60 }) == 400_000_000,
                "carry caps come from matched PVF levels");
            foreach (byte level in new byte[] { 60, 95 })
            {
                var boundary = current with { Level = level, Inventory = InventorySnapshot.Empty with
                    { Gold = 399_999_999, GoldCarryLimit = 9_999_999 } };
                Check(currency.CarryLimit(boundary) == 400_000_000 &&
                    currency.Grant(boundary, CurrencyKind.Gold, 2).Plan!.After.Gold == 400_000_000,
                    "旧七个9存量不覆盖PVF四亿上限，临界奖励仍按四亿饱和");
            }
            Check(repo.LoadAccountWallet(owner).Same(AccountWallet.Empty), "empty account wallet available before selection");
            var originalItems = current.Inventory.Items;
            current = Commit(inventory.Grant(current, [new(0, 200_000), new(42, 3), new(1, 2), new(2, 9), new(3037, 20), new(10158124, 8), new(2681762, 7), new(2681917, 6)]));
            Check(current.Inventory.Gold == 100_000 && current.Inventory.ReviveCoins == 5 && current.Inventory.WinPoints == 9, "gold cap and both coin reward IDs");
            Check(current.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 20 && current.Inventory.AccountWallet.Get(CurrencyKind.EpicSoul) == 8 &&
                current.Inventory.AccountWallet.Get(CurrencyKind.TokenCera) == 7 && current.Inventory.AccountWallet.Get(CurrencyKind.HappyTokenCera) == 6, "account rewards bypass physical stacks");
            Check(current.Inventory.Items.SequenceEqual(originalItems) && Query("SELECT count(*) FROM character_inventory_items") is 5L,
                "wallet rewards allocate no instances and retain all starter cores");
            Check(current.Inventory.AccountWallet.Revision == 1 && Query("SELECT count(*) FROM currency_audit_log") is 7L, "one account revision and exact changed-balance audit");
            var second = session.CreateCharacter("WalletTwo", characters.Get(1));
            Check(second.Inventory.AccountWallet.Same(current.Inventory.AccountWallet) && second.Inventory.Gold == 0 && second.Inventory.ReviveCoins == 0,
                "new role inherits account balances without copying character money");
            Reject(() => repo.Create(owner, new("BadSeed", towns.Room, towns.InitialReturn) { Inventory = current.Inventory }),
                "new role cannot replace existing account wallet from a caller snapshot");
            using var other = owners.Acquire("other", () => { });
            Check(repo.LoadAccountWallet(other).Same(AccountWallet.Empty), "wallets isolated by authenticated account");
            var stale = current;
            second = InventoryService.Commit(repo, owner, second, currency.Grant(second, CurrencyKind.Cera, 100));
            Check(currency.Grant(stale, CurrencyKind.Gold, 1).Plan is { Changed: false }, "full wallet grant is a no-op");
            // 金币已满的无变化奖励不写库；通过实际变化验证 stale 钱包保护。
            Reject(() => InventoryService.Commit(repo, owner, stale, CurrencyService.Spend(stale.Inventory, CurrencyKind.Gold, 1)),
                "changed plan validates shared revision even for character currency");
            current = repo.List(owner)[0];
            Check(current.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 100 && current.Inventory.Gold == 100_000, "role reload shares only account currencies");
            foreach (var kind in Enum.GetValues<CurrencyKind>())
            {
                var before = current.Inventory;
                var granted = currency.Grant(current, kind, int.MaxValue);
                current = Commit(granted);
                int expected = kind == CurrencyKind.Gold ? 100_000 : int.MaxValue;
                Check(CurrencyWallet.Get(current.Inventory, kind) == expected, "checked saturated grant " + kind);
                Check(!currency.Grant(current, kind, -1).Accepted && !CurrencyService.Spend(current.Inventory, kind, -1).Accepted,
                    "negative quantity rejected " + kind);
                var spent = CurrencyService.Spend(current.Inventory, kind, expected);
                current = Commit(spent);
                Check(CurrencyWallet.Get(current.Inventory, kind) == 0 && !CurrencyService.Spend(current.Inventory, kind, 1).Accepted,
                    "exact spend and insufficient balance " + kind);
            }
            Check(Query("SELECT count(*) FROM account_currency_balances") is 0L, "zero is represented without physical items or stale account rows");
            current = Commit(currency.Grant(current, CurrencyKind.Gold, 10));
            current = Commit(currency.Grant(current, CurrencyKind.Cera, 5));
            string beforeFailure = Snapshot();
            Sql("CREATE TRIGGER reject_currency_audit BEFORE INSERT ON currency_audit_log BEGIN SELECT RAISE(ABORT,'fixture'); END");
            var reward = inventory.Grant(current, [new(3033, 7), new(current.Inventory.Items[0].Core.ItemId, 1)]);
            Reject(() => InventoryService.Commit(repo, owner, current, reward), "failure after core write rolls back entire reward");
            Sql("DROP TRIGGER reject_currency_audit");
            Check(Snapshot() == beforeFailure, "rollback retains core UIDs, balance, both revisions and all audits");
            var full = current.Inventory with { Items = [.. Enumerable.Range(9, 56).Select(i => new InventoryItem(i + 100, new(InventoryListType.Main, (short)i), originalItems[0].Core))] };
            Check(!inventory.Grant(current with { Inventory = full }, [new(3033, 7), new(originalItems[0].Core.ItemId, 1)]).Accepted && full.AccountWallet.Get(CurrencyKind.BlackCube) == 0,
                "full page rejects entire mixed reward without granting its currency prefix");
            Sql("UPDATE characters SET level=60 WHERE slot=0");
            current = repo.List(owner)[0];
            current = Commit(currency.Initialize(current));
            Check(current.Inventory.GoldCarryLimit == 400_000_000 && currency.CarryLimit(current with { Level = 1 }) == 400_000_000,
                "saved carry cap never shrinks when level changes");
            current = Commit(currency.Grant(current, CurrencyKind.Gold, 20_000_000));
            for (byte stage = 1; stage <= 4; stage++)
            {
                uint gold = current.Inventory.Gold;
                Check(!currency.Upgrade(current, (byte)(stage + 1)).Accepted, "cannot skip expansion stage");
                current = Commit(currency.Upgrade(current, stage));
                Check(current.Inventory.Gold == gold - 5_000_000 && current.Inventory.GoldCarryLimit == (stage + 4) * 100_000_000 &&
                    current.Inventory.AuctionGoldLimit == current.Inventory.GoldCarryLimit && CurrencyService.UpgradeStage(current.Inventory) == stage,
                    "expansion commits fee and both limits " + stage);
                Check(!currency.Upgrade(current, stage).Accepted, "duplicate expansion stage cannot charge twice");
            }
            Check(!currency.Upgrade(current, 5).Accepted && !currency.Upgrade(current with { Level = 1 }, 1).Accepted, "max stage and level qualification");
            var lowGold = current with { Inventory = InventorySnapshot.Empty };
            Check(!currency.Upgrade(lowGold, 1).Accepted, "insufficient gold cannot expand");
            var excess = current with { Inventory = current.Inventory with { Gold = 900_000_000 } };
            Check(currency.Grant(excess, CurrencyKind.Gold, int.MaxValue).Plan!.After.Gold == 900_000_000, "existing over-cap balance is never reduced");
            byte[] fullBody = InventoryPackets.Full(current.Inventory);
            Check(fullBody.Length == 1850 && BinaryPrimitives.ReadUInt16LittleEndian(fullBody.AsSpan(3)) == 15, "all virtual balances including zeros serialized without physical main items");
            int[] ids = [0, 1, 2, 3033, 3034, 3035, 3036, 3037, 3262, 10100115, 10100116, 10099773, 10099774, 10099775, 10158124];
            for (int i = 0; i < ids.Length; i++)
            {
                var row = fullBody.AsSpan(5 + i * 123, 123);
                Check(BinaryPrimitives.ReadInt16LittleEndian(row) == (i < 3 ? i : i + 351) && BinaryPrimitives.ReadInt32LittleEndian(row[2..]) == ids[i] &&
                    row[10..].IndexOfAnyExcept((byte)0) < 0, "Client virtual row has quantity only " + ids[i]);
            }
            byte[] expansion = new byte[14]; expansion[13] = 1;
            Check(CurrencyRequests.GoldLimitUpgrade(expansion) == 1 && CurrencyRequests.IsCeraQuery([]) && !CurrencyRequests.IsCeraQuery([0]), "currency request widths");
            Reject(() => CurrencyRequests.GoldLimitUpgrade(new byte[15]), "legacy expansion request width rejected");
            Check(Convert.ToHexString(CurrencyPackets.Cera(current.Inventory.AccountWallet)) == "01050000000000000000000000", "CERA three independent i32 balances");
            Check(Convert.ToHexString(CurrencyPackets.GoldLimitUpgraded(current.Inventory)) == "010400000000", "Client expansion success layout");
            var reopened = new CharacterRepository(path).List(owner)[0];
            Check(JsonSerializer.Serialize(reopened) == JsonSerializer.Serialize(current), "wallet persists after repository restart");
            using var replacement = owners.Acquire("wallet", () => { });
            Reject(() => repo.LoadAccountWallet(owner), "stale owner cannot read replacement wallet");
            Reject(() => InventoryService.Commit(repo, owner, current, CurrencyService.Spend(current.Inventory, CurrencyKind.Cera, 1)), "stale owner cannot spend");

            Character Commit(InventoryResult result)
            {
                Check(result.Accepted, "test operation accepted");
                return InventoryService.Commit(repo, owner, current, result);
            }
            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
            }
            void Sql(string sql) => Query(sql);
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + ":" + Query("SELECT count(*) FROM inventory_audit_log") + ":" + Query("SELECT count(*) FROM currency_audit_log");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {checks} currency assertions");
    }
}
