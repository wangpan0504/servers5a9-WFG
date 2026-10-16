using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.BoosterSelectionTcpTests;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class RefinementTicketTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await Inventory.RefinementTicketTests.RunAsync(resources.Towns, resources.Characters, resources.ItemResources);
        var itemResources = resources.ItemResources;
        var characters = resources.Characters;
        var towns = resources.Towns;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "refinement-tickets"), resources: resources);
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var weapon = characters.Get(0).Equipment.Single(row => row.Slot == 12).Template;
        var core = InventoryRewardGrantService.Create(weapon) with
        {
            GenuineUpgrade = 7, Durability = 0, Attr = 7, EnchantCardId = 3175, EnchantUpgrade = 2,
            Value = 1234567, AmplifyType = 2, AmplifyValue = 21, Rune = 3, Random0 = new(1, 2, 3)
        };
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("TicketTcp", towns.Room, towns.InitialReturn)
            {
                Skills = characters.Get(0).Skills, AcceptedStoryLevel = 1,
                Inventory = new([new(0, Bag(3), core), new(0, Bag(9), core with { GenuineUpgrade = 8 }),
                    new(0, Worn(12), core with { GenuineUpgrade = 4 }),
                    new(0, Bag(10), InventoryRewardGrantService.Create(itemResources.Items.Get(401000012)) with { Durability = 0, ExpireTime = 0 }),
                    new(0, Bag(4), Ticket(10007131, 2)), new(0, Bag(5), Ticket(10007990, 2)),
                    new(0, Bag(65), Ticket(10006551, 1)), new(0, Bag(66), Ticket(10007139, 1))], Gold: 1234)
            });
        string targetUids = JsonSerializer.Serialize(fixture.Query("SELECT item_uid,list_type,slot_index FROM character_inventory_slots WHERE list_type=3 OR slot_index IN (3,9,10) ORDER BY item_uid"));
        string currencyAudits = JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM currency_audit_log"));
        byte[]? committedQuick = null;
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            Send(peer, 0, 3, weapon.Id, 4);
            peer.Expect(1, 438, [0, 4, 0]);
            var entry = peer.Select();
            committedQuick = EquipmentEntry(entry[4].Body, 3, true);
            committedQuick[87] = 5;
            Send(peer, 0, 3, weapon.Id, 4, 2);
            Ack(peer, 4, 1, 7, 5, 0, 3);
            var delta = peer.Expect(0, 14).Body;
            Bytes(EquipmentEntry(delta, 3), committedQuick, "fixed coupon changes only refinement despite damage and higher old level");
            That(U32(EquipmentEntry(delta, 4), 6) == 1, "fixed coupon decrements only its selected stack");
            Send(peer, 0, 3, weapon.Id, 5, 1);
            Ack(peer, 5, 1, 5, 6, 0, 3);
            committedQuick[87] = 6;
            Bytes(EquipmentEntry(peer.Expect(0, 14).Body, 3), committedQuick, "additional coupon reports the actual new level");
            string before = Snapshot();
            Send(peer, 0, 9, weapon.Id, 5);
            peer.Expect(1, 438, [0, 95, 0]);
            Send(peer, 0, 3, weapon.Id, 65);
            peer.Expect(1, 438, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, "cap and authored whitelist failures preserve all coupons and equipment");
            Send(peer, 0, 9, weapon.Id, 4, 2);
            Ack(peer, 4, 0, 8, 5, 0, 9);
            delta = peer.Expect(0, 14).Body;
            That(U32(EquipmentEntry(delta, 4), 2) == uint.MaxValue && EquipmentEntry(delta, 9)[87] == 5, "fixed ticket remains usable at ordinary cap and clears exhausted stack");
            Send(peer, 3, 12, weapon.Id, 5, 1);
            Ack(peer, 5, 0, 4, 5, 3, 12);
            That(U32(EquipmentEntry(peer.Expect(0, 14).Body, 5), 2) == uint.MaxValue, "worn coupon consumes Main instance");
            That(EquipmentEntry(peer.Expect(0, 14).Body, 12)[87] == 5, "worn item delta contains committed refinement");
            peer.Expect(0, 2);
            peer.Expect(0, 2);
            Send(peer, 0, 10, 401000012, 65);
            Ack(peer, 65, 0, 0, 5, 0, 10);
            delta = peer.Expect(0, 14).Body;
            That(U32(EquipmentEntry(delta, 65), 2) == uint.MaxValue && EquipmentEntry(delta, 10)[87] == 5, "special weapon list accepts matching target and clears only its coupon");
            peer.Barrier();
            before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_tcp_ticket BEFORE INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'ticket failure'); END");
            Send(peer, 0, 3, weapon.Id, 66, 2);
            peer.Closed();
            That(Snapshot() == before, "late failure rolls back coupon deletion, downgrade and revision before success");
            fixture.Sql("DROP TRIGGER fail_tcp_ticket");
        }
        using (var peer = Open(fixture))
        {
            var full = peer.Select()[4].Body;
            Bytes(EquipmentEntry(full, 3, true), committedQuick, "reconnect keeps already committed result after later failed request");
            Send(peer, 0, 3, weapon.Id, 66, 2);
            Ack(peer, 66, 0, 6, 3, 0, 3);
            committedQuick[87] = 3;
            Bytes(EquipmentEntry(peer.Expect(0, 14).Body, 3), committedQuick, "explicit new attempt consumes one restored ticket");
            string before = Snapshot();
            Send(peer, 0, 3, weapon.Id, 66, 2);
            peer.Expect(1, 438, [0, 7, 0]);
            peer.Barrier();
            That(Snapshot() == before, "replay of exhausted coupon performs no second mutation");
        }
        string committed = Snapshot();
        That(JsonSerializer.Serialize(fixture.Query("SELECT item_uid,list_type,slot_index FROM character_inventory_slots ORDER BY item_uid")) == targetUids,
            "only coupon instances removed, all equipment identities and positions retained");
        That(fixture.Query("SELECT gold FROM characters") is [[1234L]] && JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM currency_audit_log")) == currencyAudits,
            "coupons never charge ordinary fees or currency");
        using (var peer = Open(fixture))
        {
            var full = peer.Select()[4].Body;
            Bytes(EquipmentEntry(full, 3, true), committedQuick, "reconnect preserves exact coupon-applied equipment core");
            That(Snapshot() == committed && EquipmentEntry(full, 9, true)[87] == 5 && EquipmentEntry(full, 10, true)[87] == 5,
                "reconnect restores fixed and restricted results without returning spent coupons");
            peer.Barrier();
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "coupon database integrity");

        // 测试历史券的未到期实例；不改PVF或正式创建时的到期规则。
        ItemCore Ticket(int id, int count) => InventoryRewardGrantService.Create(itemResources.Items.Get(id), count) with { ExpireTime = 0 };
        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters ORDER BY id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log"));
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static InventorySlot Worn(short slot) => new(InventoryListType.Equipment, slot);
    static void Send(TcpPeer peer, byte list, short slot, int id, short couponSlot, byte confirmation = 0) => peer.Send(438, Build(writer =>
    {
        writer.Write(list);
        writer.Write(slot);
        writer.Write(id);
        writer.Write(couponSlot);
        writer.Write(3);
        writer.Write("NPC"u8);
        writer.Write(confirmation);
    }));
    static void Ack(TcpPeer peer, short sourceSlot, int remaining, byte oldLevel, byte newLevel, byte list, short slot) => peer.Expect(1, 438, Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(sourceSlot);
        writer.Write(remaining);
        writer.Write(oldLevel);
        writer.Write((byte)0);
        writer.Write(newLevel);
        writer.Write(list);
        writer.Write(slot);
    }));
}
