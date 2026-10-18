using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EnchantmentTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        const int beadId = 490705314, cardId = 10309006;
        const short sourceSlot = 82;
        var items = resources.ItemResources.Items;
        ItemCore Core(int id) => InventoryRewardGrantService.Create(items.Get(id)) with
        {
            Attr = 7, GenuineUpgrade = 4, AmplifyType = 2, AmplifyValue = 21,
            Rune = 8, EnchantCardId = 3175, EnchantUpgrade = 2, SortLockFlag = 1, Random0 = new(1, 2, 3)
        };
        var wrist = Core(22004);
        var ring = Core(24002);
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "enchantment"), resources: resources);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new CharacterRepository(fixture.Database).Create(owner, new("EnchantTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 25, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([
                    new(0, Main(sourceSlot), InventoryRewardGrantService.Create(items.Get(beadId), 3) with { SortLockFlag = 1 }),
                    new(0, Main(20), wrist with { EnchantCardId = 0, EnchantUpgrade = 0 }),
                    new(0, Main(23), ring), new(0, Main(9), Core(27308)),
                    new(0, new(InventoryListType.Equipment, 20), wrist)], Gold: 1234)
            });
        string originalUids = Uids();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713), (1, 713), (1, 713), (1, 713), (1, 713)]);
            string before = Snapshot();
            foreach (short invalid in new short[] { 9, sourceSlot, 100 })
            {
                Send(peer, 0, invalid); peer.Expect(1, 272, [0, 23, 0]);
            }
            peer.Send(272, new byte[9]); peer.Expect(1, 272, [0, 23, 0]); peer.Barrier();
            That(Snapshot() == before, "普通武器、源目标相同、空槽及畸形272不扣宝珠或改变任何核心");
            fixture.Sql("CREATE TRIGGER fail_bead_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='enchant_by_bead' AND NEW.slot_index=82 BEGIN SELECT RAISE(ABORT,'enchantment rollback'); END");
            Send(peer, 0, 20); peer.Closed();
            That(Snapshot() == before, "源与目标SQL已写后审计末段失败，完整资产及版本回滚且未先发送成功");
            fixture.Sql("DROP TRIGGER fail_bead_late");
        }
        byte[]? savedWrist = null, savedRing = null;
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(inventoryNotices: [(1, 713), (1, 713), (1, 713), (1, 713), (1, 713)]);
            var full = entry.First(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            foreach (var (slot, remaining) in new[] { ((short)20, 2), ((short)23, 1) })
            {
                Send(peer, 0, slot);
                var delta = peer.Expect(0, 14).Body;
                That(delta[0] == 0 && U16(delta, 1) == 2, "成功窗打开前完整差量同步真实源数量和目标核心");
                var row = Row(delta, slot);
                var original = Row(full, slot, full: true);
                That(U32(row, 14) == cardId && row[18] == 0 && row.AsSpan(0, 14).SequenceEqual(original.AsSpan(0, 14)) &&
                    row.AsSpan(19).SequenceEqual(original.AsSpan(19)),
                    "实际首饰宝珠只改Card/index，强化增幅锻造、期限、符文与其他字段逐字节保留");
                var source = Row(delta, sourceSlot);
                That(U32(source, 2) == beadId && U32(source, 6) == remaining, "源差量返回提交后剩余量，没有ACK重复扣源");
                if (slot == 20) savedWrist = row; else savedRing = row;
                peer.Expect(1, 713, [1, 0, (byte)slot, 0]);
                peer.Expect(1, 713, [1, 0, (byte)sourceSlot, 0]);
                // ACK272立即按当前目标附魔创建结果窗；首次与覆盖附魔都不能读到旧核心。
                peer.Expect(1, 272, [1, 0, (byte)slot, 0]);
                peer.Barrier();
            }
            Send(peer, 3, 20);
            var clear = peer.Expect(0, 14).Body;
            That(clear[0] == 0 && U32(Row(clear, sourceSlot), 2) == uint.MaxValue, "最后一件宝珠由源差量清槽");
            var equipment = peer.Expect(0, 14).Body;
            That(equipment[0] == 3 && U32(Row(equipment, 20), 14) == cardId && Row(equipment, 20)[18] == 0,
                "穿戴首饰同步同一真实附魔核心");
            That(peer.Expect(0, 2).Body[0] == 0 && peer.Expect(0, 2).Body[0] == 1,
                "穿戴附魔沿共享Actor/Player刷新，不向CHR基础属性重复叠加宝珠效果");
            peer.Expect(1, 713, [1, 3, 20, 0]);
            peer.Expect(1, 272, [1, 3, 20, 0]);
            peer.Barrier();
            string before = Snapshot();
            Send(peer, 0, 23); peer.Expect(1, 272, [0, 23, 0]); peer.Barrier();
            That(Snapshot() == before, "耗尽源不能重放附魔，拒绝后连接继续且无重复效果或扣费");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(inventoryNotices: [(1, 713), (1, 713), (1, 713), (1, 713)]);
            var full = entry.First(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            Bytes(Row(full, 20, full: true), savedWrist!, "重连恢复真实护腕完整附魔行");
            Bytes(Row(full, 23, full: true), savedRing!, "重连恢复真实戒指完整附魔行");
            peer.Barrier();
        }
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var saved = new CharacterRepository(fixture.Database).List(owner).Single();
            That(saved.Inventory.Find(Main(sourceSlot)) is null && saved.Inventory.Gold == 1234 &&
                saved.Inventory.Find(Main(20))!.Core == wrist with { EnchantCardId = cardId, EnchantUpgrade = 0 } &&
                saved.Inventory.Find(Main(23))!.Core == ring with { EnchantCardId = cardId, EnchantUpgrade = 0 } &&
                saved.Inventory.Find(new(InventoryListType.Equipment, 20))!.Core == wrist with { EnchantCardId = cardId, EnchantUpgrade = 0 },
                "重开仓储保留实际Card/index和其他完整属性，不消费金币或另造效果实例");
        }
        That(Uids() == originalUids && fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "附魔保留所有目标持久UID，源耗尽后没有孤儿或外键损坏");

        string Uids() => fixture.Snapshot("SELECT i.item_uid,s.list_type,s.slot_index FROM character_inventory_items i " +
            "JOIN character_inventory_slots s USING(item_uid) WHERE json_extract(i.item_core,'$.ItemId')<>$p0 ORDER BY i.item_uid", beadId);
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
        static InventorySlot Main(short slot) => new(InventoryListType.Main, slot);
        static void Send(TcpPeer peer, byte list, short slot) => peer.Send(272, Build(writer =>
        { writer.Write((byte)0); writer.Write(sourceSlot); writer.Write(list); writer.Write(slot); writer.Write(0u); }));
        static byte[] Row(byte[] body, short slot, bool full = false)
        {
            int head = full ? 5 : 3;
            int count = U16(body, full ? 3 : 1);
            That(body.Length == head + count * 123, "普通附魔条目差量保持完整123B合同");
            int offset = Enumerable.Range(0, count).Select(index => head + index * 123).Single(at => U16(body, at) == slot);
            return body.AsSpan(offset, 123).ToArray();
        }
    }
}
