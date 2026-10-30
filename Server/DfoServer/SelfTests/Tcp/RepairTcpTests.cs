using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class RepairTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var itemResources = resources.ItemResources;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "repair"), resources: resources);
        var characters = resources.Characters;
        var towns = resources.Towns;
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var low = InventoryRewardGrantService.Create(itemResources.Items.Get(10400)) with { Durability = 0 };
        var high = InventoryRewardGrantService.Create(itemResources.Items.Get(10402)) with { Durability = 0 };
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repo.Create(owner, new("RepairTcp", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                Inventory = new([new(0, new(InventoryListType.Main, 3), low), new(0, new(InventoryListType.Main, 9), high),
                    new(0, new(InventoryListType.PersonalCargo, 0), low), new(0, new(InventoryListType.Equipment, 14), high)], Gold: 1000)
            });
        string initialIds = JsonSerializer.Serialize(fixture.Query("SELECT item_uid,list_type,slot_index FROM character_inventory_slots ORDER BY item_uid"));
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            Send(peer, 0, 9, true);
            peer.Expect(1, 23, [0, 19, 0]);
            peer.Select();
            Send(peer, 0, 9, true);
            Ack(peer, 760, 0, 9);
            var delta = peer.Expect(0, 14).Body;
            Gold(delta, 760);
            Durability(delta, 0, 9, 10402, 33);
            string full = Snapshot();
            Send(peer, 0, 9, true);
            Ack(peer, 760, 0, 9);
            peer.Barrier();
            That(Snapshot() == full, "repeated full repair has no debit, revision or audit");
            peer.Send(267, [0, 3, 0]);
            peer.Expect(1, 267, [1, 0, 3, 0]);
            peer.Expect(0, 251, [1, 0, 0, 3, 0, 1]);
            Send(peer, 0, -1, false);
            Ack(peer, 581, 0, -1);
            delta = peer.Expect(0, 14).Body;
            Gold(delta, 581);
            Durability(delta, 0, 3, 10400, 33);
            Durability(peer.Expect(0, 14).Body, 3, 14, 10402, 33);
            peer.Expect(0, 2);
            peer.Expect(0, 2);
            peer.Expect(0, 251, [1, 0, 0, 3, 0, 1]);
            Send(peer, 2, 0, false);
            Ack(peer, 562, 2, 0);
            Gold(peer.Expect(0, 14).Body, 562);
            Durability(peer.Expect(0, 14).Body, 2, 0, 10400, 33);
            Send(peer, 12, -1, false);
            peer.Expect(1, 23, [0, 17, 0]);
            peer.Send(23, [0, 255, 255, 255, 255, 1, 0]);
            peer.Expect(1, 23, [0, 19, 0]);
            peer.Send(23, [0, 9, 0, 65, 0, 0, 0, 0]);
            peer.Expect(1, 23, [0, 19, 0]);
            peer.Barrier();
        }
        That(JsonSerializer.Serialize(fixture.Query("SELECT item_uid,list_type,slot_index FROM character_inventory_slots ORDER BY item_uid")) == initialIds,
            "repair never reallocates or moves instances");
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var current = repo.List(owner).Single();
            var target = current.Inventory.Find(new(InventoryListType.Main, 9))!;
            var after = current.Inventory with { Items = current.Inventory.Items.Replace(target, target with { Core = target.Core with { Durability = 0 } }) };
            repo.CommitInventory(owner, current, new(current.Inventory, after, "repair_fixture_damage"));
        }
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(0, 251)]);
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_tcp_repair BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'repair failure'); END");
            Send(peer, 0, 9, true);
            peer.Closed();
            That(Snapshot() == before, "database failure closes without success or partial repair/debit");
            fixture.Sql("DROP TRIGGER fail_tcp_repair");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(0, 251)]);
            Send(peer, 0, 9, true);
            Ack(peer, 322, 0, 9);
            var delta = peer.Expect(0, 14).Body;
            Gold(delta, 322);
            Durability(delta, 0, 9, 10402, 33);
            peer.Barrier();
            peer.Send(23, new byte[6]);
            peer.Closed();
        }
        ushort dungeonSlot;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repo.Create(owner, new("QuickRepair", towns.Room, towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = characters.Get(0).Skills,
                DungeonState = CharacterDungeonState.NewOrdinary,
                Inventory = new([new(0, new(InventoryListType.Main, 3), low), new(0, new(InventoryListType.Main, 9), high),
                    new(0, new(InventoryListType.Equipment, 14), high)], Gold: 1000)
            });
            dungeonSlot = role.Slot;
        }
        using (var peer = Open(fixture))
        {
            peer.Select(dungeonSlot);
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            string before = Snapshot();
            Send(peer, 0, 9, true);
            peer.Expect(1, 23, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, "加载阶段不能紧急修理或扣金币");
            FormalDungeonTcpTests.Load(peer);
            Send(peer, 0, 9, false);
            peer.Expect(1, 23, [0, 19, 0]);
            Send(peer, 2, 0, true);
            peer.Expect(1, 23, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, "战斗中的普通NPC及仓库修理仍按原场景资格拒绝");
            Send(peer, 0, 9, true);
            Ack(peer, 760, 0, 9);
            var delta = peer.Expect(0, 14).Body;
            Gold(delta, 760);
            Durability(delta, 0, 9, 10402, 33);
            peer.Barrier();
            before = Snapshot();
            Send(peer, 0, 9, true);
            Ack(peer, 760, 0, 9);
            peer.Barrier();
            That(Snapshot() == before, "战斗紧急修理重复请求不再次扣费、提交或审计");
            fixture.Sql("CREATE TRIGGER fail_dungeon_repair BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'dungeon repair failure'); END");
            Send(peer, 0, -1, true);
            peer.Closed();
            That(Snapshot() == before, "图内全部紧急修理SQL失败不留下扣费或部分耐久");
            fixture.Sql("DROP TRIGGER fail_dungeon_repair");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(dungeonSlot);
            // 重连回到赛丽亚房间；再走入口进入真实副本。
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer);
            FormalDungeonTcpTests.Load(peer);
            Send(peer, 0, -1, true);
            Ack(peer, 492, 0, -1);
            var packets = PartyDungeonTcpTests.Drain(peer);
            var main = packets.Single(packet => packet.Kind == 0 && packet.Opcode == 14 && packet.Body[0] == 0).Body;
            Gold(main, 492);
            Durability(main, 0, 3, 10400, 33);
            Durability(packets.Single(packet => packet.Kind == 0 && packet.Opcode == 14 && packet.Body[0] == 3).Body, 3, 14, 10402, 33);
            That(packets.Count(packet => packet.Kind == 0 && packet.Opcode == 2) == 1,
                "图内穿戴修理只刷新当前Player，原快速倍率按每件累计扣268金币");
            string before = Snapshot();
            peer.Send(23, [0, 255, 255, 255, 255, 1, 0]);
            peer.Expect(1, 23, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before, "真实战斗中的自动修理仍要求有效契约，不借快速修理授予资格");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(dungeonSlot);
            Send(peer, 0, 3, true);
            Ack(peer, 492, 0, 3);
            peer.Barrier();
            That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id) is [[492L]],
                "重连回放已提交的紧急修理耐久和绝对余额，满耐久免重复扣费");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "repair TCP database integrity");

        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters ORDER BY id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM currency_audit_log"));
    }

    static void Send(TcpPeer peer, byte list, short slot, bool quick) => peer.Send(23, Build(w =>
    {
        w.Write(list); w.Write(slot); w.Write((short)-1); w.Write((ushort)0); w.Write(quick ? (byte)1 : (byte)0);
    }));
    static void Ack(TcpPeer peer, int gold, byte list, short slot) => peer.Expect(1, 23, Build(w =>
    {
        w.Write((byte)1); w.Write(gold); w.Write(list); w.Write(slot); w.Write((ushort)0);
    }));
    static int Entry(byte[] delta, short slot)
    {
        That(delta.Length == 3 + 123 * U16(delta, 1), "repair delta complete entry length");
        return Enumerable.Range(0, U16(delta, 1)).Select(index => 3 + index * 123).Single(at => U16(delta, at) == slot);
    }
    static void Gold(byte[] delta, int value)
    {
        int at = Entry(delta, 0);
        That(delta[0] == 0 && U32(delta, at + 2) == 0 && U32(delta, at + 6) == value, "repair authoritative gold delta");
    }
    static void Durability(byte[] delta, byte list, short slot, int item, ushort value)
    {
        int at = Entry(delta, slot);
        That(delta[0] == list && U32(delta, at + 2) == item && U16(delta, at + 11) == value, "repair authoritative durability delta");
    }
}
