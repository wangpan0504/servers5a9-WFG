using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class RandomOptionTcpTests
{
    static ItemCore ChangeCore(TcpTestResources resources) =>
        InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(100060156)) with
        {
            Attr = 7, EnchantCardId = 3175, EnchantUpgrade = 2, AmplifyType = 1, AmplifyValue = 42, Rune = 8,
            GenuineUpgrade = 3, SealFlag = 1, SortLockFlag = 1, Extension97 = 0x12345678, GuardianGemKeys = 0x123456789ABCDEF0,
            Random0 = new(70, 12, 13), Random1 = new(71, 20, 21), Random2 = new(72, 30, 31),
            RandomState = 7, RandomChangedIndex = 255
        };

    static void VerifyChange(TcpTestFixture fixture, TcpTestResources resources)
    {
        var items = resources.ItemResources;
        var template = items.Items.Get(100060156);
        var definition = items.RandomOptions.ResolveChange(template)!;
        That(template.MinimumLevel == 10 && definition.GoldCost == 100 &&
            definition.Types.Select(row => row.Weight).SequenceEqual(new long[] { 1500, 7000, 1500 }),
            "原版PVF长袍变换使用独立1500/7000/1500类型权重及100金币，不套解封费用");
        var original = ChangeCore(resources);
        var service = new InventoryRandomOptionService(items.Items, items.RandomOptions,
            _ => throw new InvalidOperationException("拒绝不能随机"));
        foreach (var (invalid, gold, error) in new (ItemCore Core, uint Gold, ushort Error)[]
        {
            (original with { ExpireTime = 1 }, 100, 19), (original with { IsRental = true }, 100, 19),
            (original with { EquipmentLockId = 1 }, 100, 0xD5), (original, 99, 10),
            (original with { Random0 = default, Random1 = default, Random2 = default }, 100, 19)
        })
        {
            var role = new Character(1, 0, "ChangeRules", 0, resources.Towns.Room, null)
                { Inventory = new([new(77, Bag(13), invalid)], Gold: gold) };
            That(service.Change(role, new(13, 0, 1)) is { Accepted: false, Error: var actual } && actual == error &&
                role.Inventory.Gold == gold && role.Inventory.Items.Single().Core == invalid,
                "期限、租赁、安全锁、费用及未解封资格在随机前拒绝，原输入不变");
        }
        ItemCore? last = null;
        long uid = Convert.ToInt64(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE list_type=0 AND slot_index=13")[0][0]);
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713), (0, 251)]);
            string before = Snapshot();
            foreach (var (body, error) in new (byte[], byte)[]
            {
                ([14, 0, 0, 0, 1], 0xD5), ([15, 0, 0, 0, 1], 19), ([16, 0, 0, 0, 1], 19), ([17, 0, 0, 0, 1], 19),
                ([200, 0, 0, 0, 1], 19), ([255, 255, 0, 0, 1], 19), ([13, 0, 1, 0, 1], 19), ([13, 0, 0, 0, 3], 19)
            })
            {
                peer.Send(437, body); peer.Expect(1, 437, [0, error, 0]);
            }
            foreach (int size in new[] { 0, 4, 6 })
            {
                peer.Send(437, new byte[size]); peer.Expect(1, 437, [0, 19, 0]);
            }
            peer.Barrier();
            That(Snapshot() == before, "拒绝及畸形437使用完整3B失败，连接继续且不改资产/候选");
            Change(peer, 600);
            Change(peer, 500);
            before = Snapshot();
            peer.Send(437, [13, 0, 0, 0, 0]); peer.Expect(1, 437, [0, 4, 0]); peer.Barrier();
            That(Snapshot() == before, "已有候选后禁止另选词条，不能二次扣费或换索引");
            fixture.Sql("CREATE TRIGGER fail_option_change AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'change rollback'); END");
            peer.Send(437, [13, 0, 0, 0, 1]); peer.Closed();
            That(Snapshot() == before, "金币和核心更新后的审计失败整体回滚，不提前成功或保留半份候选");
            fixture.Sql("DROP TRIGGER fail_option_change");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(inventoryNotices: [(1, 713), (0, 251)]);
            var full = entry.First(packet => packet.Kind == 0 && packet.Opcode == 13 && packet.Body[0] == 0).Body;
            var row = Row(full, 13, true);
            That(Read() == last && row[82] == 1 && row[83] == last!.RandomChangeState && row[84] == last.RandomChange.Type,
                "重连保留同一UID、原三词条及完整候选，SQL回滚不覆盖已提交结果");
            Change(peer, 400);
            FormalDungeonTcpTests.EntryTown(peer);
            peer.Send(15, new byte[4]); peer.Expect(1, 15, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]); peer.Expect(0, 27, new byte[42]);
            peer.Send(16, Convert.FromHexString("030000000000000000FFFF0000000000000000000000000000"));
            peer.Expect(0, 28); peer.Expect(0, 29);
            string before = Snapshot();
            peer.Send(437, [13, 0, 0, 0, 1]); peer.Expect(1, 437, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == before, "加载中拒绝437，不让合法目标绕过user细阶段门禁");
            FormalDungeonTcpTests.Load(peer); peer.Expect(0, 36);
            Change(peer, 300);
        }
        void Change(TcpPeer peer, uint gold)
        {
            peer.Send(437, [13, 0, 0, 0, 1]);
            var delta = peer.Expect(0, 14).Body;
            var row = Row(delta, 13);
            peer.Expect(1, 713); // 原排序锁沿完整差量恢复。
            peer.Expect(1, 437, [1]); peer.Barrier();
            last = Read();
            That(U16(delta, 1) == 2 && U32(Row(delta, 0), 6) == gold &&
                fixture.Query("SELECT gold FROM characters") is [[var savedGold]] && Equals(savedGold, (long)gold),
                "提交后先发Main目标与金币绝对差量，最后1B ACK437；每次有效变换正常扣费");
            That(last.RandomChangedIndex == 1 && (last.RandomChangeState & 3) == 1 && last.RandomChange.Type is not (0 or 70 or 72) &&
                (last with { RandomChangedIndex = 255, RandomChangeState = 0, RandomChange = default }) == original &&
                row[71] == 3 && row[82] == 1 && row[83] == last.RandomChangeState && row[84] == last.RandomChange.Type &&
                row[85] == last.RandomChange.Value1 && row[86] == last.RandomChange.Value2,
                "变换只修改独立候选及索引，保留完整原词条、强化/附魔/增幅/锻造/符文与扩展核心");
        }

        ItemCore Read()
        {
            var saved = fixture.Query("SELECT item_uid,item_core FROM character_inventory_items WHERE item_uid=$p0", uid).Single();
            That(Convert.ToInt64(saved[0]) == uid, "变换仍使用原实例UID");
            return JsonSerializer.Deserialize<ItemCore>((string)saved[1]!)!;
        }

        string Snapshot() => fixture.Snapshot() + fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT count(*) FROM inventory_audit_log") + fixture.Snapshot("SELECT count(*) FROM currency_audit_log");
    }
}
