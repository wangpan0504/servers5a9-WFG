using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class AdventureNameTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "adventure-names"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("NameHero", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            That(resources.ItemResources.Items.AdventureNameTicket(10302272) is not null, "真实PVF改名券动作已闭合");
            repository.Create(owner, candidate with { Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.Add(
                new(0, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(10302272)))) } });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Send(645); peer.Expect(1, 645, [1, 0]); peer.Expect(0, 1375, new byte[5]); peer.Expect(0, 1343);
            SelectionContentsTcpTests.ExpectTowers(peer, fixture, resources, fixture.Options.DevelopmentAccount);
            peer.Send(1444, Name("测试冒险团")); peer.Expect(1, 1444, new byte[] { 1 }.Concat(Name("测试冒险团")).ToArray());
            peer.Send(1438, Name("测试冒险团"));
            peer.Expect(1, 1438, new byte[] { 1 }.Concat(Name("测试冒险团")).ToArray());
            peer.Expect(0, 1375, Name("测试冒险团").Concat(new byte[] { 0 }).ToArray()); peer.Expect(0, 1343);
            SelectionBarrier(peer);
            peer.Send(1438, Name("Bypass")); peer.Expect(1, 1438, [0, 3, 0]); SelectionBarrier(peer);
            using (var other = Open(fixture, "nameother"))
            {
                other.Send(1438, Name("TakenGroup"));
                other.Expect(1, 1438, new byte[] { 1 }.Concat(Name("TakenGroup")).ToArray());
                other.Expect(0, 1375); other.Expect(0, 1343); SelectionBarrier(other);
            }
            peer.Send(1444, Name("takengroup")); peer.Expect(1, 1444, [0, 20, 0]); SelectionBarrier(peer);
            peer.Select();
            Bytes(peer.AdventureName, Name("测试冒险团").Concat(new byte[] { 0 }).ToArray(), "入城回放已提交账号名称");
            That(peer.ActorAdventureName == "测试冒险团", "头顶独立昵称字段不能因1375已含名称而留空");
            var unchanged = fixture.Query("SELECT name,revision,name_initialized FROM account_adventures WHERE account=$p0", fixture.Options.DevelopmentAccount);
            peer.Send(1445, Rename("TakenGroup", 65, 0)); peer.Expect(1, 1445, [0, 20, 0]); peer.Barrier();
            peer.Send(1445, Rename("FreeBypass", 255, 1)); peer.Expect(1, 1445, [0, 19, 0]); peer.Barrier();
            That(fixture.Query("SELECT name,revision,name_initialized FROM account_adventures WHERE account=$p0", fixture.Options.DevelopmentAccount)[0]
                .SequenceEqual(unchanged[0]) && fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE slot_index=65 AND list_type=0") is [[1L]],
                "重名和未授予免费模式不改名称或消费券");
            peer.Send(1445, Rename("更名后的团", 65, 0)); peer.Expect(1, 1445, [1]);
            var consumed = peer.Expect(0, 1681).Body;
            That(consumed[0] == 0 && U16(consumed, 1) == 1 && U16(consumed, 3) == 65, "无获得提示的源槽消费差量在返回选角前发布");
            peer.Expect(0, 1375, Name("更名后的团").Concat(new byte[] { 0 }).ToArray());
            peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Send(1445, Rename("Replay", 65, 0)); peer.Expect(1, 1445, [0, 19, 0]); SelectionBarrier(peer);
            That(fixture.Query("SELECT count(*) FROM account_adventure_receipts WHERE action='name_change'") is [[1L]] &&
                fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE slot_index=65 AND list_type=0") is [[0L]],
                "改名券只消费一次，返回选角后的旧请求不能重跑");
            peer.Select();
            Bytes(peer.AdventureName, Name("更名后的团").Concat(new byte[] { 0 }).ToArray(), "同一连接重新选角安装新名称，不恢复旧账号缓存");
            That(peer.ActorAdventureName == "更名后的团", "改名后重新建立本人Actor使用新昵称");
            using (var world = Open(fixture, "worldnames"))
            {
                world.Create("NameTown"); world.Select();
                That(world.ActorAdventureName == "", "未命名账号入城不把持久回退名显示到头顶");
                var characterRevision = fixture.Query("SELECT revision FROM characters WHERE account='worldnames'")[0][0];
                world.Send(1438, Name("WorldGroup"));
                world.Expect(1, 1438, new byte[] { 1 }.Concat(Name("WorldGroup")).ToArray());
                world.Expect(0, 1375, Name("WorldGroup").Concat(new byte[] { 0 }).ToArray());
                world.Expect(0, 1343); world.Expect(0, 1349);
                That(DecodeActorAdventureName(world.Expect(0, 2).Body) == "WorldGroup",
                    "城镇首次命名刷新现有Actor，不清场或依赖重登");
                world.Barrier();
                That(fixture.Query("SELECT revision FROM characters WHERE account='worldnames'")[0][0].Equals(characterRevision),
                    "昵称投影不改变角色revision或资产状态");
            }
        }
        await fixture.StopAsync();
        await fixture.StartAsync("name-restarted", isolatedProcess: true);
        using (var peer = Open(fixture))
        {
            peer.Select();
            Bytes(peer.AdventureName, Name("更名后的团").Concat(new byte[] { 0 }).ToArray(), "真实进程重启回放名字与已消耗首次资格");
            That(peer.ActorAdventureName == "更名后的团", "重启后的Actor保留已提交昵称");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "名称、券和审计的数据库完整");
        Console.WriteLine($"PASS {Count - checks} 冒险团命名、重名检查、改名券、返回选角与重启TCP断言");
    }

    static byte[] Rename(string name, ushort slot, ushort mode) => Name(name).Concat(Build(writer =>
    {
        writer.Write(slot); writer.Write(mode);
    })).ToArray();

    static void SelectionBarrier(TcpPeer peer)
    {
        peer.Send(692, Name("NameProbe"));
        peer.Expect(1, 692, [1]);
    }
}
