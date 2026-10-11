using static DfoServer.Testing.TestCheck;
using DfoServer.Infrastructure;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Settings;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class BoosterEquipmentTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = Count;
        void Reject(Action action, string name)
        {
            bool failed = false;
            try { action(); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or SqliteException) { failed = true; }
            That(failed, name);
        }
        var catalog = itemResources.Items;
        var now = DateTimeOffset.UtcNow;
        var random = new PlannedRandom();
        var service = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces, random);
        var current = new Character(1, 0, "EquipmentBox", 0, towns.Room, towns.InitialReturn) { Job = 15 };
        var rows = Parse("[equipment]\n16000 1\n[/equipment]\n[booster equipment upgrade]\n12\n" +
            "[booster equipment separate]\n5\n[equipment]\n16400 2\n[/equipment]\n[stackable]\n1106 9\n[/stackable]\n" +
            "[booster equipment upgrade]\n0\n[equipment]\n16000 3\n[/equipment]").Rewards;
        That(rows.SequenceEqual(new BoosterChoice[] { new(16000, 1), new(16400, 2, 12, 5), new(1106, 9), new(16000, 3, 0, 5) }),
            "modifiers apply only to following equipment rows and zero explicitly resets upgrade");
        var definition = BoosterCatalog.Parse("[stackable type]\n`[booster selection]` 0\n[booster selection num]\n0\n" +
            "[booster select category]\n0 0\n[booster equipment upgrade]\n31\n[booster equipment separate]\n127\n" +
            "[equipment]\n16000 1\n[/equipment]\n[/booster select category]\n" +
            "[booster select category]\n1 0\n[equipment]\n16000 1\n[/equipment]\n[/booster select category]");
        That(definition.Selection!.Categories[0].Rewards[0] is { Upgrade: 31, Refinement: 127 } &&
            definition.Selection.Categories[1].Rewards[0] == new BoosterChoice(16000, 1),
            "representable levels are accepted and category state starts from zero");
        var chances = Parse("[booster equipment upgrade]\n255\n[random probability]\n0 0 7 5\n[/random probability]\n" +
            "[equipment]\n16000 1\n[/equipment]\n[random probability]\n12 9\n[/random probability]\n" +
            "[equipment]\n16400 1\n[/equipment]").Rewards;
        That(chances[0].UpgradeChances.SequenceEqual(new BoosterUpgradeChance[] { new(0, 0), new(7, 5) }) &&
            chances[1].UpgradeChances.SequenceEqual(new BoosterUpgradeChance[] { new(0, 0), new(7, 5), new(12, 9) }),
            "probability vectors preserve zero weights and each reward snapshots its declared prefix");
        foreach (string rule in new[] { "[booster equipment upgrade]\n32", "[booster equipment upgrade]\n-1",
            "[booster equipment upgrade]\n7 8", "[booster equipment separate]\n128", "[booster equipment separate]\nx",
            "[random probability]\n7 100\n[/random probability]", "[booster equipment upgrade]\n255",
            "[booster equipment upgrade]\n255\n[random probability]\n7 0\n[/random probability]",
            "[booster equipment upgrade]\n255\n[random probability]\n32 1\n[/random probability]",
            "[booster equipment upgrade]\n255\n[random probability]\n7 -1\n[/random probability]",
            "[booster equipment upgrade]\n255\n[random probability]\n7 1 8\n[/random probability]",
            "[booster equipment upgrade]\n255\n[random probability]\n7 1", "[booster equipment grade]\n6" })
            That(Parse(rule + "\n[equipment]\n16000 1\n[/equipment]").PendingRule is not null,
                "unknown grade or malformed equipment modifier keeps the whole category pending");

        current = current with { Inventory = new([new(1, Bag(3), Core(10005273) with { SortLockFlag = 1 })], Gold: 12345) };
        var result = service.Open(current, Select(3, 401000005), out var receipt, now);
        That(result.Accepted && result.Plan!.After.Items.Single().Core == Core(401000005) with { Attr = 7, GenuineUpgrade = 3 },
            "actual PVF reward keeps complete template, 60-day expiry, reinforcement and refinement in separate fields");
        That(result.Plan!.After.Gold == 12345 && receipt!.Rewards.Single() == new BoosterGrantedItem(401000005, 1) &&
            current.Level == 1 && current.Job == 15, "no added price, reward job or wear-level constraint");
        var debugSettings = new GameDebugSettings();
        var debugRandom = new PlannedRandom();
        var debugService = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits,
            itemResources.EpicPieces, debugRandom, debugSettings);
        var debugOpen = debugService.Open(current, Select(3, 401000005), out _, now);
        That(debugOpen.Accepted && debugOpen.Plan!.After.Items.Single().Core is { ExpireTime: int.MaxValue, Attr: 7, GenuineUpgrade: 3 },
            "礼包内部60天装备在调试开启时统一2038期限，原装备属性保持");
        debugSettings.IgnoreCashShopAbsoluteExpiration = false;
        var normalOpen = debugService.Open(current, Select(3, 401000005), out _, now);
        That(normalOpen.Accepted && normalOpen.Plan!.After.Items.Single().Core.ExpireTime == Core(401000005).ExpireTime &&
            debugOpen.Plan!.After.Items.Single().Core.ExpireTime == int.MaxValue, "关闭恢复内部奖励相对期限，不改写已规划实例");
        debugSettings.IgnoreCashShopAbsoluteExpiration = true;
        debugRandom.Values.Enqueue(0);
        debugRandom.BeforeDraw = () => debugSettings.IgnoreCashShopAbsoluteExpiration = false;
        var randomSource = current with { Inventory = new([new(1, Bag(3), Core(10014751))]) };
        debugOpen = debugService.Open(randomSource, Select(3, 16000), out _, now);
        That(debugOpen.Accepted && debugOpen.Plan!.After.Items.Single().Core.ExpireTime == int.MaxValue &&
            !debugSettings.IgnoreCashShopAbsoluteExpiration, "抽取期间切换GM时，整次开箱仍使用入口冻结的期限状态");
        current = current with { Inventory = new([new(1, Bag(3), Core(10014751, 2))]) };
        var source = current;
        random.Values.Enqueue(0);
        random.Values.Enqueue(99);
        result = service.Open(current, Select(3, 16000, 2), out receipt, now);
        That(result.Accepted && result.Plan!.After.Items.Select(item => item.Core.Attr).SequenceEqual(new byte[] { 1, 12 }) &&
            result.Plan.After.Items.All(item => item.Core.GenuineUpgrade == 0 && item.Core.EnchantUpgrade == 0),
            "each instance in a batch independently rolls reinforcement and never writes the enchant-card field");
        That(receipt!.Rewards.Single() == new BoosterGrantedItem(16000, 2) && result.Plan!.After.Items.All(item => item.Uid == 0),
            "receipt aggregates only counts while inventory retains distinct attributes and defers UID allocation");
        int offset = 0;
        int[] weights = [5, 5, 5, 5, 9, 9, 9, 9, 20, 15, 6, 3];
        for (byte level = 1; level <= weights.Length; level++)
        {
            random.Values.Enqueue(offset);
            random.Values.Enqueue(offset + weights[level - 1] - 1);
            result = service.Open(current, Select(3, 16000, 2), out _, now);
            That(result.Accepted && result.Plan!.After.Items.All(item => item.Core.Attr == level),
                "original probability interval includes both edges of level " + level);
            offset += weights[level - 1];
        }
        That(offset == 100 && random.Values.Count == 0, "all real PVF weights participate without a filtered pool");
        var occupied = Enumerable.Range(9, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), Core(16000))).ToImmutableArray();
        current = source with { Inventory = new(source.Inventory.Items.AddRange(occupied)) };
        int calls = random.Calls;
        That(!service.Open(current, Select(3, 16000, 2), out _).Accepted && random.Calls == calls &&
            current.Inventory.Find(Bag(3))!.Core.Count == 2, "full equipment page retains the entire batch before random draws");
        current = current with { Inventory = current.Inventory with { Items = current.Inventory.Items.RemoveAt(1) } };
        That(!service.Open(current, Select(3, 16000, 2), out _).Accepted && random.Calls == calls,
            "one available slot cannot partially deliver two equipment rewards");
        random.Values.Enqueue(9);
        result = service.Open(current, Select(3, 16000), out _, now);
        That(result.Accepted && result.Plan!.After.Find(Bag(9))!.Core.Attr == 2 && result.Plan.After.Find(Bag(3))!.Core.Count == 1,
            "exact available capacity succeeds without reserving extra slots");

        var empty = source with { Inventory = new([]) };
        foreach (var reward in new InventoryReward[] { new(1106, 1, 7), new(1, 1, 7), new(16000, 1, 32), new(16000, 1, 0, 128) })
            That(!InventoryRewardGrantService.TryGrantBatch(empty, [reward], catalog, itemResources.GoldLimits, itemResources.EpicPieces, now).Accepted,
                "shared grant rejects attributes for non-equipment or outside native field widths");
        result = InventoryRewardGrantService.TryGrantBatch(empty, [new(16000, 2, 31, 127)], catalog, itemResources.GoldLimits, itemResources.EpicPieces, now);
        That(result.Accepted && result.Plan!.After.Items.All(item => item.Core is { Attr: 31, Upgrade: 31, GenuineUpgrade: 127, EnchantUpgrade: 0 }),
            "shared grant supports every representable level and distinct equipment instances");
        var combined = Core(16000) with { Attr = 0xE7, EnchantUpgrade = 11 };
        That(combined.Upgrade == 7 && !JsonSerializer.Serialize(combined).Contains("\"Upgrade\":"),
            "trade-count high bits and enchant upgrades do not alter reinforcement or persisted schema");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-equipment-booster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("equipment-booster", () => { });
            repo.Create(owner, new("EqRewards", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(3), Core(10014751, 2)), new(0, Bag(4), Core(10005273))])
            });
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            var saved = session.Selected!;
            var stale = service.Open(saved, Select(4, 401000005), out _, now).Plan!;
            string before = Snapshot();
            Query("CREATE TRIGGER fail_equipment_booster BEFORE INSERT ON character_inventory_slots WHEN NEW.slot_index=10 BEGIN SELECT RAISE(ABORT,'equipment booster rollback'); END");
            random.Values.Enqueue(0);
            random.Values.Enqueue(99);
            Reject(() => session.MutateInventory(role => service.Open(role, Select(3, 16000, 2), out _, now)), "later equipment write failure propagates");
            That(session.Selected == saved && Snapshot() == before && random.Values.Count == 0,
                "failed commit rolls back source, rewards, UID sequence, revision and audit without rerolling");
            Query("DROP TRIGGER fail_equipment_booster");
            random.Values.Enqueue(5);
            random.Values.Enqueue(97);
            That(session.MutateInventory(role => service.Open(role, Select(3, 16000, 2), out _, now)).Accepted,
                "new opening commits all resolved per-instance levels");
            Reject(() => repo.CommitInventory(owner, saved, stale), "stale plan cannot commit after random rewards");
            That(session.MutateInventory(role => service.Open(role, Select(4, 401000005), out _, now)).Accepted,
                "fixed reinforcement and refinement use the same commit path");
            before = Snapshot();
            calls = random.Calls;
            That(!session.MutateInventory(role => service.Open(role, Select(3, 16000, 2), out _)).Accepted && Snapshot() == before && random.Calls == calls,
                "exhausted replay neither grants nor rerolls");
            var reopened = new CharacterRepository(path).List(owner).Single();
            That(JsonSerializer.Serialize(reopened) == JsonSerializer.Serialize(session.Selected) &&
                reopened.Inventory.Items.Select(item => item.Core.Attr).SequenceEqual(new byte[] { 2, 12, 7 }) &&
                reopened.Inventory.Find(Bag(11))!.Core.GenuineUpgrade == 3,
                "repository reopen preserves exact levels, original expiry and full cores");
            That(reopened.Inventory.Items.Select(item => item.Uid).Distinct().Count() == 3 &&
                reopened.Inventory.Items.All(item => item.Uid > 0), "all rewards receive unique persisted instance identities");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.MutateInventory(role => service.Open(role, Select(4, 401000005), out _)), "old generation cannot deliver equipment rewards");
            That(Query("PRAGMA integrity_check") is "ok", "equipment rewards preserve database integrity");

            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Query("SELECT count(*) FROM inventory_audit_log") + ":" +
                Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} booster equipment assertions");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(catalog.Get(id), count, now);
    }

    static BoosterSelectionCategory Parse(string body) => BoosterCatalog.Parse("[stackable type]\n`[booster selection]` 0\n" +
        "[booster selection num]\n0\n[booster select category]\n0 0\n" + body + "\n[/booster select category]").Selection!.Categories[0];
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static BoosterOpenRequest Select(short slot, int id, int count = 1) => new(slot, count, new(0, 0, [id]));
    sealed class PlannedRandom : Random
    {
        public Queue<long> Values { get; } = new();
        public int Calls { get; private set; }
        public Action? BeforeDraw { get; set; }
        public override long NextInt64(long maxValue)
        {
            Calls++;
            BeforeDraw?.Invoke();
            long value = Values.Dequeue();
            if (value < 0 || value >= maxValue)
                throw new InvalidOperationException("随机强化用例超出真实权重范围");
            return value;
        }
    }
}
