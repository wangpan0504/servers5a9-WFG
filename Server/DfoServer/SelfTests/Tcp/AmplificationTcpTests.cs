using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AmplificationTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        AmplificationTests.RunResources(resources.ItemResources);
        const int weapon = 31440;
        var items = resources.ItemResources.Items;
        var core = InventoryRewardGrantService.Create(items.Get(weapon)) with
        { Attr = 0xE0, EnchantCardId = 3175, EnchantUpgrade = 2, GenuineUpgrade = 4, Rune = 8,
            Random0 = new(1, 2, 3), Extension114 = new(1, 333) };
        var initial = new List<InventoryItem>
        {
            new(0, Main(9), core),
            new(0, Main(10), core with { AmplifyType = 0x83, AmplifyValue = 51 }),
            new(0, Main(11), core with { AmplifyType = 0x84, AmplifyValue = 9 }),
            new(0, Main(12), core with { Attr = 0xEB, AmplifyType = 3, AmplifyValue = 50 })
        };
        foreach (var (slot, itemId) in new (short, int)[] { (65, 1184), (66, 1284), (67, 1183), (68, 10000704), (69, 8238), (70, 490705006) })
            initial.Add(new(0, Main(slot), InventoryRewardGrantService.Create(items.Get(itemId), 2) with { ExpireTime = 0 }));
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "amplification-options"), resources: resources);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new CharacterRepository(fixture.Database).Create(owner, new("AmplifyTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            { Job = 2, Level = 75, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(2).Skills, Inventory = new([..initial], Gold: 1234) });
        string uids = fixture.Snapshot("SELECT item_uid FROM character_inventory_items ORDER BY item_uid");
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            var selected = peer.Select();
            var initialList = selected.First(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            foreach (short slot in new short[] { 10, 11 })
            {
                int at = Enumerable.Range(0, U16(initialList, 3)).Select(index => 5 + index * 123)
                    .Single(offset => U16(initialList, offset) == slot);
                That(initialList[at + 19] == 128 && U16(initialList, at + 20) == 0 &&
                    Saved(slot).AmplifyType is 0x83 or 0x84 && Saved(slot).AmplifyValue > 0,
                    "真实选角下发128/0安全未净化状态，数据库仍保留隐藏类型与原值");
            }
            string before = Snapshot();
            peer.Send(205, [2]); peer.Expect(1, 205, [0, 17, 0]);
            peer.Send(204, new byte[11]); peer.Expect(1, 204, [0, 17, 0]);
            peer.Send(205, AmplificationTests.Request(0, 9, weapon + 1, 65, 1184, 0)); peer.Expect(1, 205, [0, 17, 0]);
            peer.Barrier();
            That(Snapshot() == before, "204/205畸形和身份不符均有完整失败回执，不写库且同连接继续");
            peer.Send(205, AmplificationTests.Request(0, 9, weapon, 65, 1184, 0));
            peer.Expect(1, 205, [1, 0, 65, 0, 1, 0, 0, 0, 9, 0, 3, 6, 0]); peer.Barrier();
            peer.Send(205, AmplificationTests.Request(1, 9, weapon, 66, 1284, 3)); peer.Expect(1, 205, [0, 23, 0]);
            peer.Send(205, AmplificationTests.Request(1, 9, weapon, 66, 1284, 4));
            peer.Expect(1, 205, [1, 1, 66, 0, 1, 0, 0, 0, 9, 0, 4, 6, 0]); peer.Barrier();
            That(Saved(9) == core with { AmplifyType = 4, AmplifyValue = 6 },
                "赋予与扭转真实提交完整核心，ACK后不重建目标或重复发送源扣除");
            before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_amplification_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='equipment_amplification_option' AND NEW.slot_index=69 BEGIN SELECT RAISE(ABORT,'amplification rollback'); END");
            peer.Send(205, GoldRequest()); peer.Closed();
            That(Snapshot() == before, "黄金书目标及材料更新后的审计失败整笔回滚，不提前回成功或重抽");
            fixture.Sql("DROP TRIGGER fail_amplification_late");
        }
        byte[]? persisted = null;
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Send(205, GoldRequest());
            var gold = peer.Expect(1, 205).Body;
            That(gold.Length == 14 && gold.AsSpan(0, 13).SequenceEqual(new byte[] { 1, 2, 69, 0, 1, 0, 0, 0, 9, 0, 3, 6, 0 }) &&
                gold[13] is >= 3 and <= 6, "原生黄金增幅书真实14B回执包含PVF抽取等级及绝对材料余量");
            peer.Barrier();
            That(Saved(9) == core with { Attr = (byte)(0xE0 | gold[13]), AmplifyType = 3, AmplifyValue = 6 },
                "黄金书保存实际结果等级，保留UID及所有其余属性");
            string before = Snapshot();
            peer.Send(205, GoldRequest()); peer.Expect(1, 205, [0, 23, 0]); peer.Barrier();
            That(Snapshot() == before, "黄金书相同属性重放拒绝，余量不重复扣除");
            peer.Send(204, AmplificationTests.PurifyRequest(10, weapon, 67, 1183));
            peer.Expect(1, 204, [1, 67, 0, 1, 0, 0, 0, 10, 0, 3, 51, 0]); peer.Barrier();
            peer.Send(204, AmplificationTests.PurifyRequest(11, weapon, 68, 10000704));
            peer.Expect(1, 204, [1, 68, 0, 1, 0, 0, 0, 11, 0, 0, 0, 0]); peer.Barrier();
            peer.Send(204, AmplificationTests.PurifyRequest(12, weapon, 70, 490705006));
            peer.Expect(1, 204, [1, 70, 0, 1, 0, 0, 0, 12, 0, 0, 0, 0]);
            var empty = peer.Expect(0, 1681).Body;
            That(empty.Length == 126 && U16(empty, 3) == 12 && U32(empty, 5) == uint.MaxValue,
                "黄金清除沿共享无播报更新先清目标旧对象，不重复扣源");
            var delta = peer.Expect(0, 1681).Body;
            That(delta.Length == 126 && delta[0] == 0 && U16(delta, 1) == 1 && U16(delta, 3) == 12 &&
                delta[13] == 0xE0 && delta[22] == 0 && U16(delta, 23) == 0,
                "黄金清除ACK之后仅更新目标完整行，补上204原生回执不携带的等级归零");
            persisted = delta.AsSpan(3).ToArray(); peer.Barrier();
            That(Saved(10) == core with { AmplifyType = 3, AmplifyValue = 51 } && Saved(11) == core && Saved(12) == core,
                "净化保留掉落原值，普通及黄金清除恢复无异次元核心，其他字段未丢失");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            var full = entry.First(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            int count = U16(full, 3);
            int at = Enumerable.Range(0, count).Select(index => 5 + index * 123).Single(offset => U16(full, offset) == 12);
            Bytes(full.AsSpan(at, 123).ToArray(), persisted!, "重新选角完整恢复黄金清除后的123B装备行"); peer.Barrier();
        }
        That(Convert.ToInt64(fixture.Query("SELECT gold FROM characters").Single().Single()) == 1234 &&
            fixture.Snapshot("SELECT item_uid FROM character_inventory_items ORDER BY item_uid") == uids &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "异次元属性操作不更换UID或扣普通金币，持久化及外键一致");

        ItemCore Saved(short slot) => System.Text.Json.JsonSerializer.Deserialize<ItemCore>((string)fixture.Query(
            "SELECT item_core FROM character_inventory_items JOIN character_inventory_slots USING(item_uid) WHERE list_type=0 AND slot_index=$p0", slot).Single().Single())!;
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold FROM characters") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        static byte[] GoldRequest() => AmplificationTests.Request(2, 9, weapon, 69, 8238, 3, "amplify-test-item"u8.ToArray());
        static InventorySlot Main(short slot) => new(InventoryListType.Main, slot);
    }
}
