using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class MaterialExchangeTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "material-exchange"));
        var characters = resources.Characters;
        var towns = resources.Towns;
        var catalog = itemResources.Items;
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.Create(owner, new("MultiTcp", towns.Room, towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = characters.Get(15).Skills,
                Inventory = new([new(0, Bag(3), Core(10305616, 350)), new(0, Bag(121), Core(10305616, 350)),
                    new(0, Bag(122), Core(10158964, 20)), new(0, Bag(4), Core(3027, 10)), new(0, Bag(123), Core(3027, 25))], Gold: 54321)
            });
            InventoryService.Commit(repo, owner, current, InventoryRewardGrantService.TryGrantBatch(current, [new(3034, 100)], catalog, itemResources.GoldLimits, itemResources.EpicPieces));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            Buy(peer, 101000783, 1, 247, 486);
            peer.Expect(1, 21, [0, 17, 0]);
            peer.Select();
            Buy(peer, 101000783, 99, 247, 486);
            CheckAck(peer, 101000783, 999999998, 9, [(10305616, 0), (10158964, 5)]);
            byte[] delta = peer.Expect(0, 14).Body;
            Entry(delta, 3, -1, 0);
            Entry(delta, 9, 101000783, 999999998);
            Entry(delta, 121, 10305616, 100);
            Entry(delta, 122, 10158964, 5);
            That(U16(delta, 1) == 4, "weapon exchange updates only the three source slots and output");
            Buy(peer, 10099377, 3, 5, 5);
            CheckAck(peer, 10099377, 3, 65, [(3027, 0), (3034, 70)]);
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 4, -1, 0);
            Entry(delta, 65, 10099377, 3);
            Entry(delta, 123, 3027, 5);
            Entry(delta, 355, 3034, 70);
            That(U16(delta, 1) == 4, "mixed material purchase includes wallet total after ACK");
            string before = Snapshot();
            Buy(peer, 101000783, 1, 247, 486);
            peer.Expect(1, 21, [0, 10, 0]);
            Buy(peer, 10099377, 3, 5, 5);
            peer.Expect(1, 21, [0, 10, 0]);
            peer.Barrier();
            That(before == Snapshot(), "insufficient repeated multi-material requests neither debit nor grant");
        }
        await fixture.StopAsync();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single();
            var grant = InventoryRewardGrantService.TryGrantBatch(current, [new(10305616, 1100), new(10158964, 25)], catalog, itemResources.GoldLimits, itemResources.EpicPieces);
            var after = grant.Plan!.After;
            after = after with { Items = after.Items.Add(new(0, Bag(3), Core(10305616, 1) with { EquipmentLockId = 1 })) };
            repo.CommitInventory(owner, current, new(current.Inventory, after, "exchange_fixture_refill"));
        }
        await fixture.StartAsync("material-exchange-restart");
        using (var peer = Open(fixture))
        {
            var packets = peer.Select(inventoryNotices: [(0, 251)]);
            Entry(packets[4].Body, 9, 101000783, 999999998, full: true);
            Entry(packets[4].Body, 65, 10099377, 3, full: true);
            string before = Snapshot();
            Sql("CREATE TRIGGER fail_multi BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'multi rollback'); END");
            Buy(peer, 101000783, 1, 247, 486);
            peer.Closed();
            That(before == Snapshot(), "failed transaction restores both costs and output, with no ACK or partial notifications");
            Sql("DROP TRIGGER fail_multi");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(0, 251)]);
            Buy(peer, 101000783, 1, 247, 486);
            CheckAck(peer, 101000783, 999999998, 10, [(10158964, 15)]);
            byte[] delta = peer.Expect(0, 14).Body;
            Entry(delta, 121, 10305616, 600);
            Entry(delta, 122, 10158964, 15);
            Entry(delta, 10, 101000783, 999999998);
            That(U16(delta, 1) == 3, "locked first material is unchanged while later stacks are consumed");
            Buy(peer, 101000783, 1, 247, 486);
            CheckAck(peer, 101000783, 999999998, 11, [(10158964, 0)]);
            delta = peer.Expect(0, 14).Body;
            Entry(delta, 121, -1, 0);
            Entry(delta, 122, -1, 0);
            Entry(delta, 11, 101000783, 999999998);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            var packets = peer.Select(inventoryNotices: [(0, 251)]);
            Entry(packets[4].Body, 3, 10305616, 1, full: true);
            Entry(packets[4].Body, 11, 101000783, 999999998, full: true);
            Buy(peer, 101000783, 1, 247, 486);
            peer.Expect(1, 21, [0, 10, 0]);
            peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("SELECT count(*),count(DISTINCT item_uid) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=101000783") is [[3L, 3L]],
            "three paid weapon purchases create exactly three unique instances");
        That(fixture.Query("SELECT gold FROM characters") is [[54321L]], "material-only PVF recipes never add a sale-value gold fee");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "multi-material TCP database integrity");

        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT revision FROM account_wallets")) +
            JsonSerializer.Serialize(fixture.Query("SELECT currency_kind,balance FROM account_currency_balances ORDER BY currency_kind")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM currency_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'"));
        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        ItemCore Core(int id, int count) => InventoryRewardGrantService.Create(catalog.Get(id), count);
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static void Buy(TcpPeer peer, int item, int count, int shop, int npc) => peer.Send(21, Build(writer =>
    {
        writer.Write(item);
        writer.Write(count);
        writer.Write(shop);
        writer.Write(npc);
    }));
    static void CheckAck(TcpPeer peer, int item, int value, short slot, (int Item, int Remaining)[] materials)
    {
        byte[] ack = peer.Expect(1, 21).Body;
        That(ack[0] == 1 && ack.Length == 141 + materials.Length * 8 && ack[140] == materials.Length, "ACK has native byte count and exact repeated row width");
        That(U32(ack, 1) == 54321 && U16(ack, 17) == slot && U32(ack, 19) == item && U32(ack, 23) == value, "ACK carries paid gold and actual reward increment/core");
        for (int i = 0; i < materials.Length; i++)
            That(U32(ack, 141 + i * 8) == materials[i].Item && U32(ack, 145 + i * 8) == materials[i].Remaining, "material row reports its paid balance");
    }
    static void Entry(byte[] body, short slot, int item, int value, bool full = false)
    {
        int header = full ? 5 : 3;
        int count = U16(body, full ? 3 : 1);
        That(body.Length == header + count * 123, "full-width material exchange inventory entries");
        int at = Enumerable.Range(0, count).Select(index => header + index * 123).Single(at => U16(body, at) == slot);
        That(U32(body, at + 2) == unchecked((uint)item) && U32(body, at + 6) == value, "committed material/output slot " + slot);
    }
}
