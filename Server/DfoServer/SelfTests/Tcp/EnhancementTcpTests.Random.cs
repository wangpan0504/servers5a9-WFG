using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class EnhancementTcpTests
{
    static async Task RandomAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var r = resources.ItemResources;
        var core = InventoryRewardGrantService.Create(r.Items.Get(27308)) with
            { Attr = 0xEB, GenuineUpgrade = 4, EnchantCardId = 3175, EnchantUpgrade = 2, Random0 = new(1, 2, 3) };
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "enhancement-random"), resources: resources);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new CharacterRepository(fixture.Database).Create(owner, new("RandomTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 75, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Main(9), core), new(0, Main(10), core with { AmplifyType = 3, AmplifyValue = 6 }),
                    new(0, Main(65), Ticket(490704576, 1)), new(0, Main(66), Ticket(10004055, 1)),
                    new(0, Main(67), Ticket(2681908, 1)), new(0, Main(68), Ticket(10007406, 1)),
                    new(0, Main(121), Ticket(7804, 2))], Gold: 1234)
            });
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            string before = State();
            peer.Send(80, EnhancementTests.Request(0, 0, 9, 27308, 65, [])); peer.Expect(1, 80, [0, 23, 0]);
            peer.Send(80, EnhancementTests.Request(2, 0, 10, 27308, 65, [])); peer.Expect(1, 80, [0, 23, 0]); peer.Barrier();
            That(State() == before, "随机券错模式及增幅目标拒绝后连接可继续，源、核心及revision不变");
            fixture.Sql("CREATE TRIGGER fail_random_coupon_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='equipment_enhancement_ticket' AND NEW.slot_index=65 BEGIN SELECT RAISE(ABORT,'random rollback'); END");
            peer.Send(80, EnhancementTests.Request(2, 0, 9, 27308, 65, [])); peer.Closed();
            That(State() == before, "随机结果与扣券已写后审计失败整笔回滚，不提前发布结果");
            fixture.Sql("DROP TRIGGER fail_random_coupon_late");
        }
        byte last = 11;
        using (var peer = Open(fixture))
        {
            peer.Select();
            foreach (var (slot, minimum, maximum) in new[] { ((short)65, 7, 12), ((short)66, 12, 12), ((short)67, 6, 13) })
            {
                long uid = Uid();
                peer.Send(80, EnhancementTests.Request(2, 0, 9, 27308, slot, []));
                var ack = peer.Expect(1, 80).Body;
                byte result = ack[13];
                That(ack.Length == 27 && ack[0] == 1 && ack[1] == 2 && U16(ack, 2) == slot &&
                    U32(ack, 4) == 0 && ack[10] == 1 && ack[11] == last && ack[12] == 0 &&
                    result >= minimum && result <= maximum && ack[14] == 0 && U16(ack, 15) == 9 &&
                    U16(ack, 19) == ushort.MaxValue && U32(ack, 21) == 0 && ack[25] == 0 && ack[26] == 0,
                    "真实随机券模式2、路径1完整27B回执原位更新源和目标，各模板使用自身奖池");
                peer.Barrier();
                That(Core() == core with { Attr = (byte)(0xE0 | result) } && Uid() == uid,
                    "随机覆盖保持目标UID、锻造、附魔和随机属性，不使用通用重载破坏结果窗对象");
                last = result;
            }
            peer.Send(80, EnhancementTests.Request(0, 0, 9, 27308, 68, []));
            var lucky = peer.Expect(1, 80).Body;
            That(lucky.Length == 27 && lucky[1] == 0 && lucky[10] == 4 && lucky[11] == last &&
                (lucky[12] == 0 && lucky[13] == last + 1 || lucky[12] == 2 && lucky[13] == last - 1) &&
                U16(lucky, 19) == ushort.MaxValue && U32(lucky, 4) == 0,
                "幸运强化沿原生请求0按动作分流，回包强化模式0和幸运路径4，无金币或保护消费");
            last = lucky[13];
            peer.Barrier();
            string before = State();
            peer.Send(80, EnhancementTests.Request(0, 0, 9, 27308, 68, [])); peer.Expect(1, 80, [0, 23, 0]); peer.Barrier();
            That(State() == before, "幸运券耗尽重放不再改装备或扣费");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select();
            var full = entry.First(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            int at = Enumerable.Range(0, U16(full, 3)).Select(index => 5 + index * 123).Single(index => U16(full, index) == 9);
            That(full[at + 10] == (0xE0 | last) && Core() == core with { Attr = (byte)(0xE0 | last) } &&
                fixture.Query("SELECT gold FROM characters") is [[1234L]] &&
                fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=7804") is [[2L]],
                "随机及幸运结果重连恢复完整核心，金币和强化保护券保持不变");
            peer.Barrier();
        }
        ItemCore Ticket(int id, int count) => InventoryRewardGrantService.Create(r.Items.Get(id), count) with { ExpireTime = 0 };
        ItemCore Core() => JsonSerializer.Deserialize<ItemCore>((string)fixture.Query("SELECT i.item_core FROM character_inventory_items i " +
            "JOIN character_inventory_slots s USING(item_uid) WHERE s.list_type=0 AND s.slot_index=9").Single().Single())!;
        long Uid() => (long)fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE list_type=0 AND slot_index=9").Single().Single();
        string State() => fixture.Snapshot("SELECT revision,gold FROM characters") + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
        static InventorySlot Main(short slot) => new(InventoryListType.Main, slot);
    }
}
