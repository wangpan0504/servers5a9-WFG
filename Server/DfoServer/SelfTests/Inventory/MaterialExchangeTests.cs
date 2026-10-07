using DfoServer.Infrastructure;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class MaterialExchangeTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL material exchange: " + name);
        }
        void Reject(Action action, string name)
        {
            bool failed = false;
            try { action(); }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or SqliteException) { failed = true; }
            Check(failed, name);
        }
        var catalog = itemResources.Items;
        var service = new InventoryShopService(catalog, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces);
        var now = DateTimeOffset.UtcNow;
        var current = new Character(1, 0, "Exchange", 0, towns.Room, towns.InitialReturn) { Job = 15 };
        var sky = Core(10305616, 350) with { SortLockFlag = 1 };
        var stone = Core(10158964, 20);
        current = current with { Inventory = new([new(1, Bag(3), sky), new(2, Bag(121), sky), new(3, Bag(122), stone)], Gold: 12345) };
        var weapon = new BuyItemRequest(101000783, 1, 247, 486);
        Check(itemResources.Shops.Allows(486, 247, weapon.ItemId) && catalog.Get(weapon.ItemId).ShopPrice.Materials.SequenceEqual(new ItemMaterialCost[] { new(10305616, 600), new(10158964, 15) }),
            "matched shop sells weapon for two real materials");
        var result = service.Buy(current, weapon with { Count = 99 }, out var receipt, now);
        Check(result.Accepted && receipt!.Count == 1 && receipt.Paid.Gold == 12345, "equipment exchange uses one recipe without extra job/level limits or inferred gold");
        Check(result.Plan!.After.Find(Bag(3)) is null && result.Plan.After.Find(Bag(121)) is { Uid: 2, Core.Value: 100, Core.SortLockFlag: 1 } &&
            result.Plan.After.Find(Bag(122)) is { Uid: 3, Core.Value: 5 }, "first material spans quickbar and page, preserving surviving UIDs and locks");
        Check(result.Plan.After.Find(Bag(9))!.Core == InventoryRewardGrantService.Create(catalog.Get(weapon.ItemId)), "exchange creates complete equipment core");
        Check(receipt!.Materials.SequenceEqual(new ShopMaterialBalance[] { new(10305616, 0), new(10158964, 5) }), "ACK uses each first old slot balance, not cross-stack sum");
        byte[] ack = ShopPackets.Bought(result, receipt);
        Check(ack.Length == 157 && ack[140] == 2 && I32(ack, 141) == 10305616 && I32(ack, 145) == 0 && I32(ack, 149) == 10158964 && I32(ack, 153) == 5,
            "two-material native ACK contains both i32 pairs");
        Check(I32(ack, 1) == 12345 && I32(ack, 19) == weapon.ItemId && I32(ack, 23) == InventoryRewardGrantService.InitialQuality,
            "multi-material tail leaves price and complete reward entry intact");
        var before = current.Inventory;
        foreach (var invalid in new[] { weapon with { ShopId = 2 }, weapon with { NpcId = 1 }, weapon with { Count = 1_000_001 } })
            Check(!service.Buy(current, invalid, out _).Accepted && current.Inventory == before, "invalid offer/quantity never consumes materials");
        foreach (short slot in new short[] { 3, 122 })
        {
            var item = before.Find(Bag(slot))!;
            foreach (var blocked in new[] { item.Core with { Value = 1 }, item.Core with { EquipmentLockId = 1 }, item.Core with { ExpireTime = 1 } })
            {
                var input = current with { Inventory = before with { Items = before.Items.Replace(item, item with { Core = blocked }) } };
                Check(service.Buy(input, weapon, out _) is { Accepted: false, Error: 10 } && current.Inventory == before,
                    "either material shortage/lock/expiry rejects whole recipe");
            }
        }
        var extra = new InventoryItem(4, Bag(4), sky with { Value = 600 });
        var first = before.Find(Bag(3))!;
        var guarded = current with { Inventory = before with { Items = before.Items.Replace(first, first with { Core = sky with { EquipmentLockId = 1 } }).Add(extra) } };
        result = service.Buy(guarded, weapon, out receipt);
        Check(result.Accepted && result.Plan!.After.Find(Bag(3)) == guarded.Inventory.Find(Bag(3)) && result.Plan.After.Find(Bag(4)) is null,
            "a locked earlier stack does not forbid using enough later material");
        Check(receipt!.Materials.SequenceEqual(new ShopMaterialBalance[] { new(10158964, 5) }), "ACK leaves untouched first match intact; actual later deletions use delta");
        var full = current with { Inventory = before with { Items = before.Items.AddRange(Enumerable.Range(9, 56).Select(slot => new InventoryItem(100 + slot, Bag((short)slot), Core(10400)))) } };
        Check(service.Buy(full, weapon, out _) is { Accepted: false, Error: 4 } && full.Inventory.Find(Bag(122))!.Core.Count == 20,
            "full output page keeps all materials and gold");

        var bell = new BuyItemRequest(10099377, 3, 5, 5);
        var mixed = current with { Inventory = new([new(1, Bag(3), Core(3027, 10)), new(2, Bag(121), Core(3027, 25))], Gold: 54321)
        { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.WhiteCube, 100) } };
        result = service.Buy(mixed, bell, out receipt);
        Check(result.Accepted && result.Plan!.After.Find(Bag(121))!.Core.Count == 5 && result.Plan.After.AccountWallet.Get(CurrencyKind.WhiteCube) == 70 &&
            result.Plan.After.Find(Bag(65))!.Core.Count == 3, "batch purchase multiplies both physical and wallet costs");
        Check(receipt!.Materials.SequenceEqual(new ShopMaterialBalance[] { new(3027, 0), new(3034, 70) }) && receipt.Core!.Value == 3,
            "mixed costs report paid balances and reward increment");
        var shortWallet = mixed with { Inventory = mixed.Inventory with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.WhiteCube, 29) } };
        Check(!service.Buy(shortWallet, bell, out _).Accepted && shortWallet.Inventory.Find(Bag(3))!.Core.Count == 10, "wallet shortage does not partially consume physical cost");
        var multiply = InventoryMaterialConsumptionService.Plan(mixed.Inventory, [new(3027, 5), new(3027, 5), new(3034, 10)], itemResources.EpicPieces, now, multiplier: 3);
        Check(multiply.Accepted && multiply.Plan!.After.Find(Bag(121))!.Core.Count == 5 && multiply.Plan.After.AccountWallet.Get(CurrencyKind.WhiteCube) == 70,
            "shared material planner combines repeated cost entries before multiplying");
        Check(!InventoryMaterialConsumptionService.Plan(mixed.Inventory, [new(3027, int.MaxValue)], itemResources.EpicPieces, now, 2).Accepted &&
            !InventoryMaterialConsumptionService.Plan(mixed.Inventory, [new(3027, int.MaxValue), new(3027, 1)], itemResources.EpicPieces, now).Accepted,
            "cost multiplication and duplicate-sum overflow reject without wrapping");
        foreach (var cost in new ItemMaterialCost[] { new(0, 1), new(3027, 0), new(3027, -1) })
            Check(!InventoryMaterialConsumptionService.Plan(mixed.Inventory, [cost], itemResources.EpicPieces, now).Accepted, "invalid shared material input rejected");
        Check(!InventoryMaterialConsumptionService.Plan(mixed.Inventory, [new(3027, 1)], itemResources.EpicPieces, now, 0).Accepted,
            "zero multiplier cannot grant a free recipe");
        var equipment = Core(10400) with { SortLockFlag = 1, EnchantCardId = 1337 };
        var physical = new InventorySnapshot([new(1, Bag(3), equipment), new(2, Bag(9), equipment with { EquipmentLockId = 1 }), new(3, Bag(10), equipment)]);
        var consumeEquipment = InventoryMaterialConsumptionService.Plan(physical, [new(10400, 2)], itemResources.EpicPieces, now);
        Check(consumeEquipment.Accepted && consumeEquipment.Plan!.After.Items.SequenceEqual(new[] { physical.Items[1] }),
            "equipment is a one-count recipe input, preserving locked copies and full cores");
        Check(!InventoryMaterialConsumptionService.Plan(physical, [new(10400, 3)], itemResources.EpicPieces, now).Accepted,
            "equipment quality value is not available quantity");
        var stored = mixed.Inventory with { Items = [new(1, new(InventoryListType.PersonalCargo, 0), Core(3027, 100))] };
        Check(!InventoryMaterialConsumptionService.Plan(stored, [new(3027, 1)], itemResources.EpicPieces, now).Accepted, "shared cost planner never withdraws from storage");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-material-exchange-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("material-exchange", () => { });
            current = repo.Create(owner, new("ExchangeOne", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(3), Core(3027, 10)), new(0, Bag(121), Core(3027, 25))], Gold: 54321)
            });
            current = InventoryService.Commit(repo, owner, current, InventoryRewardGrantService.TryGrantBatch(current, [new(3034, 100)], catalog, itemResources.GoldLimits, itemResources.EpicPieces));
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            current = session.Selected!;
            string saved = Snapshot();
            Query("CREATE TRIGGER fail_exchange BEFORE INSERT ON currency_audit_log BEGIN SELECT RAISE(ABORT,'exchange rollback'); END");
            Reject(() => session.MutateInventory(role => service.Buy(role, bell, out _)), "failure after output insertion rejects without success");
            Check(Snapshot() == saved && session.Selected == current, "SQL rollback restores every cost, output UID, wallet, revisions and audit");
            Query("DROP TRIGGER fail_exchange");
            var stale = current;
            var stalePlan = service.Buy(current, bell, out _).Plan!;
            result = session.MutateInventory(role => service.Buy(role, bell, out _));
            current = session.Selected!;
            Check(result.Accepted && current.Inventory.Items.Single(item => item.Core.ItemId == 10099377).Uid > 0, "mixed-material exchange commits one output instance");
            Reject(() => repo.CommitInventory(owner, stale, stalePlan), "old revision cannot repeat a paid exchange");
            saved = Snapshot();
            Check(!session.MutateInventory(role => service.Buy(role, bell, out _)).Accepted && Snapshot() == saved, "insufficient replay has no additional effects");
            Check(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(current), "restart preserves exact costs and output");
            var sibling = repo.Create(owner, new("ExchangeTwo", towns.Room, towns.InitialReturn));
            Check(sibling.Inventory.AccountWallet.Get(CurrencyKind.WhiteCube) == 70 && sibling.Inventory.Items.IsEmpty, "shared currency and personal materials retain correct ownership");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.MutateInventory(role => service.Buy(role, bell, out _)), "old generation cannot consume materials");
            Check(Query("PRAGMA integrity_check") is "ok" && Convert.ToInt64(Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion, "shared planner requires no schema change");

            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Query("SELECT count(*) FROM inventory_audit_log") + ":" +
                Query("SELECT count(*) FROM currency_audit_log") + ":" + Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {checks} multi-material exchange assertions");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(catalog.Get(id), count);
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
}
