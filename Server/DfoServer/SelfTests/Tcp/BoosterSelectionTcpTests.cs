using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.BoosterTcpTests;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class BoosterSelectionTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "booster-selection"));
        var characters = resources.Characters;
        var towns = resources.Towns;
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repo.Create(owner, new("SelectTcp", towns.Room, towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = characters.Get(15).Skills,
                Inventory = new([new(0, Bag(3), Core(10000743, 2)), new(0, Bag(4), Core(10012332)),
                    new(0, Bag(5), Core(8403)), new(0, Bag(6), Core(10161354)), new(0, Bag(7), Core(10304530)),
                    new(0, Bag(8), Core(10012104)), new(0, Bag(65), Core(10000743)), new(0, Bag(67), Core(10002056))], Gold: 12345)
            });
        }
        int[] all = [100300855, 100312548, 100322438, 100344654, 100352921];
        int[] imported = [100050791, 100100692, 100150661];
        byte[][] importedEntries = [];
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            SendSelect(peer, 3, [10000871]);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Select();
            string before = Snapshot();
            foreach (var ids in new int[][] { [], [1106], [10000871, 10000871], [10000871, 10000872] })
            {
                SendSelect(peer, 3, ids);
                peer.Expect(1, 160, [0, 19, 0]);
            }
            SendSelect(peer, 3, [10000871], 1);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Send(160, [3, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]);
            peer.Barrier();
            That(Snapshot() == before, "bad choices and unsupported detail tail do not consume the source or report success");
            for (int i = 1; i <= 2; i++)
            {
                SendSelect(peer, 3, [10000871]);
                ExpectAck(peer, 10000743, 3, [(10000871, 1)]);
                byte[] delta = peer.Expect(0, 14).Body;
                Entry(delta, 3, i == 1 ? 10000743 : -1, i == 1 ? 1 : 0);
                Entry(delta, 66, 10000871, i);
            }
            SendSelect(peer, 65, [10000872]);
            ExpectAck(peer, 10000743, 65, [(10000872, 1)]);
            byte[] clear = peer.Expect(0, 14).Body;
            That(U16(clear, 1) == 1, "source-slot template replacement clears exactly the old source");
            Entry(clear, 65, -1, 0);
            Entry(peer.Expect(0, 14).Body, 65, 10000872, 1);
            SendSelect(peer, 4, [109000071]);
            ExpectAck(peer, 10012332, 4, [(109000071, 1)]);
            byte[] equipment = peer.Expect(0, 14).Body;
            Entry(equipment, 4, -1, 0);
            Entry(equipment, 9, 109000071, InventoryRewardGrantService.InitialQuality);
            SendSelect(peer, 5, [100332], 0, 0);
            peer.Expect(1, 160, [0, 19, 0]);
            SendSelect(peer, 5, [100332], 0, 1);
            ExpectAck(peer, 8403, 5, [(100332, 1)]);
            byte[] category = peer.Expect(0, 14).Body;
            Entry(category, 5, -1, 0);
            Entry(category, 10, 100332, InventoryRewardGrantService.InitialQuality);
            SendSelect(peer, 6, all[..4]);
            peer.Expect(1, 160, [0, 19, 0]);
            SendSelect(peer, 6, all.Reverse().ToArray());
            ExpectAck(peer, 10161354, 6, all.Select(id => (id, 1)).ToArray());
            byte[] whole = peer.Expect(0, 14).Body;
            That(U16(whole, 1) == 6, "whole group projects the source and all five outputs after ACK");
            Entry(whole, 6, -1, 0);
            for (int i = 0; i < all.Length; i++)
                Entry(whole, (short)(11 + i), all[i], InventoryRewardGrantService.InitialQuality);
            long earliestExpiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 14 * 86400;
            SendSelect(peer, 7, imported);
            ExpectAck(peer, 10304530, 7, imported.Select(id => (id, 1)).ToArray());
            byte[] temporary = peer.Expect(0, 14).Body;
            Entry(temporary, 7, -1, 0);
            importedEntries = imported.Select((id, index) =>
            {
                short slot = (short)(16 + index);
                Entry(temporary, slot, id, InventoryRewardGrantService.InitialQuality);
                byte[] entry = EquipmentEntry(temporary, slot);
                That(U16(entry, 11) == itemResources.Items.Get(id).Durability && entry[13] == 0 &&
                    U32(entry, 56) >= earliestExpiry && U32(entry, 56) <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 14 * 86400,
                    "selected imported equipment projects inherited durability and its own temporary expiry");
                return entry;
            }).ToArray();
            That(importedEntries.Select(entry => U32(entry, 56)).Distinct().Count() == 1,
                "one opening gives all imported rewards the same creation-time expiry");
            before = Snapshot();
            SendSelect(peer, 8, [10000219]);
            peer.Expect(1, 160, [0, 19, 0]);
            SendSelect(peer, 67, [10002064]);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, "default hints do not authorize missing candidates or expired selected rewards");
            SendSelect(peer, 8, [101000007]);
            ExpectAck(peer, 10012104, 8, [(101000007, 1)]);
            byte[] defaultEquipment = peer.Expect(0, 14).Body;
            That(U16(defaultEquipment, 1) == 2, "stale default causes neither an extra reward nor an extra projection");
            Entry(defaultEquipment, 8, -1, 0);
            Entry(defaultEquipment, 19, 101000007, InventoryRewardGrantService.InitialQuality);
            SendSelect(peer, 67, [3290, 3311]);
            ExpectAck(peer, 10002056, 67, [(3290, 88), (3311, 88)]);
            byte[] defaultMaterials = peer.Expect(0, 14).Body;
            That(U16(defaultMaterials, 1) == 3, "defaults use real row quantities without automatically granting unchosen entries");
            Entry(defaultMaterials, 67, -1, 0);
            Entry(defaultMaterials, 121, 3290, 88);
            Entry(defaultMaterials, 122, 3311, 88);
            before = Snapshot();
            SendSelect(peer, 6, all);
            peer.Expect(1, 160, [0, 19, 0]);
            peer.Barrier();
            That(before == Snapshot(), "replay of consumed selection cannot grant again");
            That(fixture.Query("SELECT count(*),count(DISTINCT item_uid) FROM character_inventory_items") is [[15L, 15L]],
                "four stackable and eleven complete equipment instances persist once");
        }
        await fixture.StopAsync();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single();
            That(current.Inventory.Find(Bag(9))!.Core == Core(109000071), "full equipment core persists beyond the network projection");
            That(current.Inventory.Find(Bag(19))!.Core == Core(101000007) &&
                current.Inventory.Find(Bag(121))!.Core == Core(3290, 88) && current.Inventory.Find(Bag(122))!.Core == Core(3311, 88),
                "default selections persist only the selected complete cores and declared quantities");
            for (int i = 0; i < imported.Length; i++)
                That(current.Inventory.Find(Bag((short)(16 + i)))!.Core ==
                    Core(imported[i]) with { ExpireTime = checked((int)U32(importedEntries[i], 56)) },
                    "repository reopen retains each imported reward's complete core and original expiry");
            InventoryService.Commit(repo, owner, current, InventoryRewardGrantService.TryGrantBatch(current, [new(10161354, 1)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces));
        }
        await fixture.StartAsync("selection-restart");
        using (var peer = Open(fixture))
        {
            byte[] full = peer.Select()[4].Body;
            Entry(full, 65, 10000872, 1, true);
            Entry(full, 66, 10000871, 2, true);
            Entry(full, 67, 10161354, 1, true);
            Entry(full, 9, 109000071, InventoryRewardGrantService.InitialQuality, true);
            Entry(full, 19, 101000007, InventoryRewardGrantService.InitialQuality, true);
            Entry(full, 121, 3290, 88, true);
            Entry(full, 122, 3311, 88, true);
            for (int i = 0; i < imported.Length; i++)
                That(EquipmentEntry(full, (short)(16 + i), true).SequenceEqual(importedEntries[i]),
                    "isolated server restart restores imported equipment without resetting its expiry or duplicating a reward");
            string before = Snapshot();
            Sql("CREATE TRIGGER fail_selection BEFORE INSERT ON character_inventory_slots WHEN NEW.slot_index=21 BEGIN SELECT RAISE(ABORT,'selection rollback'); END");
            SendSelect(peer, 67, all);
            peer.Closed();
            That(before == Snapshot(), "later reward failure sends no success and rolls back source, earlier reward, revisions, audit and UID sequence");
            Sql("DROP TRIGGER fail_selection");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            SendSelect(peer, 67, all);
            ExpectAck(peer, 10161354, 67, all.Select(id => (id, 1)).ToArray());
            byte[] delta = peer.Expect(0, 14).Body;
            Entry(delta, 67, -1, 0);
            for (int i = 0; i < all.Length; i++)
                Entry(delta, (short)(20 + i), all[i], InventoryRewardGrantService.InitialQuality);
            peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("SELECT gold FROM characters") is [[12345L]], "selection does not add a gold or purchase-material fee");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "selection database integrity");

        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(itemResources.Items.Get(id), count);
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT list_type,slot_index,item_uid FROM character_inventory_slots ORDER BY slot_index")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
    internal static byte[] EquipmentEntry(byte[] body, short slot, bool full = false)
    {
        int header = full ? 5 : 3;
        int at = Enumerable.Range(0, U16(body, full ? 3 : 1)).Select(index => header + index * 123).Single(at => U16(body, at) == slot);
        return body.AsSpan(at, 123).ToArray();
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    internal static void SendSelect(TcpPeer peer, short slot, int[] ids, byte category = 0, byte subcategory = 0, int count = 1) => peer.Send(160, Build(writer =>
    {
        writer.Write(slot);
        writer.Write(count);
        writer.Write(category);
        writer.Write(subcategory);
        foreach (int id in ids)
            writer.Write(id);
        writer.Write((byte)0);
        writer.Write((byte)0);
    }));
}
