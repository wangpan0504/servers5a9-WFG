using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EnhancementTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        const int coupon = 490705402, sword = 27308;
        const short sourceSlot = 86;
        var items = resources.ItemResources.Items;
        var core = InventoryRewardGrantService.Create(items.Get(sword)) with
        { Attr = 0xE7, Durability = 3, GenuineUpgrade = 4, EnchantCardId = 3175, EnchantUpgrade = 2,
            Rune = 8, SortLockFlag = 1, Random0 = new(1, 2, 3), Extension114 = new(1, 333) };
        var ticket = InventoryRewardGrantService.Create(items.Get(coupon), 3) with { ExpireTime = 0, SortLockFlag = 1 };
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "enhancement"), resources: resources);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new CharacterRepository(fixture.Database).Create(owner, new("EnhanceTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 25, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Main(sourceSlot), ticket), new(0, Main(21), core),
                    new(0, new(InventoryListType.Equipment, 12), core)], Gold: 1234)
            });
        string uids = Uids();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713), (1, 713), (1, 714)]);
            string before = Snapshot();
            peer.Send(80, EnhancementTests.Request(1, 0, 21, sword, sourceSlot, [])); peer.Expect(1, 80, [0, 23, 0]);
            peer.Send(80, EnhancementTests.Request(0, 0, 21, sword + 1, sourceSlot, [])); peer.Expect(1, 80, [0, 23, 0]);
            peer.Send(80, new byte[19]); peer.Expect(1, 80, [0, 23, 0]); peer.Barrier();
            That(Snapshot() == before, "CMD80错误模式、目标身份及畸形正文不扣券或改变版本，失败后连接继续");
            fixture.Sql("CREATE TRIGGER fail_enhancement_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='equipment_enhancement_ticket' AND NEW.slot_index=86 BEGIN SELECT RAISE(ABORT,'enhancement rollback'); END");
            Send(peer, 0, 21); peer.Closed();
            That(Snapshot() == before, "完整核心与源券写入后审计末段失败同事务回滚，未发送成功");
            fixture.Sql("DROP TRIGGER fail_enhancement_late");
        }
        byte[]? saved = null;
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(inventoryNotices: [(1, 713), (1, 713), (1, 714)]);
            var full = entry.First(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            var expected = Row(full, 21, true); expected[10] = 0xEB;
            Send(peer, 0, 21); Ack(peer, 7, 2, 0, 21);
            var delta = peer.Expect(0, 14).Body;
            saved = Row(delta, 21);
            Bytes(saved, expected, "实际+11券只覆盖Attr低五位，保留完整武器行和所有其他字段");
            That(U32(Row(delta, sourceSlot), 6) == 2, "差量源数量与ACK绝对余量一致");
            peer.Expect(1, 713, [1, 0, 21, 0]);
            peer.Expect(1, 713, [1, 0, (byte)sourceSlot, 0]);
            Send(peer, 0, 21); Ack(peer, 11, 1, 0, 21);
            delta = peer.Expect(0, 14).Body;
            That(U16(delta, 1) == 1 && U32(Row(delta, sourceSlot), 6) == 1,
                "固定券同等级操作仍只扣一券，目标无变化不发送伪差量");
            peer.Expect(1, 713, [1, 0, (byte)sourceSlot, 0]);
            Send(peer, 3, 12); Ack(peer, 7, 0, 3, 12);
            var clear = peer.Expect(0, 14).Body;
            That(U32(Row(clear, sourceSlot), 2) == uint.MaxValue, "最后一券清源槽");
            var worn = peer.Expect(0, 14).Body;
            That(worn[0] == 3 && Row(worn, 12)[10] == 0xEB, "穿戴目标同步同一提交的完整+11核心");
            That(peer.Expect(0, 2).Body[0] == 0 && peer.Expect(0, 2).Body[0] == 1, "穿戴强化沿共享Actor/Player刷新");
            peer.Expect(1, 714, [1, 3, 12, 0]);
            peer.Barrier();
            string before = Snapshot();
            Send(peer, 0, 21); peer.Expect(1, 80, [0, 23, 0]); peer.Barrier();
            That(Snapshot() == before, "源耗尽重放拒绝且不重复扣费或修改核心");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(inventoryNotices: [(1, 713), (1, 714)]);
            var full = entry.First(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            Bytes(Row(full, 21, true), saved!, "重连完整恢复真实券写入的+11武器行"); peer.Barrier();
        }
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var savedRole = new CharacterRepository(fixture.Database).List(owner).Single();
            That(savedRole.Inventory.Find(Main(sourceSlot)) is null && savedRole.Inventory.Gold == 1234 &&
                savedRole.Inventory.Find(Main(21))!.Core == core with { Attr = 0xEB } &&
                savedRole.Inventory.Find(new(InventoryListType.Equipment, 12))!.Core == core with { Attr = 0xEB },
                "重开仓储保留源耗尽与完整目标属性，不扣普通金币或锻造值");
        }
        That(Uids() == uids && fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "强化不更换目标UID、不损坏外键或数据库");

        string Uids() => fixture.Snapshot("SELECT i.item_uid,s.list_type,s.slot_index FROM character_inventory_items i " +
            "JOIN character_inventory_slots s USING(item_uid) WHERE json_extract(i.item_core,'$.ItemId')<>$p0 ORDER BY i.item_uid", coupon);
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") + fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") + fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
        static InventorySlot Main(short slot) => new(InventoryListType.Main, slot);
        static void Send(TcpPeer peer, byte list, short slot) => peer.Send(80, EnhancementTests.Request(0, list, slot, sword, sourceSlot, new byte[8]));
        static void Ack(TcpPeer peer, byte oldLevel, int remaining, byte list, short slot)
        {
            var body = peer.Expect(1, 80).Body;
            That(body.Length == 27 && body[0] == 1 && body[1] == 0 && U16(body, 2) == sourceSlot && U32(body, 4) == remaining &&
                U16(body, 8) == ushort.MaxValue && body[10] == 0 && body[11] == oldLevel && body[12] == 0 && body[13] == 11 &&
                body[14] == list && U16(body, 15) == slot && U16(body, 17) == ushort.MaxValue && U16(body, 19) == ushort.MaxValue &&
                U32(body, 21) == 0 && body[25] == 0 && body[26] == 0, "真实CMD80完整27B回包与提交结果一致");
        }
        static byte[] Row(byte[] body, short slot, bool full = false)
        {
            int head = full ? 5 : 3, count = U16(body, full ? 3 : 1);
            That(body.Length == head + count * 123, "强化差量及全量保留完整123B物品合同");
            int at = Enumerable.Range(0, count).Select(index => head + index * 123).Single(offset => U16(body, offset) == slot);
            return body.AsSpan(at, 123).ToArray();
        }
    }
}
