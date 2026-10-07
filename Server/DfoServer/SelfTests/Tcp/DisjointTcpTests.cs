using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class DisjointTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "disjoint"));
        var characters = resources.Characters;
        var towns = resources.Towns;
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var coat = InventoryRewardGrantService.Create(itemResources.Items.Get(10400));
        var tool = InventoryRewardGrantService.Create(itemResources.Items.Get(2600468), 2);
        var totals = new Dictionary<int, int> { [3037] = 5, [10100115] = 7, [10158124] = 9, [10099775] = 11 };
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.Create(owner, new("DisjointTcp", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = new([new(0, Bag(3), coat), new(0, Bag(4), tool), new(0, Bag(9), coat),
                    new(0, Bag(10), InventoryRewardGrantService.Create(itemResources.Items.Get(10114))),
                    new(0, Bag(11), InventoryRewardGrantService.Create(itemResources.Items.Get(400050000))),
                    new(0, Bag(12), coat with { EquipmentLockId = 1 })])
            });
            InventoryService.Commit(repo, owner, current, InventoryRewardGrantService.TryGrantBatch(current,
                totals.Select(pair => new InventoryReward(pair.Key, pair.Value)).ToArray(), itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            Send(peer, 3, 4);
            peer.Expect(1, 26, [0, 19, 0]);
            var packets = peer.Select(inventoryNotices: [(0, 251)]);
            CheckWallet(packets[4].Body);
            Send(peer, 12, -1);
            peer.Expect(1, 26, [0, 19, 0]);
            Send(peer, 3, 4);
            Receive(peer, 3, 4, 1, [3037, 10100115]);
            string saved = Snapshot();
            Send(peer, 3, 4);
            peer.Expect(1, 26, [0, 17, 0]);
            peer.Barrier();
            That(Snapshot() == saved, "empty source replay cannot debit tool or duplicate rewards");
            Send(peer, 9, 4);
            Receive(peer, 9, 4, 0, [3037, 10100115]);
            Send(peer, 10, -1);
            Receive(peer, 10, -1, 0, [3037, 10158124]);
            Send(peer, 11, -1);
            Receive(peer, 11, -1, 0, [3037, 3332, 10099775]);
            peer.Barrier();
        }
        await fixture.StopAsync();
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single();
            That(current.Inventory.Find(Bag(12))!.Core.EquipmentLockId == 1 && current.Inventory.Find(Bag(4)) is null,
                "locked source retained and exhausted portable tool removed");
            foreach (var pair in totals)
                That(CurrencyWallet.RewardKind(pair.Key) is { } kind ? CurrencyWallet.Get(current.Inventory, kind) == pair.Value :
                    current.Inventory.Items.Where(item => item.Core.ItemId == pair.Key).Sum(item => item.Core.Count) == pair.Value,
                    "database result equals exact received increments for " + pair.Key);
            var after = current.Inventory with { Items = current.Inventory.Items.Add(new(0, Bag(3), coat)).Add(new(0, Bag(4), tool)) };
            repo.CommitInventory(owner, current, new(current.Inventory, after, "disjoint_fixture_restore"));
        }
        await fixture.StartAsync("disjoint-restart");
        using (var peer = Open(fixture))
        {
            CheckWallet(peer.Select(inventoryNotices: [(0, 251)])[4].Body);
            string before = Snapshot();
            Sql("CREATE TRIGGER fail_tcp_disjoint BEFORE INSERT ON currency_audit_log BEGIN SELECT RAISE(ABORT,'disjoint failure'); END");
            Send(peer, 3, 4);
            peer.Closed();
            That(Snapshot() == before, "failure during wallet audit rolls back source, tool, wallet and inventory audit without ACK");
            Sql("DROP TRIGGER fail_tcp_disjoint");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(0, 251)]);
            Send(peer, 3, 4);
            Receive(peer, 3, 4, 1, [3037, 10100115]);
            peer.Barrier();
            peer.Send(26, new byte[8]);
            peer.Closed();
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "disjoint TCP database integrity");

        void Receive(TcpPeer peer, short sourceSlot, short toolSlot, int toolCount, int[] expectedItems)
        {
            byte[] ack = peer.Expect(1, 26).Body;
            That(ack.Length >= 7 && ack[0] == 1 && U16(ack, 1) == sourceSlot && ack[3] == 0 && U16(ack, 4) == ushort.MaxValue,
                "disjoint ACK source and extra owner word precede result count");
            That(ack.Length == 7 + ack[6] * 10 && ack[6] == expectedItems.Length, "disjoint ACK exact count and row width");
            var rows = Enumerable.Range(0, ack[6]).Select(index => 7 + index * 10).ToArray();
            That(rows.Select(at => (int)U32(ack, at + 2)).SequenceEqual(expectedItems), "disjoint ACK exact output identities");
            byte[] delta = peer.Expect(0, 14).Body;
            That(delta[0] == 0 && delta.Length == 3 + U16(delta, 1) * 123, "disjoint authoritative main delta layout");
            That(U32(delta, Entry(delta, sourceSlot) + 2) == uint.MaxValue, "committed source cleared after ACK");
            if (toolSlot >= 0)
            {
                int at = Entry(delta, toolSlot);
                That(U32(delta, at + 2) == (toolCount == 0 ? uint.MaxValue : 2600468u) && U32(delta, at + 6) == toolCount,
                    "portable tool decremented exactly once or cleared");
            }
            foreach (int at in rows)
            {
                short slot = (short)U16(ack, at);
                int item = (int)U32(ack, at + 2), count = (int)U32(ack, at + 6);
                That(count > 0, "ACK grant quantity is positive increment");
                totals[item] = checked(totals.GetValueOrDefault(item) + count);
                int target = Entry(delta, slot);
                That(U32(delta, target + 2) == item && U32(delta, target + 6) == totals[item], "ACK increment then exact committed total");
            }
        }
        void CheckWallet(byte[] full)
        {
            int epic = Entry(full, 365, true), legendary = Entry(full, 364, true);
            That(U32(full, epic + 2) == 10158124 && U32(full, epic + 6) == totals[10158124] &&
                U32(full, legendary + 2) == 10099775 && U32(full, legendary + 6) == totals[10099775],
                "epic and legendary wallet rows retain item identity across restart");
        }
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision FROM characters")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT currency_kind,balance FROM account_currency_balances ORDER BY currency_kind")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM currency_audit_log"));
        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static void Send(TcpPeer peer, short slot, short tool) => peer.Send(26, Build(writer =>
    {
        writer.Write(slot);
        writer.Write((byte)0);
        writer.Write(tool);
        writer.Write(0);
    }));
    static int Entry(byte[] packet, short slot, bool full = false)
    {
        int header = full ? 5 : 3;
        int count = U16(packet, full ? 3 : 1);
        return Enumerable.Range(0, count).Select(index => header + 123 * index).Single(at => U16(packet, at) == slot);
    }
}
