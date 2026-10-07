using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EpicBookTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "epic-book"));
        var characters = resources.Characters;
        var towns = resources.Towns;
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.Create(owner, new("EpicTcp", towns.Room, towns.InitialReturn)
            {
                Job = 15, AcceptedStoryLevel = 1, Skills = characters.Get(15).Skills
            });
            InventoryService.Commit(repo, owner, current, InventoryRewardGrantService.TryGrantBatch(current,
                [new(10300000, 2000), new(10300001, 7), new(10099775, 44), new(10158124, 24)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces));
            repo.Create(owner, new("EpicSibling", towns.Room, towns.InitialReturn) { AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            SendCraft(peer);
            peer.Expect(1, 788, [0, 4, 0]);
            CheckFull(peer.Select(), 2000);
            string before = Snapshot();
            peer.Send(788, Build(writer => writer.Write(-1)));
            peer.Expect(1, 788, [0, 4, 0]);
            peer.Barrier();
            That(before == Snapshot(), "unknown recipe preserves all assets without notifications");
            SendCraft(peer);
            Receive(peer, 9, 1000, 22, 12);
            SendCraft(peer);
            Receive(peer, 10, 0, 0, 0);
            before = Snapshot();
            SendCraft(peer);
            peer.Expect(1, 788, [0, 4, 0]);
            peer.Barrier();
            That(before == Snapshot(), "exhausted repeated recipe cannot create another item");
        }
        using (var peer = Open(fixture))
        {
            CheckFull(peer.Select(1), 0);
            peer.Barrier();
        }
        await fixture.StopAsync();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var roles = repo.List(owner);
            That(roles[0].Inventory.EpicPieces.Same(roles[1].Inventory.EpicPieces) && roles[0].Inventory.Items.Length == 2 && roles[1].Inventory.Items.Length == 0,
                "book belongs to account, crafted equipment belongs to selected character");
            InventoryService.Commit(repo, owner, roles[0], InventoryRewardGrantService.TryGrantBatch(roles[0],
                [new(10300000, 1000), new(10099775, 22), new(10158124, 12)], itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces));
        }
        await fixture.StartAsync("epic-book-restart");
        using (var peer = Open(fixture))
        {
            CheckFull(peer.Select(), 1000);
            string before = Snapshot();
            Sql("CREATE TRIGGER fail_tcp_epic BEFORE INSERT ON epic_piece_audit_log BEGIN SELECT RAISE(ABORT,'epic failure'); END");
            SendCraft(peer);
            peer.Closed();
            That(before == Snapshot(), "book audit failure rolls back output UID, currencies and counts without success ACK");
            Sql("DROP TRIGGER fail_tcp_epic");
        }
        using (var peer = Open(fixture))
        {
            CheckFull(peer.Select(), 1000);
            SendCraft(peer);
            Receive(peer, 11, 0, 0, 0);
            peer.Barrier();
            peer.Send(788, new byte[3]);
            peer.Closed();
        }
        using (var peer = Open(fixture))
        {
            CheckFull(peer.Select(), 0);
            peer.Send(788, new byte[5]);
            peer.Closed();
        }
        await fixture.StopAsync();
        That(fixture.Query("SELECT count(*),count(DISTINCT item_uid) FROM character_inventory_items") is [[3L, 3L]], "exactly three independent crafted instances survive reconnect");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "epic book TCP database integrity");

        void Receive(TcpPeer peer, short slot, int pieces, int legendary, int epic)
        {
            peer.Expect(1, 788, [1]);
            byte[] delta = peer.Expect(0, 14).Body;
            That(delta[0] == 0 && U16(delta, 1) == 3 && delta.Length == 372, "craft main delta contains output and two soul balances");
            int row = Entry(delta, slot);
            That(U32(delta, row + 2) == 450004 && U32(delta, row + 6) == 999999998 && U16(delta, row + 11) == itemResources.Items.Get(450004).Durability,
                "craft output contains matched complete equipment identity and durability");
            That(U32(delta, Entry(delta, 364) + 2) == 10099775 && U32(delta, Entry(delta, 364) + 6) == legendary &&
                U32(delta, Entry(delta, 365) + 2) == 10158124 && U32(delta, Entry(delta, 365) + 6) == epic, "craft soul delta contains authoritative totals");
            peer.Expect(0, 1152, Build(writer =>
            {
                writer.Write((byte)1);
                writer.Write(1);
                writer.Write(10300000);
                writer.Write(pieces);
            }));
        }
        void CheckFull(WirePacket[] packets, int balance)
        {
            byte[] book = packets.Single(packet => packet.Kind == 0 && packet.Opcode == 1152).Body;
            Bytes(book, Build(writer =>
            {
                writer.Write((byte)0);
                writer.Write(balance == 0 ? 1 : 2);
                if (balance != 0)
                {
                    writer.Write(10300000);
                    writer.Write(balance);
                }
                writer.Write(10300001);
                writer.Write(7);
            }), "login replaces book with committed account totals");
            byte[] main = packets[4].Body;
            That(!Enumerable.Range(0, U16(main, 3)).Any(index => U32(main, 7 + index * 123) is 10300000 or 10300001), "book fragments never appear as Main instances");
        }
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision FROM characters ORDER BY id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT revision,order_hash,counts FROM account_epic_books")) +
            JsonSerializer.Serialize(fixture.Query("SELECT revision FROM account_wallets")) +
            JsonSerializer.Serialize(fixture.Query("SELECT currency_kind,balance FROM account_currency_balances ORDER BY currency_kind")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM currency_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM epic_piece_audit_log"));
        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
    static void SendCraft(TcpPeer peer) => peer.Send(788, Build(writer => writer.Write(450004)));
    static int Entry(byte[] packet, short slot) => Enumerable.Range(0, U16(packet, 1)).Select(index => 3 + 123 * index).Single(at => U16(packet, at) == slot);
}
