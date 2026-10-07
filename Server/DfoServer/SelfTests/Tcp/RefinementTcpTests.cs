using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.BoosterSelectionTcpTests;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class RefinementTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        var characters = resources.Characters;
        var towns = resources.Towns;
        Inventory.RefinementTests.Run(towns, characters, resources.ItemResources);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "refinement"));
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var weapon = characters.Get(0).Equipment.Single(item => item.Slot == 12).Template;
        That(itemResources.Refinement.TryCost(weapon, 0, out var cost) && cost.SuccessWeight == 10000, "TCP fixture uses guaranteed first refinement");
        var core = InventoryRewardGrantService.Create(weapon) with
        {
            Value = 1234567, Attr = 7, EnchantCardId = 3175, EnchantUpgrade = 2, AmplifyType = 2, AmplifyValue = 21,
            Rune = 3, Random0 = new(1, 2, 3), TradeRestriction = 2, Extension27 = 1234
        };
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("RefineTcp", towns.Room, towns.InitialReturn)
            {
                Skills = characters.Get(0).Skills, AcceptedStoryLevel = 1,
                Inventory = new([new(0, Bag(3), core), new(0, Bag(9), core), new(0, Worn(12), core),
                    new(0, Bag(10), core with { GenuineUpgrade = 8 }), new(0, Bag(11), core with { Durability = 0 }),
                    new(0, Bag(4), InventoryRewardGrantService.Create(itemResources.Items.Get(cost.ItemId), cost.Count * 3))], Gold: 1234)
            });
        string targetUids = JsonSerializer.Serialize(fixture.Query("SELECT item_uid,list_type,slot_index FROM character_inventory_slots WHERE slot_index<>4 ORDER BY item_uid"));
        string currencyAudits = JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM currency_audit_log"));
        byte[]? quickCore = null, bagCore = null;
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            Send(peer, 0, 3);
            peer.Expect(1, 438, [0, 4, 0]);
            var entry = peer.Select();
            quickCore = EquipmentEntry(entry[4].Body, 3, true);
            quickCore[87] = 1;
            bagCore = EquipmentEntry(entry[4].Body, 9, true);
            bagCore[87] = 1;
            string before = Snapshot();
            Send(peer, 0, 10);
            peer.Expect(1, 438, [0, 95, 0]);
            Send(peer, 0, 11);
            peer.Expect(1, 438, [0, 7, 0]);
            Send(peer, 0, 3, itemId: core.Value);
            peer.Expect(1, 438, [0, 4, 0]);
            peer.Barrier();
            That(Snapshot() == before, "wrong template, maximum level and damaged weapon leave all assets unchanged");
            Send(peer, 0, 3);
            Ack(peer, cost.Count * 2, 0, 3);
            var delta = peer.Expect(0, 14).Body;
            Bytes(EquipmentEntry(delta, 3), quickCore, "quickbar delta changes only refinement byte in the complete raw item");
            That(U32(EquipmentEntry(delta, 4), 6) == cost.Count * 2, "material delta reports absolute remaining count");
            peer.Barrier();
            before = Snapshot();
            Sql("CREATE TRIGGER fail_tcp_refinement BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'refinement failure'); END");
            Send(peer, 0, 9);
            peer.Closed();
            That(Snapshot() == before, "late database failure closes without success and rolls back material, level and audit");
            Sql("DROP TRIGGER fail_tcp_refinement");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            Bytes(EquipmentEntry(entry[4].Body, 3, true), quickCore, "reconnect restores committed quickbar core");
            That(EquipmentEntry(entry[4].Body, 9, true)[87] == 0, "failed transaction restores old equipment level");
            Send(peer, 0, 9, confirmation: 1);
            Ack(peer, cost.Count, 0, 9);
            Bytes(EquipmentEntry(peer.Expect(0, 14).Body, 9), bagCore, "confirmation does not select a different cost or reject an ordinary material");
            Send(peer, 3, 12, confirmation: 2);
            Ack(peer, 0, 3, 12);
            var clear = peer.Expect(0, 14).Body;
            That(clear[0] == 0 && U32(EquipmentEntry(clear, 4), 2) == uint.MaxValue, "last material is cleared after committed ACK");
            var worn = peer.Expect(0, 14).Body;
            That(worn[0] == 3 && EquipmentEntry(worn, 12)[87] == 1, "worn refinement uses the actual equipment container");
            peer.Expect(0, 2);
            peer.Expect(0, 2);
            peer.Barrier();
            string before = Snapshot();
            Send(peer, 3, 12);
            peer.Expect(1, 438, [0, 22, 0]);
            peer.Barrier();
            That(Snapshot() == before, "exhausted replay has no delta, charge or new refinement");
        }
        await fixture.StopAsync();
        string committed = Snapshot();
        That(JsonSerializer.Serialize(fixture.Query("SELECT item_uid,list_type,slot_index FROM character_inventory_slots ORDER BY item_uid")) == targetUids,
            "all target UIDs and positions survive and only the consumed material disappears");
        That(fixture.Query("SELECT gold FROM characters") is [[1234L]] && JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM currency_audit_log")) == currencyAudits,
            "NPC refinement never charges gold");
        await fixture.StartAsync("refinement-restart");
        using (var peer = Open(fixture))
        {
            var full = peer.Select()[4].Body;
            Bytes(EquipmentEntry(full, 3, true), quickCore, "process restart restores complete quickbar item");
            Bytes(EquipmentEntry(full, 9, true), bagCore, "process restart restores complete bag item");
            That(Snapshot() == committed, "entry does not reroll or rewrite committed refinement");
            peer.Send(438, [0, 3, 0, 128, 62, 0, 0, 4, 0, 255, 255, 255, 127, 65, 0]);
            peer.Closed();
            That(Snapshot() == committed, "malformed variable-length request cannot mutate assets");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "refinement TCP database integrity");

        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters ORDER BY id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,list_type,slot_index FROM character_inventory_slots ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log"));
        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        void Send(TcpPeer peer, byte list, short slot, int? itemId = null, byte confirmation = 0) => peer.Send(438, Build(w =>
        {
            w.Write(list);
            w.Write(slot);
            w.Write(itemId ?? weapon.Id);
            w.Write((short)4);
            w.Write(3);
            w.Write("NPC"u8);
            w.Write(confirmation);
        }));
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static InventorySlot Worn(short slot) => new(InventoryListType.Equipment, slot);
    static void Ack(TcpPeer peer, int remaining, byte list, short slot) => peer.Expect(1, 438, Build(w =>
    {
        w.Write((byte)1);
        w.Write((short)4);
        w.Write(remaining);
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((byte)1);
        w.Write(list);
        w.Write(slot);
    }));
}
