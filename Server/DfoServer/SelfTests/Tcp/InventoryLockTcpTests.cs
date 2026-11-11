using DfoServer.Game.Accounts;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class InventoryTcpTests
{
    static async Task VerifyLocksAsync(ServerOptions source, string root, CharacterCatalog characters, TownCatalog towns, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, root, resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new GameSession(repository, towns, owner).CreateCharacter("BagLockTest", characters.Get(0));
        int weaponId = characters.Get(0).Equipment.Single(item => item.Slot == 12).Template.Id;
        var uid = fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE list_type=3 AND slot_index=12")[0][0];
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            string before = Snapshot(fixture);
            foreach (ushort opcode in new ushort[] { 267, 268, 269, 713, 714 })
            {
                peer.Send(opcode, [3, 12, 0]);
                peer.Expect(1, opcode, opcode == 268 ? [0, 19, 0, 0, 0, 0, 0] : opcode >= 713 ? [0, 19, 0, 0] : [0, 19, 0]);
            }
            That(Snapshot(fixture) == before, "unselected lock commands preserve all assets");
            var entry = peer.Select();
            That(U16(entry[4].Body, 1) == 24 && (U16(entry[4].Body, 1) >> 3) == 3,
                "full bag sends 24 slots which the client decodes as three expansion stages");
            peer.Send(267, [3, 12, 0]);
            peer.Expect(1, 267, [1, 3, 12, 0]);
            peer.Expect(0, 251, [1, 0, 3, 12, 0, 1]);
            before = Snapshot(fixture);
            peer.Send(267, [3, 12, 0]);
            peer.Expect(1, 267, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot(fixture) == before, "duplicate safety lock does not allocate another ID");
            peer.Send(19, MoveItem(3, 12, weaponId, 1, 0, 64, 0));
            peer.Expect(1, 19, MoveAck(3, 12, 1, 0, 64));
            WearDelta(peer);
            peer.Expect(0, 251, [1, 0, 0, 64, 0, 1]);
            peer.Send(713, [0, 64, 0]);
            peer.Expect(1, 713, [1, 0, 64, 0]);
            before = Snapshot(fixture);
            peer.Send(713, [0, 64, 0]);
            peer.Expect(1, 713, [1, 0, 64, 0]);
            peer.Send(20, Convert.FromHexString("080000000814100018012000"));
            peer.Expect(1, 20, Convert.FromHexString("010400000008001000"));
            peer.Barrier();
            That(Snapshot(fixture) == before, "locked final page slot stays fixed and repeated sorting lock is idempotent");
            peer.Send(19, MoveItem(0, 64, weaponId, 1, 0, 8, 0));
            peer.Expect(1, 19, MoveAck(0, 64, 1, 0, 8));
            peer.Expect(1, 714, [1, 0, 8, 0]);
            peer.Expect(0, 251, [1, 0, 0, 8, 0, 1]);
            That(Equals(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE list_type=0 AND slot_index=8")[0][0], uid),
                "locked equipment remains original instance in quick bar");
            peer.Send(19, MoveItem(0, 8, weaponId, 1, 3, 12, 0));
            peer.Expect(1, 19, MoveAck(0, 8, 1, 3, 12));
            WearDelta(peer, emptyQuick: true);
            peer.Expect(0, 251, [1, 0, 3, 12, 0, 1]);
            peer.Send(713, [3, 12, 0]);
            peer.Expect(1, 713, [1, 3, 12, 0]);
            peer.Expect(1, 714, [1, 3, 12, 0]);
            peer.Send(19, MoveItem(3, 12, weaponId, 1, 0, 64, 0));
            peer.Expect(1, 19, MoveAck(3, 12, 1, 0, 64));
            WearDelta(peer);
            peer.Expect(1, 713, [1, 0, 64, 0]);
            peer.Expect(0, 251, [1, 0, 0, 64, 0, 1]);
            peer.Send(19, MoveItem(0, 64, weaponId, 1, 3, 12, 0));
            peer.Expect(1, 19, MoveAck(0, 64, 1, 3, 12));
            WearDelta(peer);
            peer.Expect(1, 714, [1, 3, 12, 0]);
            peer.Expect(0, 251, [1, 0, 3, 12, 0, 1]);
            That(fixture.Query("SELECT json_extract(item_core,'$.SortLockFlag') FROM character_inventory_items WHERE item_uid=$p0", uid) is [[1L]],
                "穿戴只隐藏蓝锁，原实例的排序锁仍保存，卸下会恢复");
            peer.Send(714, [3, 12, 0]); peer.Expect(1, 714, [1, 3, 12, 0]);
            peer.Send(268, [3, 12, 0]);
            peer.Expect(1, 268, [1, 3, 12, 0, 0, 0, 0, 0]);
            peer.Expect(0, 252, [3, 12, 0]);
            peer.Send(269, [3, 12, 0]);
            peer.Expect(1, 269, [0, 19, 0]);
            peer.Send(267, [3, 12, 0]);
            peer.Expect(1, 267, [1, 3, 12, 0]);
            peer.Expect(0, 251, [1, 0, 3, 12, 0, 1]);
            before = Snapshot(fixture);
            peer.Send(269, [3, 12, 0]);
            peer.Expect(1, 269, [1, 3, 12, 0]);
            peer.Expect(0, 251, [1, 0, 3, 12, 0, 1]);
            peer.Barrier();
            That(Snapshot(fixture) == before, "cancel confirms active lock without duplicate writes");
            foreach (ushort opcode in new ushort[] { 267, 268, 269, 713, 714 })
            {
                peer.Send(opcode, [0, 9, 0]);
                peer.Expect(1, opcode, opcode == 268 ? [0, 21, 0, 0, 0, 0, 0] : opcode >= 713 ? [0, 21, 0, 0] : [0, 21, 0]);
            }
            peer.Barrier();
            That(Snapshot(fixture) == before, "empty targets reject all lock operations without writes");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("reopened");
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(inventoryNotices: [(0, 251)]);
            Bytes(entry[8].Body, [1, 0, 3, 12, 0, 1], "restarted session restores moved equipment lock");
            string before = Snapshot(fixture);
            fixture.Sql("CREATE TRIGGER fail_tcp_lock BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'lock failure'); END");
            peer.Send(268, [3, 12, 0]);
            peer.Closed();
            That(Snapshot(fixture) == before, "failed lock transaction closes without success ACK or partial writes");
            fixture.Sql("DROP TRIGGER fail_tcp_lock");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(0, 251)]);
            peer.Send(268, [3, 12, 0]);
            peer.Expect(1, 268, [1, 3, 12, 0, 0, 0, 0, 0]);
            peer.Expect(0, 252, [3, 12, 0]);
            peer.Send(713, [0, 9]);
            peer.Closed();
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]], "TCP locking database integrity");

        static void WearDelta(TcpPeer peer, bool emptyQuick = false)
        {
            if (emptyQuick)
            {
                var emptied = peer.Expect(0, 14).Body;
                That(emptied[0] == 0 && U16(emptied, 3) is >= 3 and <= 8 && U32(emptied, 5) == uint.MaxValue,
                    "锁定装备穿出快捷栏先清空3–8，再全量载入");
            }
            var main = peer.Expect(0, 13).Body;
            That(main[0] == 0 && main.Length == 5 + 123 * U16(main, 3),
                "锁定装备穿脱用NOTI13全量载入，不发14/1681新物品标记");
            peer.Expect(0, 2);
            peer.Expect(0, 2);
        }
    }
}
