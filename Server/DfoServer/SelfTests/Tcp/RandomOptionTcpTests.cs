using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class RandomOptionTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "random-option"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var template = resources.ItemResources.Items.Get(100260138);
        var core = InventoryRewardGrantService.Create(template) with { Attr = 7, SealFlag = 1, EnchantCardId = 3175, Rune = 8 };
        var changeCore = ChangeCore(resources);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("SealTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Skills = resources.Characters.Get(0).Skills, AcceptedStoryLevel = 1, DungeonState = CharacterDungeonState.NewOrdinary,
                Inventory = new([new(0, Bag(3), core), new(0, Bag(9), core), new(0, Bag(10), core),
                    new(0, Bag(11), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(10400))),
                    new(0, Bag(12), core with { EquipmentLockId = 1 }), new(0, Bag(13), changeCore),
                    new(0, Bag(14), changeCore with { EquipmentLockId = 1, SortLockFlag = 0 }),
                    new(0, Bag(15), changeCore with { IsRental = true, SortLockFlag = 0 }),
                    new(0, Bag(16), changeCore with { ItemId = 10400, SortLockFlag = 0 }),
                    new(0, Bag(17), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(100060156)))], Gold: 1000)
            });
        string initialIds = JsonSerializer.Serialize(fixture.Query("SELECT item_uid,list_type,slot_index FROM character_inventory_slots ORDER BY item_uid"));
        await fixture.StartAsync();
        byte[] townRow;
        using (var peer = Open(fixture))
        {
            peer.Send(401, [9, 0, 255, 255]);
            peer.Expect(1, 401, [0]);
            peer.Send(437, [13, 0, 0, 0, 1]); peer.Expect(1, 437, [0, 19, 0]);
            var entry = peer.Select(inventoryNotices: [(1, 713), (0, 251)]);
            var full = entry.First(packet => packet.Kind == 0 && packet.Opcode == 13 && packet.Body[0] == 0).Body;
            var original = Row(full, 9, true);
            peer.Send(401, [9, 0, 255, 255]);
            var delta = peer.Expect(0, 14).Body;
            townRow = Row(delta, 9);
            Verify(delta, townRow, original, 900);
            peer.Expect(1, 401, [1]);
            string committed = Snapshot();
            foreach (var body in new byte[][] { [9, 0, 0, 0], [11, 0, 0, 0], [12, 0, 0, 0], [100, 0, 0, 0], [9, 0, 0], [255, 255, 0, 0] })
            {
                peer.Send(401, body);
                peer.Expect(1, 401, [0]);
            }
            peer.Barrier();
            That(Snapshot() == committed, "解封重放、普通装备、安全锁、空槽及畸形包不改变资产");
            FormalDungeonTcpTests.EntryTown(peer);
            peer.Send(15, new byte[4]);
            peer.Expect(1, 15, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
            peer.Expect(0, 27, new byte[42]);
            // 普通副本不建立剧情等待状态，专门验证玩家可操作阶段的解封。
            peer.Send(16, Convert.FromHexString("030000000000000000FFFF0000000000000000000000000000"));
            peer.Expect(0, 28);
            peer.Expect(0, 29);
            FormalDungeonTcpTests.Load(peer);
            peer.Expect(0, 36);
            peer.Send(401, [3, 0, 0, 0]);
            delta = peer.Expect(0, 14).Body;
            Verify(delta, Row(delta, 3), Row(full, 3, true), 800);
            peer.Expect(1, 401, [1]);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(inventoryNotices: [(1, 713), (0, 251)]);
            var full = entry.First(packet => packet.Kind == 0 && packet.Opcode == 13 && packet.Body[0] == 0).Body;
            That(Row(full, 9, true).SequenceEqual(townRow), "重连恢复逐字节一致的解封词条与完整核心");
            string before = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_tcp_seal BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'seal failure'); END");
            peer.Send(401, [10, 0, 0, 0]);
            peer.Closed();
            That(Snapshot() == before, "解封SQL失败不发成功，不扣金币或留下随机词条");
            fixture.Sql("DROP TRIGGER fail_tcp_seal");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(inventoryNotices: [(1, 713), (0, 251)]);
            peer.Send(401, [10, 0, 0, 0]);
            var delta = peer.Expect(0, 14).Body;
            That(U32(Row(delta, 0), 6) == 700 && Row(delta, 10)[71] > 0, "回滚后重连可正常解封且只扣一次费用");
            peer.Expect(1, 401, [1]);
            peer.Barrier();
        }
        VerifyChange(fixture, resources);
        That(JsonSerializer.Serialize(fixture.Query("SELECT item_uid,list_type,slot_index FROM character_inventory_slots ORDER BY item_uid")) == initialIds,
            "城镇和副本解封均保留原实例UID与槽位");
        await fixture.StopAsync();

        string Snapshot() => JsonSerializer.Serialize(fixture.Query("SELECT revision,gold FROM characters ORDER BY id")) +
            JsonSerializer.Serialize(fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM inventory_audit_log")) +
            JsonSerializer.Serialize(fixture.Query("SELECT count(*) FROM currency_audit_log"));
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static byte[] Row(byte[] body, short slot, bool full = false)
    {
        int header = full ? 5 : 3;
        int count = U16(body, header - 2);
        That(body[0] == 0 && body.Length == header + 123 * count, "魔法封印物品列表保持完整123字节条目");
        int at = Enumerable.Range(0, count).Select(index => header + index * 123).Single(index => U16(body, index) == slot);
        return body[at..(at + 123)];
    }
    static void Verify(byte[] delta, byte[] row, byte[] original, uint gold)
    {
        That(U32(Row(delta, 0), 6) == gold && U16(delta, 1) == 2, "只发送目标装备和权威金币差量");
        int count = row[71];
        That(count is >= 1 and <= 3 && row[82] == 255 && row[81] == 0 &&
            row[83..87].All(value => value == 0) && row[72..(72 + count)].Distinct().Count() == count &&
            row[72..(72 + count)].All(value => value > 0), "完整词条数量、去重与已解封状态");
        That(row[..71].SequenceEqual(original[..71]) && row[87..].SequenceEqual(original[87..]),
            "网络核心的封装、强化、附魔、耐久和扩展字段原样保留");
    }
}
