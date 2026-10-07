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
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class BoosterTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL booster: " + name);
        }
        void Reject(Action action, string name)
        {
            bool failed = false;
            try { action(); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or SqliteException) { failed = true; }
            Check(failed, name);
        }
        var catalog = itemResources.Items;
        BoosterBatchRandomTests.Run(Check);
        var random = new EdgeRandom();
        var service = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces, random);
        var now = DateTimeOffset.UtcNow;
        var current = new Character(1, 0, "Booster", 0, towns.Room, towns.InitialReturn) { Job = 15 };
        Check(itemResources.Boosters.Get(7542).Pools.Length == 3 && itemResources.Boosters.Get(2058).Pools.Length == 2,
            "repeated pool tags remain independent in matched PVF");
        Check(itemResources.Boosters.DirectActivationItems.Contains(30) && itemResources.Boosters.DirectActivationItems.Contains(33) &&
            !itemResources.Boosters.DirectActivationItems.Contains(1), "contract identity comes from matched premium resources, coins stay wallets");
        var parsed = Parse("[etc]\n2 1106 0.25 7 1112 0.75 9\n[/etc]\n[etc]\n1 1106 100 3\n[/etc]");
        Check(parsed.Available && parsed.Pools.Length == 2 && parsed.Pools[0].Draws == 2 &&
            parsed.Pools[0].Rewards.SequenceEqual(new BoosterReward[] { new(1106, 25, 7), new(1112, 75, 9) }),
            "fractional weights retain exact proportions and all declared quantities");
        foreach (string bad in new[] { "[etc]\n0 1106 1 1\n[/etc]", "[etc]\n1 1106 -1 1\n[/etc]", "[etc]\n1 1106 1 0\n[/etc]",
            "[etc]\n1 1106 1\n[/etc]", "[etc]\n1 1106 1 1\n[/etc]\n99", "[etc]\n1 1106 1 1\n[/etc]\n[avatar]\n1 1 1 1 1 1\n[/avatar]" })
            Check(!Parse(bad).Available, "malformed or unsupported reward pool rejects whole definition");
        Check(Parse("[etc]\n1\n[/etc]\n[etc]\n1 1106 100 1\n[/etc]").Pools.Length == 1, "empty PVF placeholder is not a fake reward");
        Check(!itemResources.Boosters.Get(10162064).Available && !itemResources.Boosters.Get(490704415).Available && !itemResources.Boosters.Get(1106).Available,
            "quota, advancement pools and ordinary consumables are not silently treated as simple boxes");

        current = current with { Inventory = new([new(1, Bag(3), Core(7542, 2)), new(2, Bag(65), Core(1108, 990) with { SortLockFlag = 1 })], Gold: 12345) };
        var before = current.Inventory;
        var result = service.Open(current, new(3, 2), out var receipt, now);
        Check(!result.Accepted && result.Error == 4 && result.Plan is null && receipt is null && current.Inventory == before,
            "reward stack overflow keeps all boxes, rewards and gold without allocating another stack");
        current = current with { Inventory = before with { Items = before.Items.SetItem(1,
            before.Items[1] with { Core = before.Items[1].Core with { Value = 960 } }) } };
        before = current.Inventory;
        result = service.Open(current, new(3, 2), out receipt, now);
        Check(result.Accepted && current.Inventory == before && random.Calls == 0, "deterministic batch is planned without modifying source or drawing unnecessarily");
        Check(result.Plan!.After.Find(Bag(3)) is null && result.Plan.After.Find(Bag(65)) is { Uid: 2, Core.Value: 1000, Core.SortLockFlag: 1 } &&
            result.Plan.After.Items.Count(item => item.Core.ItemId == 1108) == 1, "quickbar source and exact stack capacity preserve surviving UID and sort lock");
        Check(receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(1108, 40), new(1114, 40), new(1016, 40) }) && result.Plan.After.Gold == 12345,
            "every independent pool is multiplied by source count without an extra fee");
        byte[] ack = BoosterPackets.Opened(result, receipt);
        Check(ack.Length == 42 && ack[0] == 1 && ack[1] == 0 && I32(ack, 2) == 7542 && U16(ack, 6) == 3 &&
            I32(ack, 8) == 0 && I32(ack, 12) == 0 && U16(ack, 16) == 3 && I32(ack, 18) == 1108 && I32(ack, 22) == 40,
            "Client success has full 18-byte header followed by item/count pairs");
        Check(BoosterPackets.Opened(InventoryResult.Reject(19), null).SequenceEqual(new byte[] { 0, 19, 0 }), "failure uses common u16 result code");
        Check(BoosterRequests.Read([3, 0, 2, 0, 0, 0]) == new BoosterOpenRequest(3, 2), "ordinary request carries u16 slot and i32 count");
        foreach (int width in new[] { 0, 2, 5, 7, 9 })
            Reject(() => BoosterRequests.Read(new byte[width]), "parser rejects nonordinary width " + width);
        foreach (int count in new[] { int.MinValue, -1, 0, 3, int.MaxValue })
            Check(!service.Open(current, new(3, count), out _).Accepted && current.Inventory == before, "invalid source count does not normalize to one or whole stack");
        foreach (var core in new[] { Core(7542, 2) with { EquipmentLockId = 1 }, Core(7542, 2) with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) }, Core(1106) })
            Check(!service.Open(current with { Inventory = new([new(1, Bag(3), core)]) }, new(3, 1), out _).Accepted,
                "safety lock, exact expiry and wrong source reject without consuming");
        var sorted = current with { Inventory = new([new(1, Bag(3), Core(7542) with { SortLockFlag = 1 })]) };
        Check(service.Open(sorted, new(3, 1), out _).Accepted, "sorting lock does not forbid item use");

        var filled = Enumerable.Range(65, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), Core(1106, 1000))).ToImmutableArray();
        filled = filled.SetItem(0, new(65, Bag(65), Core(7542))).SetItem(1, new(66, Bag(66), Core(1114, 980)))
            .SetItem(2, new(67, Bag(67), Core(1016, 980)));
        current = current with { Inventory = new(filled) };
        result = service.Open(current, new(65, 1), out _);
        Check(result.Accepted && result.Plan!.After.Items.Length == 56 && result.Plan.After.Find(Bag(65)) is { Uid: 0, Core.ItemId: 1108, Core.Value: 20 },
            "full page succeeds using the exhausted source slot, without a fixed empty-slot prerequisite");
        var notFreed = current with { Inventory = current.Inventory with { Items = filled.SetItem(0, filled[0] with { Core = Core(7542, 2) }) } };
        Check(!service.Open(notFreed, new(65, 1), out _).Accepted && notFreed.Inventory.Find(Bag(65))!.Core.Count == 2,
            "full page rejects complete operation when source slot is retained");

        current = current with { Inventory = new([new(1, Bag(3), Core(2058, 2))]) };
        result = service.Open(current, new(3, 2), out receipt);
        Check(result.Accepted && random.Calls == 4 && receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(2050, 2), new(2054, 2) }),
            "both repeated pools draw once for every source and combine identical outcomes");
        random.Last = true;
        result = service.Open(current, new(3, 1), out receipt);
        Check(result.Accepted && receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(2053, 1), new(2057, 1) }), "last weight interval stays reachable");
        var bulkRandom = new BoosterBatchRandomTests.BudgetRandom(31024);
        var bulkService = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces, bulkRandom);
        var bulk = current with { Inventory = new([new(1, Bag(3), Core(2058, int.MaxValue))]) };
        var timer = System.Diagnostics.Stopwatch.StartNew();
        result = bulkService.Open(bulk, new(3, int.MaxValue), out receipt);
        timer.Stop();
        Check(result.Accepted && bulkRandom.Calls < 128 && receipt!.Rewards.Sum(row => (long)row.Count) == 2L * int.MaxValue &&
            result.Plan!.After.Find(Bag(3)) is null && bulk.Inventory.Find(Bag(3))!.Core.Count == int.MaxValue,
            "largest legal random batch keeps all rewards, consumes no source before commit and needs bounded random work");
        Check(receipt!.Rewards.Where(row => row.ItemId is >= 2050 and <= 2053).Sum(row => (long)row.Count) == int.MaxValue &&
            receipt.Rewards.Where(row => row.ItemId is >= 2054 and <= 2057).Sum(row => (long)row.Count) == int.MaxValue &&
            receipt.Rewards.All(row => result.Plan!.After.Items.Where(item => item.Core.ItemId == row.ItemId).Sum(item => (long)item.Core.Count) == row.Count),
            "independent pool counts and complete grant plan agree for a multi-billion draw request");
        Console.WriteLine($"Bulk booster planning: {timer.Elapsed.TotalMilliseconds:F2}ms, {bulkRandom.Calls} random calls");
        var fullEmblems = Enumerable.Range(289, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), Core(2050, int.MaxValue)));
        bulk = bulk with { Inventory = new([bulk.Inventory.Items[0], .. fullEmblems]) };
        Check(bulkService.Open(bulk, new(3, int.MaxValue), out receipt) is { Accepted: false, Error: 4 } && receipt is null &&
            bulk.Inventory.Find(Bag(3))!.Core.Count == int.MaxValue, "full reward page rejects entire large batch without partial consumption or truncation");
        current = current with { Inventory = new([new(1, Bag(3), Core(114))]) };
        result = service.Open(current, new(3, 1), out receipt);
        var output = result.Plan!.After.Items.Single();
        Check(result.Accepted && current.Level == 1 && current.Job == 15 && output.Core == InventoryRewardGrantService.Create(catalog.Get(receipt!.Rewards[0].ItemId)),
            "equipment reward keeps full core, seal and durability without output job or wear-level restrictions");

        foreach (var (box, reward, count) in new[] { (2660291, 1, 10), (10160255, 10158124, 1), (2683799, 2681762, 720) })
        {
            current = current with { Inventory = new([new(1, Bag(3), Core(box))]) };
            result = service.Open(current, new(3, 1), out receipt);
            var kind = CurrencyWallet.RewardKind(reward)!.Value;
            Check(result.Accepted && result.Plan!.After.Items.IsEmpty && CurrencyWallet.Get(result.Plan.After, kind) == count,
                "reward routes to actual wallet without a physical instance " + reward);
            var fullWallet = current with { Inventory = CurrencyWallet.Set(current.Inventory, kind, int.MaxValue) };
            Check(service.Open(fullWallet, new(3, 1), out _) is { Accepted: false, Error: 4 } && fullWallet.Inventory.Items.Length == 1,
                "wallet overflow keeps box and never truncates reward " + reward);
        }
        foreach (int id in new[] { 38, 762 })
        {
            current = current with { Inventory = new([new(1, Bag(3), Core(id))]) };
            int calls = random.Calls;
            Check(!service.Open(current, new(3, 1), out _).Accepted && random.Calls == calls, "unsupported detail or premium pool rejected before randomness " + id);
        }
        current = current with { Inventory = new([new(1, Bag(3), Core(2660291) with { Value = int.MaxValue })]) };
        Check(service.Open(current, new(3, int.MaxValue), out _) is { Accepted: false, Error: 4 }, "batch reward arithmetic overflow rejects without a long deterministic loop");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-booster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("booster-test", () => { });
            repo.Create(owner, new("BoxOne", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(3), Core(2660291)), new(0, Bag(65), Core(7542, 2))], Gold: 12345)
            });
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            var saved = session.Selected!;
            string snapshot = Snapshot();
            Query("CREATE TRIGGER fail_box BEFORE INSERT ON currency_audit_log BEGIN SELECT RAISE(ABORT,'box rollback'); END");
            Reject(() => session.PlanAndCommitInventory(role => service.Open(role, new(3, 1), out _)), "wallet write failure propagates before success");
            Check(Snapshot() == snapshot && session.Selected == saved, "box, reward, revisions, UID sequence and audit roll back together");
            Query("DROP TRIGGER fail_box");
            var stale = session.Selected!;
            var stalePlan = service.Open(stale, new(65, 2), out _).Plan!;
            Check(session.PlanAndCommitInventory(role => service.Open(role, new(3, 1), out _)).Accepted, "retry after explicit reconnectable failure consumes only one coin box");
            Reject(() => repo.CommitInventory(owner, stale, stalePlan), "old revision cannot commit a second random result");
            Check(session.PlanAndCommitInventory(role => service.Open(role, new(65, 2), out _)).Accepted, "complete physical reward batch commits");
            snapshot = Snapshot();
            Check(!session.PlanAndCommitInventory(role => service.Open(role, new(3, 1), out _)).Accepted && Snapshot() == snapshot,
                "exhausted replay cannot mint another reward");
            Check(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(session.Selected),
                "repository reopen restores every committed item and wallet exactly");
            var sibling = repo.Create(owner, new("BoxTwo", towns.Room, towns.InitialReturn));
            Check(sibling.Inventory.ReviveCoins == 0 && sibling.Inventory.Items.IsEmpty, "character-owned coin reward does not leak on character creation");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.PlanAndCommitInventory(role => service.Open(role, new(65, 1), out _)), "old generation cannot open boxes");
            Check(Query("PRAGMA integrity_check") is "ok" && Convert.ToInt64(Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion, "booster reuses atomic inventory storage without a schema change");

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
        InventoryPlanningTests.Run(towns, characters, itemResources, Check);
        Console.WriteLine($"PASS {checks} booster assertions");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(catalog.Get(id), count, now);
    }
    static BoosterDefinition Parse(string pools) => BoosterCatalog.Parse("[stackable type]\n`[booster]` 0\n[booster info]\n" + pools + "\n[/booster info]");
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static int I32(byte[] bytes, int at) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
    static ushort U16(byte[] bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
    sealed class EdgeRandom : Random
    {
        public int Calls { get; private set; }
        public bool Last { get; set; }
        public override long NextInt64(long maxValue)
        {
            Calls++;
            return Last ? maxValue - 1 : 0;
        }
    }
}
