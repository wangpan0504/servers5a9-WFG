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
        var clock = new ConsumableUseSessionTests.FakeClock();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "ticket-wire"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repository.Create(owner, new("WarTicketTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = new([
                new(0, new(InventoryListType.Main, 65), new(10310974, ItemKind.Consumable, 1) { ExpireTime = 2145852000 }),
                new(0, new(InventoryListType.Main, 66), new(10310974, ItemKind.Consumable, 1) { ExpireTime = 2145852000 })]) });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
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
