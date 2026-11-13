using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class DungeonIncountTicketTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        DungeonIncountTicketTests.Run(resources, root);
        // 当前资源已移除目标或尚缺消费合同的券；用真实动作请求验证，不把拒绝当作玩法已实现。
        (uint Action, int[] Items)[] unavailable =
        [
            (59, [10005808, 10005809, 10005915, 10005916, 10006033, 10006034, 10006113, 10006114,
                10006260, 10006261, 10148136, 10148137, 10153549, 10153550, 10153885, 10153886,
                10155575, 10155576, 490000104, 490000105, 490000401, 490000402, 490001656, 490001657,
                490001992, 490001993, 490002460, 490002461, 490003133, 490003134]),
            (155, [10157319, 10159439, 10304090, 10304301, 10304441, 490002303, 490002847,
                490003513, 490003524, 490005340, 490006380, 490006816, 490007904, 490010216]),
            (174, [10162281, 10162282, 10304873]),
            (175, [10162423, 10302669]),
            (60, [10000949, 10090157]),
            (210, [10305819]),
            (222, [10309602])
        ];
        var rejectedTickets = unavailable.SelectMany(group => group.Items.Select(id => (Item: id, group.Action)))
            .Select((ticket, index) => (ticket.Item, ticket.Action, Slot: checked((ushort)(67 + index)))).ToArray();
        That(rejectedTickets.Length == 53 && rejectedTickets.Select(ticket => ticket.Item).Distinct().Count() == 53,
            "逐款覆盖49款缺目标券与4款特殊消费合同缺口，不含四款待确认解绑券");
        var clock = new ConsumableUseSessionTests.FakeClock();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "ticket-wire"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repository.Create(owner, new("WarTicketTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = new([
                new(0, new(InventoryListType.Main, 65), new(10310974, ItemKind.Consumable, 1) { ExpireTime = 2145852000 }),
                new(0, new(InventoryListType.Main, 66), new(10310974, ItemKind.Consumable, 1) { ExpireTime = 2145852000 }),
                .. rejectedTickets.Select(ticket => new InventoryItem(0, new(InventoryListType.Main, (short)ticket.Slot),
                    new(ticket.Item, ItemKind.Consumable, 1) { ExpireTime = 2145852000 }))]) });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string unavailableBefore = fixture.Snapshot();
            foreach (var ticket in rejectedTickets)
            {
                That(resources.ItemResources.Items.StackableAction(ticket.Item)?.ClientType == ticket.Action,
                    "失败回归必须使用源券真实动作，不能用动作不匹配代替业务拒绝：" + ticket.Item);
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    peer.Send(515, Build(w =>
                    { w.Write(ticket.Slot); w.Write((byte)0); w.Write(0u); w.Write(ticket.Action); w.Write(0u); w.Write(0u); }));
                    peer.Expect(1, 515, Build(w =>
                    { w.Write((byte)0); w.Write((ushort)0); w.Write(ticket.Slot); w.Write((byte)0); w.Write((byte)ticket.Action); }));
                    peer.Barrier();
                    That(fixture.Snapshot() == unavailableBefore,
                        "未接通券及重复请求保留整库、源UID、冷却、日限次与revision：" + ticket.Item);
                }
            }
            Use(peer, 65, 1); peer.Expect(1, 515, [0, 0, 0, 65, 0, 0, 214]); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_dungeon_incount_extra") is [[0L]], "错误动作参数不扣券或追加");
            string mismatched = fixture.Snapshot();
            peer.Send(515, Build(w => { w.Write((ushort)65); w.Write((byte)0); w.Write(0u); w.Write(213u); w.Write(0u); w.Write(0u); }));
            peer.Expect(1, 515, [0, 0, 0, 65, 0, 0, 213]); peer.Barrier();
            That(fixture.Snapshot() == mismatched, "真实券不能冒用另一类追加动作，拒绝后资产不变");
            Use(peer, 65); Ack(peer, 65); peer.Expect(0, 550, Convert.FromHexString("341500000203")); peer.Barrier();
            That(fixture.Query("SELECT daily_extra,weekly_extra FROM character_dungeon_incount_extra WHERE dungeon=5428") is [[1L, 1L]] &&
                fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE list_type=0 AND slot_index=65") is [[0L]],
                "真实CMD515消费一张并仅发已提交550，不对源发第二次扣物通知");
            Use(peer, 65); peer.Expect(1, 515, [0, 0, 0, 65, 0, 0, 214]); peer.Barrier();
            fixture.Sql("CREATE TRIGGER ticket_wire_abort BEFORE INSERT ON character_dungeon_incount_extra BEGIN SELECT RAISE(ABORT,'ticket rollback'); END");
            string before = fixture.Snapshot();
            Use(peer, 66); peer.Closed();
            That(fixture.Snapshot() == before && fixture.Query("SELECT daily_extra,weekly_extra FROM character_dungeon_incount_extra") is [[1L, 1L]],
                "真实请求晚期SQL失败关闭连接且整笔回滚，不发成功");
        }
        fixture.Sql("DROP TRIGGER ticket_wire_abort");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier(); Use(peer, 66); Ack(peer, 66);
            peer.Expect(0, 550, Convert.FromHexString("341500000304")); peer.Barrier();
            That(fixture.Query("SELECT daily_extra,weekly_extra FROM character_dungeon_incount_extra") is [[2L, 2L]],
                "重连读取原存档，失败券仍可合法使用且只追加一次");
        }
        await fixture.StopAsync();
    }

    static void Use(TcpPeer peer, ushort slot, uint first = 0) => peer.Send(515, Build(w =>
    { w.Write(slot); w.Write((byte)0); w.Write(0u); w.Write(214u); w.Write(first); w.Write(0u); }));
    static void Ack(TcpPeer peer, ushort slot) => peer.Expect(1, 515, Build(w =>
    { w.Write((byte)1); w.Write(slot); w.Write((byte)0); w.Write(214u); }));
}
