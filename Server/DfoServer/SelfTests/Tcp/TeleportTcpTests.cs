using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class TeleportTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new Clock();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "teleport"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("Teleport", resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null);
            var items = resources.ItemResources.Items;
            repository.Create(owner, candidate with { Level = 60, CompletedQuests = [3156], Inventory = candidate.Inventory with
            {
                Items = candidate.Inventory.Items.Add(InventoryRewardGrantService.CreateItem(items.Get(2600014), new(InventoryListType.Main, 65),
                    InventoryRewardGrantService.Create(items.Get(2600014), 3)))
                    .Add(InventoryRewardGrantService.CreateItem(items.Get(101590011), new(InventoryListType.Equipment, 9),
                        InventoryRewardGrantService.Create(items.Get(101590011))))
            } });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Send(237, Potion(65, 2600014, 38, 0, 1327, 234)); peer.Expect(1, 237, [0, 23, 0]);
            peer.Send(692, Name("Missing")); peer.Expect(1, 692, [1]);
            peer.Select(prepared: true);
            peer.Send(469, Name("Missing")); peer.Expect(1, 469, [0, 1, 0]); peer.Barrier();
            peer.Send(237, Potion(65, 2600014, 38, 0, 1327, 234));
            peer.Expect(1, 237, [1, 65, 0, 78, 172, 39, 0]);
            peer.Expect(0, 1681); peer.Expect(0, 1681); peer.Expect(0, 23); peer.Expect(0, 24); peer.Barrier();
            That(Count() == 2 && fixture.Query("SELECT town,area,x,y FROM characters") is [[38L, 0L, 1327L, 234L]],
                "真实TCP药剂ACK、绝对库存及23/24后保存目的地，无额外124");
            peer.Send(237, Potion(65, 2600014, 38, 0, 1327, 234)); peer.Expect(1, 237, [0, 23, 0]); peer.Barrier();
            That(Count() == 2, "原药请求重放仅拒绝，不重复扣件");
            peer.Send(470, Solo(-1, 7, 39, 3, 783, 205)); peer.Expect(1, 470, [1]);
            peer.Expect(0, 378, [1, 2, 164, 1, 0, 0]); peer.Expect(0, 23); peer.Expect(0, 24); peer.Barrier();
            That(fixture.Query("SELECT town,area,x,y FROM characters") is [[39L, 3L, 783L, 205L]], "真实NPC身份与任务准入后光环跨图落库");
            peer.Send(470, Solo(-1, 7, 39, 3, 783, 205)); peer.Expect(1, 470, [0, 1, 0, 0, 0, 0, 0]); peer.Barrier();
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(prepared: true, inventoryNotices: [(0, 378)]); peer.Barrier();
            peer.Send(470, Solo(-1, 7, 39, 3, 783, 205)); peer.Expect(1, 470, [0, 1, 0, 0, 0, 0, 0]); peer.Barrier();
            That(Count() == 2 && fixture.Query("SELECT count(*) FROM character_teleport_cooldowns") is [[1L]],
                "重选恢复冷却；失败ACK之后后续请求可操作且没有额外库存变化");
            clock.Offset = TimeSpan.FromSeconds(420);
            using var target = Open(fixture, "beta"); target.Create("Target"); target.Select();
            target.Send(1426); target.Expect(0, 23); target.Expect(0, 24); target.Barrier();
            peer.Send(469, Name("Target"));
            var precheck = peer.Expect(1, 469).Body;
            That(precheck[0] == 1 && U32(precheck, 2) == target.Id && U32(precheck, 6) == 6 &&
                precheck.AsSpan(10, 6).SequenceEqual(System.Text.Encoding.ASCII.GetBytes("Target")) &&
                precheck.AsSpan(16, 3).SequenceEqual(new byte[] { 1, 0, 0 }),
                "玩家预检查只查询当前公开城镇成员，不创建离线目标");
            peer.Send(470, Solo(target.Id, -1, 255, 255, 0, 0)); peer.Expect(1, 470, [1]); peer.Expect(0, 378, [1, 2, 164, 1, 0, 0]);
            peer.Expect(0, 23); peer.Expect(0, 24); peer.Expect(0, 2); peer.Expect(0, 3); peer.Expect(0, 22); peer.Barrier();
            target.Expect(0, 2); target.Expect(0, 3); target.Expect(0, 22); target.Barrier();
            That(fixture.Query("SELECT town,area,x,y FROM characters WHERE id=$p0", peer.Id) is [[38L, 0L, 622L, 196L]],
                "玩家传送取实际目标位置，伪造客户端目的地不进入保存；同屏双方收到真实角色");
        }
        int Count() => System.Text.Json.JsonSerializer.Deserialize<ItemCore>((string)fixture.Query(
            "SELECT item_core FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=2600014")[0][0])!.Count;
    }
    static byte[] Potion(short slot, int item, byte town, byte area, ushort x, ushort y) => Build(w =>
    { w.Write(slot); w.Write(item); w.Write((byte)0); Position(w, town, area, x, y); });
    static byte[] Solo(int character, int npc, byte town, byte area, ushort x, ushort y) => Build(w =>
    { w.Write(character); w.Write(npc); Position(w, town, area, x, y); });
    static void Position(BinaryWriter w, byte town, byte area, ushort x, ushort y)
    { w.Write(town); w.Write(area); w.Write(x); w.Write(y); w.Write((byte)5); }
    sealed class Clock : TimeProvider
    {
        public TimeSpan Offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }
}
