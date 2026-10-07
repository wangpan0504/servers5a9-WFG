using DfoServer.Infrastructure;
using System.Buffers.Binary;
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
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class BoosterSelectionTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL booster selection: " + name);
        }
        void Reject(Action action, string name)
        {
            bool failed = false;
            try { action(); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or SqliteException) { failed = true; }
            Check(failed, name);
        }
        var catalog = itemResources.Items;
        var service = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces);
        var now = DateTimeOffset.UtcNow;
        var current = new Character(1, 0, "Selection", 0, towns.Room, towns.InitialReturn) { Job = 15 };
        var grouped = itemResources.Boosters.Get(8403).Selection!;
        Check(grouped.SelectionCount == 1 && grouped.Categories.Length == 28 &&
            grouped.Categories.Single(row => row.Category == 0 && row.Subcategory == 1).Rewards[0] == new BoosterChoice(100332, 1),
            "two PVF category coordinates remain distinct, not flattened or inferred from character job");
        var parsed = Parse("0 0\n[stackable]\n1106 7 1112 9\n[/stackable]\n[etc]\n1106 3\n[/etc]", 0);
        Check(parsed.Selection!.Categories[0].Rewards.SequenceEqual(new BoosterChoice[] { new(1106, 7), new(1112, 9), new(1106, 3) }),
            "selection rewards are item/count pairs and repeated resource rows remain intact");
        var hints = Parse("0 0\n[default select]\n2 1106 999999999\n[recommend]\n1 1112\n" +
            "[stackable]\n1106 7 1112 9\n[/stackable]\n[default select]\n1 1106\n[recommend]\n0", 2).Selection!.Categories[0];
        Check(hints.PendingRule is null && hints.DefaultItemIds.SequenceEqual(new[] { 1106, 999999999, 1106 }) &&
            hints.RecommendedItemIds.SequenceEqual(new[] { 1112 }) &&
            hints.Rewards.SequenceEqual(new BoosterChoice[] { new(1106, 7), new(1112, 9) }),
            "unclosed hint fields preserve IDs without adding rewards, replacing quantities or requiring current candidates");
        Check(Parse("0 0\n[default select]\n1 1106\n[etc]\n1106 7 1106 3\n[/etc]", 0)
            .Selection!.Categories[0].Rewards.SequenceEqual(new BoosterChoice[] { new(1106, 7), new(1106, 3) }),
            "full-group hint does not append another reward to repeated resource rows");
        foreach (string bad in new[] { "0 0\n[etc]\n1106\n[/etc]", "0 0\n[etc]\n1106 0\n[/etc]",
            "0 0\n[etc]\n-1 2\n[/etc]", "0 0\n[equipment]\n16400 1\n[/equipment]\n[avatar]\n1 1 1 1\n[/avatar]",
            "0 0\n[etc]\n1106 2\n[/etc]\n99", "0 0\n[default select]\n2 1106\n[etc]\n1106 2\n[/etc]",
            "0 0\n[recommend]\n1 1106 1112\n[etc]\n1106 2\n[/etc]", "0 0\n[default select]\n-1\n[etc]\n1106 2\n[/etc]",
            "0 0\n[recommend]\n1 x\n[etc]\n1106 2\n[/etc]", "0 0\n[default select]\n1 1106\n[/default select]",
            "0 0\n[stackable]\n1106 2\n[etc]\n1112 1\n[/etc]", "0 0\n[etc]\n1106 2\n[/stackable]",
            "0 0\n[default select]\n1 1106\n[booster equipment grade]\n6\n[equipment]\n16400 1\n[/equipment]" })
            Check(Parse(bad).Selection!.Categories[0].PendingRule is not null, "unknown or malformed category never grants a truncated subset");
        Check(!Parse("256 0\n[etc]\n1106 1\n[/etc]").Available && !Parse("0 0", -1).Available,
            "invalid category key and selection count reject definition");
        Check(!BoosterCatalog.Parse("[stackable type]\n`[booster selection]` 0\n[booster selection num]\n1\n" +
            "[booster select category]\n0 0\n[/booster select category]\n[booster select category]\n0 0\n[/booster select category]").Available,
            "duplicate category cannot replace an existing reward group");
        byte[] body = [3, 0, 1, 0, 0, 0, 2, 1, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), 1106);
        var request = BoosterRequests.Read(body);
        Check(request.Slot == 3 && request.Count == 1 && request.Selection is { Category: 2, Subcategory: 1 } &&
            request.Selection.ItemIds.SequenceEqual(new[] { 1106 }), "native 6-byte prefix, two category bytes, i32 ids and two zero option counts");
        foreach (int width in new[] { 7, 8, 9, 11, 12, 13, 15 })
            Reject(() => BoosterRequests.Read(new byte[width]), "truncated selection width " + width);
        body[^1] = 1;
        Check(!BoosterRequests.IsItemSelection(body), "extra detail options are not parsed as ordinary item selection");
        Reject(() => BoosterRequests.Read(body), "nonzero detail tail is not discarded");

        current = current with { Inventory = new([new(1, Bag(3), Core(10000743, 2) with { SortLockFlag = 1 }),
            new(2, Bag(65), Core(10000871, 5) with { SortLockFlag = 1 })], Gold: 12345) };
        var before = current.Inventory;
        var result = service.Open(current, Select(3, [10000871], count: 2), out var receipt, now);
        Check(result.Accepted && current.Inventory == before && result.Plan!.After.Find(Bag(3)) is null &&
            result.Plan.After.Find(Bag(65)) is { Uid: 2, Core.Value: 7, Core.SortLockFlag: 1 },
            "quickbar and sort lock permit use, identical reward merges without changing the existing UID");
        Check(receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(10000871, 2) }) && result.Plan!.After.Gold == 12345,
            "selected reward count comes from PVF times opened count, without extra price or material costs");
        foreach (var bad in new[] { Select(3, []), Select(3, [1106]), Select(3, [10000871, 10000871]),
            Select(3, [10000871, 10000872]), Select(3, [10000871], 1), Select(3, [10000871], 0, 1),
            Select(3, [10000871], count: 0), Select(3, [10000871], count: 3), new BoosterOpenRequest(3, 1) })
            Check(!service.Open(current, bad, out _).Accepted && current.Inventory == before, "invalid selection keeps all source items and assets");
        foreach (var core in new[] { Core(10000743) with { EquipmentLockId = 1 },
            Core(10000743) with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) }, Core(7542) })
            Check(!service.Open(current with { Inventory = new([new(1, Bag(3), core)]) }, Select(3, [10000871]), out _, now).Accepted,
                "safety lock, expiry and cross-kind request reject before consumption");

        var filled = Enumerable.Range(65, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), Core(1108, 1000))).ToImmutableArray();
        filled = filled.SetItem(0, new(65, Bag(65), Core(10000743)));
        current = current with { Inventory = new(filled) };
        result = service.Open(current, Select(65, [10000871]), out _);
        Check(result.Accepted && result.Plan!.After.Find(Bag(65)) is { Uid: 0, Core.ItemId: 10000871 } && result.Plan.After.Items.Length == 56,
            "full page uses the exhausted source slot rather than requiring an extra empty slot");
        current = current with { Inventory = new(filled.SetItem(0, filled[0] with { Core = Core(10000743, 2) })) };
        Check(!service.Open(current, Select(65, [10000871]), out _).Accepted && current.Inventory.Find(Bag(65))!.Core.Count == 2,
            "capacity failure retains source when its slot cannot be freed");
        current = current with { Inventory = new([new(1, Bag(3), Core(10012332))]) };
        result = service.Open(current, Select(3, [109000071]), out _);
        Check(result.Accepted && result.Plan!.After.Items.Single().Core == Core(109000071) && current.Level == 1 && current.Job == 15,
            "equipment keeps full seal, durability, quality and expiry without an output wear-level or job restriction");
        current = current with { Inventory = new([new(1, Bag(3), Core(8403))]) };
        Check(service.Open(current, Select(3, [100332], 0, 1), out _).Accepted &&
            !service.Open(current, Select(3, [100332], 0, 0), out _).Accepted &&
            !service.Open(current, Select(3, [100332], 3, 2), out _).Accepted, "correct second category succeeds; cross-category and empty groups reject");

        current = current with { Inventory = new([new(1, Bag(3), Core(10153913))]) };
        result = service.Open(current, Select(3, [100050655, 100100567, 100150535]), out receipt);
        Check(result.Accepted && receipt!.Rewards.Length == 3 && result.Plan!.After.Items.Length == 3, "multiple selections grant every chosen equipment");
        Check(service.Open(current, Select(3, [100050655]), out _).Accepted, "selection count is the resource maximum, not an added minimum");
        Check(!service.Open(current, Select(3, [100050655, 100100567, 100150535, 100200510]), out _).Accepted,
            "selection cannot exceed the resource quota");
        current = current with { Inventory = new([new(1, Bag(3), Core(10012510))]) };
        Check(service.Open(current, Select(3, [10012190]), out _).Accepted &&
            !service.Open(current, Select(3, [100350302]), out _).Accepted,
            "an unchosen missing template does not block a valid independent choice");
        result = service.Open(current, Select(3, [63220]), out _);
        Check(result.Accepted && result.Plan!.After.Items.Single() is { Core.ItemId: 63220, Core.Kind: ItemKind.Creature, Creature: not null },
            "a selected supported creature is granted with its independent detail and complete core");
        int[] all = [100300855, 100312548, 100322438, 100344654, 100352921];
        current = current with { Inventory = new([new(1, Bag(3), Core(10161354))]) };
        result = service.Open(current, Select(3, all.Reverse().ToArray()), out receipt);
        Check(result.Accepted && receipt!.Rewards.Select(row => row.ItemId).SequenceEqual(all) && result.Plan!.After.Items.Length == 5,
            "selection count zero grants all rewards in the selected group, independent of submitted order");
        Check(!service.Open(current, Select(3, all[..4]), out _).Accepted && !service.Open(current, Select(3, [.. all, all[0]]), out _).Accepted,
            "full-group echo cannot omit or duplicate rewards");
        current = current with { Inventory = new([new(1, Bag(3), Core(10012104))]) };
        result = service.Open(current, Select(3, [101000007]), out receipt);
        Check(result.Accepted && receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(101000007, 1) }) &&
            result.Plan!.After.Items.Single().Core == Core(101000007),
            "stale default ID outside the candidate list neither blocks a valid choice nor creates an extra item");
        Check(!service.Open(current, Select(3, [10000219]), out _).Accepted &&
            !service.Open(current, Select(3, [101000007, 10000219]), out _).Accepted,
            "default hint never authorizes an item outside the real reward list or an extra selection");
        current = current with { Inventory = new([new(1, Bag(3), Core(10002056))]) };
        result = service.Open(current, Select(3, [3290, 3311]), out receipt);
        Check(result.Accepted && receipt!.Rewards.SequenceEqual(new BoosterGrantedItem[] { new(3290, 88), new(3311, 88) }) &&
            result.Plan!.After.Items.Length == 2 && result.Plan.After.Items.All(item => item.Core.Count == 88),
            "multiple defaults use actual row quantities, allow fewer than the selection maximum and do not auto-grant unchosen expired defaults");
        Check(!service.Open(current, Select(3, [10002064]), out _).Accepted && current.Inventory.Items.Length == 1,
            "a default that is actually selected still needs a usable complete reward template");
        current = current with { Inventory = new([new(1, Bag(3), Core(2749783))]) };
        var historical = new DateTimeOffset(2013, 3, 1, 0, 0, 0, TimeSpan.Zero);
        result = service.Open(current, Select(3, [2749770]), out receipt, historical);
        Check(result.Accepted && receipt!.Rewards.Single() == new BoosterGrantedItem(2749770, 5) &&
            result.Plan!.After.Items.Single().Core.Count == 5,
            "default list leading count and repeated ID do not replace or add to the configured five-item reward");

        current = current with { Inventory = new(filled.SetItem(0, new(65, Bag(65), Core(10002056)))) };
        var materialPage = Enumerable.Range(121, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), Core(3290, int.MaxValue)));
        current = current with { Inventory = current.Inventory with { Items = current.Inventory.Items.AddRange(materialPage) } };
        before = current.Inventory;
        Check(!service.Open(current, Select(65, [3311]), out _).Accepted && current.Inventory == before,
            "default selection still checks real output capacity and keeps the source and every existing instance on failure");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("selection-test", () => { });
            repo.Create(owner, new("SelectOne", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(3), Core(10161354)), new(0, Bag(4), Core(10000743, 2))])
            });
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            var saved = session.Selected!;
            string snapshot = Snapshot();
            Query("CREATE TRIGGER fail_selection BEFORE INSERT ON character_inventory_slots WHEN NEW.slot_index=10 BEGIN SELECT RAISE(ABORT,'selection rollback'); END");
            Reject(() => session.MutateInventory(role => service.Open(role, Select(3, all), out _)), "later reward insert failure propagates");
            Check(session.Selected == saved && Snapshot() == snapshot, "source, already-written rewards, revisions, audit and UID sequence roll back together");
            Query("DROP TRIGGER fail_selection");
            var stalePlan = service.Open(saved, Select(4, [10000871]), out _).Plan!;
            Check(session.MutateInventory(role => service.Open(role, Select(3, all), out _)).Accepted, "complete all-group plan commits once");
            Reject(() => repo.CommitInventory(owner, saved, stalePlan), "stale revision cannot commit a selection computed before another asset change");
            Check(session.MutateInventory(role => service.Open(role, Select(4, [10000871], count: 2), out _)).Accepted, "batch selection commits through the shared transaction");
            snapshot = Snapshot();
            Check(!session.MutateInventory(role => service.Open(role, Select(3, all), out _)).Accepted && Snapshot() == snapshot,
                "exhausted request replay creates no extra reward or audit");
            Check(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(session.Selected),
                "repository reopen retains complete equipment and consumable rewards");
            Check(Query("SELECT COUNT(DISTINCT item_uid) FROM character_inventory_items") is 6L, "every final physical instance has a unique persisted UID");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.MutateInventory(role => service.Open(role, Select(4, [10000871]), out _)), "old generation cannot open or replay a selection");
            Check(Query("PRAGMA integrity_check") is "ok", "selection database integrity");

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
        Console.WriteLine($"PASS {checks} booster selection assertions");
        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(catalog.Get(id), count, now);
    }

    static BoosterDefinition Parse(string category, int count = 1) => BoosterCatalog.Parse("[stackable type]\n`[booster selection]` 0\n" +
        "[booster selection num]\n" + count + "\n[booster select category]\n" + category + "\n[/booster select category]");
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static BoosterOpenRequest Select(short slot, int[] ids, byte category = 0, byte subcategory = 0, int count = 1) =>
        new(slot, count, new(category, subcategory, ids));
}
