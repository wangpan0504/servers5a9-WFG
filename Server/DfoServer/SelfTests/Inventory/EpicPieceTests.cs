using static DfoServer.Testing.TestCheck;
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

internal static class EpicPieceTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = Count;
        void Reject(Action action, string name)
        {
            bool failed = false;
            try { action(); }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or SqliteException) { failed = true; }
            That(failed, name);
        }
        var catalog = itemResources.Items;
        var pieces = itemResources.EpicPieces;
        var craft = new EpicPieceCraftService(catalog, pieces, itemResources.GoldLimits);
        var first = pieces.Entries[0];
        That(first == new EpicPieceEntry(0, 450004, 10300000) && pieces.Entries[1].PieceId == 10300001, "matched PVF book keeps original order");
        That(pieces.Recipes[450004].Costs.SequenceEqual(new ItemMaterialCost[] { new(10300000, 1000), new(10099775, 22), new(10158124, 12) }), "matched epic recipe costs");
        That(itemResources.Shops.Shops.Values.All(shop => !shop.Items.Any(pieces.ByPiece.ContainsKey)), "matched ordinary shops do not sell book pieces");
        That(pieces.Recipes.Values.All(recipe => catalog.Get(recipe.EquipmentId).Kind == ItemKind.Equipment), "every PVF recipe creates an equipment item");
        var current = new Character(1, 0, "Epic", 0, towns.Room, towns.InitialReturn) { Job = 15 };
        foreach (var recipe in pieces.Recipes.Values)
        {
            var available = InventoryRewardGrantService.TryGrantBatch(current, recipe.Costs.Select(cost => new InventoryReward(cost.ItemId, cost.Count)).ToArray(), catalog, itemResources.GoldLimits, itemResources.EpicPieces);
            var made = available.Accepted ? craft.Craft(current with { Inventory = available.Plan!.After }, recipe.EquipmentId) : available;
            That(made.Accepted && made.Plan!.After.Items.Single().Core.ItemId == recipe.EquipmentId &&
                made.Plan.After.EpicPieces.Counts.All(count => count == 0), "complete matched recipe can be crafted: " + recipe.EquipmentId);
        }
        var multiple = catalog.Get(101000783).ShopPrice;
        That(multiple.Materials.SequenceEqual(new ItemMaterialCost[] { new(10305616, 600), new(10158964, 15) }),
            "multi-material shop metadata remains complete without blocking epic equipment creation");
        var coat = InventoryRewardGrantService.Create(catalog.Get(10400));
        var full = Enumerable.Range(9, 56).Select(slot => new InventoryItem(slot, Bag((short)slot), coat)).ToImmutableArray();
        current = current with { Inventory = new(full) };
        var grant = InventoryRewardGrantService.TryGrantBatch(current, [new(10300000, 1000), new(10300000, 1000), new(10300001, 7)], catalog, itemResources.GoldLimits, itemResources.EpicPieces);
        That(grant.Accepted && grant.Plan!.After.Items.SequenceEqual(full) && grant.Plan.ChangedSlots.Length == 0, "pieces never need slots or ItemCore instances");
        That(grant.Plan!.After.EpicPieces.Get(0) == 2000 && grant.Plan.After.EpicPieces.Get(1) == 7 && grant.Plan.ChangedEpicPieces.SequenceEqual(new[] { 0, 1 }), "piece batches accumulate in account book");
        current = current with { Inventory = grant.Plan.After };
        var overflow = current with { Inventory = current.Inventory with { EpicPieces = current.Inventory.EpicPieces.Set(pieces, 0, int.MaxValue) } };
        That(!InventoryRewardGrantService.TryGrantBatch(overflow, [new(10300001, 1), new(10300000, 1)], catalog, itemResources.GoldLimits, itemResources.EpicPieces).Accepted && overflow.Inventory.EpicPieces.Get(1) == 7,
            "piece overflow rejects the entire batch without clipping or partial rewards");
        That(!InventoryRewardGrantService.TryGrantBatch(current, [new(10300001, 1), new(10400, 1)], catalog, itemResources.GoldLimits, itemResources.EpicPieces).Accepted && current.Inventory.EpicPieces.Get(1) == 7,
            "physical overflow also rolls back earlier planned piece grants");
        var funded = current.Inventory with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.LegendarySoul, 44).Set(CurrencyKind.EpicSoul, 24) };
        That(!craft.Craft(current with { Inventory = funded }, 450004).Accepted, "full equipment page preserves all recipe assets");
        current = current with { Inventory = funded with { Items = [] } };
        var result = craft.Craft(current, 450004);
        That(result.Accepted && current.Level == 1 && current.Job == 15, "crafting is not limited to output level or job");
        var created = result.Plan!.After.Items.Single();
        That(created.Position == Bag(9) && created.Uid == 0 && created.Core == InventoryRewardGrantService.Create(catalog.Get(450004)), "craft uses unified complete core creation");
        That(result.Plan.After.EpicPieces.Get(0) == 1000 && result.Plan.After.EpicPieces.Get(1) == 7 &&
            result.Plan.After.AccountWallet.Get(CurrencyKind.LegendarySoul) == 22 && result.Plan.After.AccountWallet.Get(CurrencyKind.EpicSoul) == 12,
            "craft spends exact fragment and both soul costs");
        That(!craft.Craft(current, -1).Accepted && !craft.Craft(current, 10300000).Accepted && !craft.Craft(current with { Inventory = InventorySnapshot.Empty }, 450004).Accepted,
            "unknown recipe and insufficient book reject without mutation");
        foreach (var kind in new[] { CurrencyKind.LegendarySoul, CurrencyKind.EpicSoul })
            That(!craft.Craft(current with { Inventory = current.Inventory with { AccountWallet = current.Inventory.AccountWallet.Set(kind, 0) } }, 450004).Accepted,
                "missing recipe currency rejects: " + kind);

        var empty = EpicBookPackets.Info(EpicPieceBook.Empty, pieces);
        That(empty.SequenceEqual(new byte[5]), "empty full notification explicitly clears prior account book");
        var packet = EpicBookPackets.Info(current.Inventory.EpicPieces, pieces);
        That(packet.Length == 21 && packet[0] == 0 && I32(packet, 1) == 2 && I32(packet, 5) == 10300000 && I32(packet, 9) == 2000 && I32(packet, 13) == 10300001 && I32(packet, 17) == 7,
            "full notification has i32 count and ordered item/absolute-balance pairs");
        packet = EpicBookPackets.Info(current.Inventory.EpicPieces.Set(pieces, 0, 0), pieces, [0]);
        That(packet.Length == 13 && packet[0] == 1 && I32(packet, 1) == 1 && I32(packet, 5) == 10300000 && I32(packet, 9) == 0, "delta includes exhausted balance zero");
        That(EpicBookPackets.Crafted(new(true, 0)).SequenceEqual(new byte[] { 1 }) &&
            EpicBookPackets.Crafted(InventoryResult.Reject(4)).SequenceEqual(new byte[] { 0, 4, 0 }), "craft success and u16 failure widths");
        That(EpicBookRequests.Craft([0xd4, 0xdd, 6, 0]) == 450004, "craft request is one i32 equipment ID");
        Reject(() => EpicBookRequests.Craft(new byte[3]), "truncated craft request rejected");
        Reject(() => EpicBookRequests.Craft(new byte[5]), "trailing craft bytes rejected");
        Reject(() => (current.Inventory.EpicPieces with { OrderHash = new string('0', 64) }).Validate(pieces), "changed catalog order cannot reinterpret saved balances");
        Reject(() => (current.Inventory.EpicPieces with { Counts = [1] }).Validate(pieces), "truncated stored count array rejected");
        Reject(() => (current.Inventory.EpicPieces with { Counts = [-1] }).Validate(), "negative stored balance rejected");

        // 配方夹具覆盖普通材料、装备材料和扣除后腾出的格子，不改运行PVF。
        var materials = new EpicPieceCatalog(Recipe("10300000 10 3167 2 3167 2 10400 1"));
        var materialCraft = new EpicPieceCraftService(catalog, materials, itemResources.GoldLimits);
        var book = EpicPieceBook.Empty.Set(materials, 0, 10);
        var stack = InventoryRewardGrantService.Create(catalog.Get(3167), 2) with { SortLockFlag = 1 };
        var materialInput = current with { Inventory = new(full.Add(new(100, Bag(3), stack)).Add(new(101, Bag(121), stack))) { EpicPieces = book } };
        result = materialCraft.Craft(materialInput, 450004);
        That(result.Accepted && result.Plan!.After.Find(Bag(9))!.Core.ItemId == 450004 && result.Plan.After.Find(Bag(3)) is null && result.Plan.After.Find(Bag(121)) is null,
            "combined duplicate recipe costs span quickbar/page and reuse consumed equipment slot");
        That(result.Plan!.After.Items.Length == 56 && result.Plan.After.EpicPieces.Get(0) == 0, "craft has no arbitrary extra-free-slot requirement");
        foreach (var blocked in new[] { stack with { EquipmentLockId = 1 }, stack with { ExpireTime = 1 } })
        {
            var input = materialInput with { Inventory = materialInput.Inventory with { Items = materialInput.Inventory.Items.Replace(materialInput.Inventory.Find(Bag(3))!, new(100, Bag(3), blocked)) } };
            That(!materialCraft.Craft(input, 450004).Accepted, "locked or expired recipe items retained");
        }
        var cargoOnly = materialInput with { Inventory = materialInput.Inventory with { Items = materialInput.Inventory.Items.Replace(materialInput.Inventory.Find(Bag(3))!, new(100, new(InventoryListType.PersonalCargo, 0), stack)) } };
        That(!materialCraft.Craft(cargoOnly, 450004).Accepted, "craft does not silently take materials from storage");
        Reject(() => new EpicPieceCatalog(Recipe("10300001 10")), "recipe fragment must match output mapping");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-epic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("epic", () => { });
            current = repo.Create(owner, new("EpicOne", towns.Room, towns.InitialReturn));
            That(current.Inventory.EpicPieces.Same(EpicPieceBook.Empty), "new account starts with no fragments");
            grant = InventoryRewardGrantService.TryGrantBatch(current, [new(10300000, 2000), new(10300001, 7), new(10099775, 44), new(10158124, 24)], catalog, itemResources.GoldLimits, itemResources.EpicPieces);
            current = InventoryService.Commit(repo, owner, current, grant);
            That(current.Inventory.EpicPieces.Revision == 1 && Query(path, "SELECT count(*) FROM character_inventory_items") is 0L, "fragment grants persist no physical instance");
            var sibling = repo.Create(owner, new("EpicTwo", towns.Room, towns.InitialReturn));
            That(sibling.Inventory.EpicPieces.Same(current.Inventory.EpicPieces) && Query(path, "SELECT count(*) FROM epic_piece_audit_log") is 2L, "new character inherits shared book without regrant or audit");
            using var other = owners.Acquire("other_epic", () => { });
            That(repo.Create(other, new("EpicOther", towns.Room, towns.InitialReturn)).Inventory.EpicPieces.Same(EpicPieceBook.Empty), "other account remains isolated");
            Reject(() => repo.Create(owner, new("EpicInvalid", towns.Room, towns.InitialReturn) { Inventory = new([]) { EpicPieces = current.Inventory.EpicPieces } }), "creation cannot overwrite shared book");
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            That(!session.MutateInventory(character => craft.Craft(character, 450004), 4).Accepted, "not-ready session cannot craft");
            session.MarkReady();
            current = session.Selected!;
            string before = Snapshot();
            Query(path, "CREATE TRIGGER fail_epic BEFORE INSERT ON epic_piece_audit_log BEGIN SELECT RAISE(ABORT,'epic rollback'); END");
            Reject(() => session.MutateInventory(character => craft.Craft(character, 450004)), "audit failure rejects before success");
            That(before == Snapshot() && session.Selected == current, "failure rolls back pieces, soul wallet, equipment UID, revisions, audits and online state");
            Query(path, "DROP TRIGGER fail_epic");
            var stale = current;
            var plan = craft.Craft(stale, 450004).Plan!;
            result = session.MutateInventory(character => craft.Craft(character, 450004));
            current = session.Selected!;
            That(result.Accepted && current.Inventory.Items.Single().Uid > 0 && current.Inventory.EpicPieces.Get(0) == 1000 && current.Inventory.EpicPieces.Revision == 2, "committed craft allocates one equipment UID");
            Reject(() => repo.CommitInventory(owner, stale, plan), "same character stale revision cannot craft twice");
            Reject(() => repo.CommitInventory(owner, sibling, craft.Craft(sibling, 450004).Plan!), "another character stale shared book cannot overwrite account balance");
            That(session.MutateInventory(character => craft.Craft(character, 450004)).Accepted && session.Selected!.Inventory.EpicPieces.Get(0) == 0,
                "another valid request consumes another complete recipe");
            before = Snapshot();
            That(!session.MutateInventory(character => craft.Craft(character, 450004)).Accepted && before == Snapshot(), "exhausted recipe replay has no effects");
            current = session.Selected!;
            That(current.Inventory.Items.Select(item => item.Uid).Distinct().Count() == 2 && current.Inventory.Items.All(item => item.Core.ItemId == 450004), "crafted instances retain independent UIDs");
            That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).First()) == JsonSerializer.Serialize(current), "reopen restores exact shared book, wallet and complete equipment cores");
            var forged = current.Inventory with { EpicPieces = current.Inventory.EpicPieces with { Revision = 99 } };
            Reject(() => repo.CommitInventory(owner, current, new(current.Inventory, forged, "invalid")), "business cannot forge account book revision");
            Query(path, "UPDATE account_epic_books SET counts=json_set(counts,'$[0]',1) WHERE account='epic'");
            Reject(() => repo.CommitInventory(owner, current, new(current.Inventory, current.Inventory with { Gold = 1 }, "stale_external")), "external count edit without revision is detected");
            Query(path, "UPDATE account_epic_books SET counts=json_set(counts,'$[0]',0) WHERE account='epic'");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.MutateInventory(character => craft.Craft(character, 450004)), "old generation cannot mutate book");
            That(Query(path, "PRAGMA integrity_check") is "ok", "book database integrity");

            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Query(path, "SELECT count(*) FROM inventory_audit_log") + ":" +
                Query(path, "SELECT count(*) FROM currency_audit_log") + ":" + Query(path, "SELECT count(*) FROM epic_piece_audit_log") + ":" +
                Query(path, "SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} epic book assertions; pieces={pieces.Entries.Length}, recipes={pieces.Recipes.Count}");
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    static object? Query(string path, string sql)
    {
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
    static string Recipe(string costs) => "[piece list]\n450004 10300000\n[/piece list]\n[equipment recipe info]\n[info]\n[output]\n450004\n[recipe]\n" + costs + "\n[/recipe]\n[/info]\n[/equipment recipe info]";
}
