using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AvatarSocketTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        InventoryItem Avatar(int id, short slot) => InventoryRewardGrantService.CreateItem(items.Get(id),
            new(InventoryListType.Avatar, slot), InventoryRewardGrantService.Create(items.Get(id)) with { Attr = 3, Durability = 3 })
            with { Avatar = new() { Color1 = 1, Color2 = 2, ExpireTime = int.MaxValue } };
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "avatar-socket"), resources: resources);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            new CharacterRepository(fixture.Database).Create(owner,
                new("SocketTcp", resources.Towns.Room, resources.Towns.InitialReturn)
                {
                    Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                    Inventory = new([Avatar(101550535, 0), Avatar(415580044, 1),
                        new(0, new(InventoryListType.Main, 74), InventoryRewardGrantService.Create(items.Get(7421), 2)),
                        new(0, new(InventoryListType.Main, 75), InventoryRewardGrantService.Create(items.Get(10301316)))], Gold: 4321)
                });
        byte[] rare = Request(0, 101550535, 74), skin = Request(1, 415580044, 75);
        string identity = Identity();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            string original = Snapshot();
            peer.Send(206, rare); peer.Expect(1, 206, [0, 4, 0]);
            That(original == Snapshot(), "未选角开孔拒绝，不改库存");
            peer.Select(); peer.Barrier();
            string before = Snapshot();
            foreach (byte[] bad in new[] { rare[..^1], rare.Concat(new byte[] { 0 }).ToArray(),
                Request(0, 1, 74), Request(1, 415580044, 74), Request(0, 101550535, 76) })
            {
                peer.Send(206, bad); peer.Expect(1, 206, [0, 4, 0]); peer.Barrier();
                That(before == Snapshot(), "畸形、模板不符、grade不符和空材料拒绝，后续连接可继续");
            }
            fixture.Sql("CREATE TRIGGER fail_avatar_socket_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='avatar_socket_open' BEGIN SELECT RAISE(ABORT,'socket rollback'); END");
            peer.Send(206, rare); peer.Closed();
            That(before == Snapshot(), "晚期SQL失败回滚材料、全部时装详情、UID、revision和审计，不提前发成功");
            fixture.Sql("DROP TRIGGER fail_avatar_socket_late");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            peer.Send(206, rare); peer.Expect(1, 206, [1, 0, 0, 74, 0]);
            CheckRow(peer.Expect(0, 14).Body, 3, 0, 101550535, 1);
            peer.Barrier();
            That(fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items " +
                "WHERE json_extract(item_core,'$.ItemId')=7421") is [[1L]] && Identity() == identity,
                "稀有时装成功ACK先扣源一次，再发布完整目标，源不另发14；UID、核心及非孔详情保留");
            string opened = Snapshot();
            peer.Send(206, rare); peer.Expect(1, 206, [0, 4, 0]); peer.Barrier();
            That(opened == Snapshot(), "已开孔重发拒绝，不再次消耗源");
            peer.Send(206, skin); peer.Expect(1, 206, [1, 1, 0, 75, 0]);
            CheckRow(peer.Expect(0, 14).Body, 3, 1, 415580044, 0xFFEF);
            peer.Barrier();
            That(fixture.Query("SELECT COUNT(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10301316") is [[0L]],
                "高级皮肤开双多色孔，最后一个开孔器耗尽");
            opened = Snapshot();
            peer.Send(206, skin); peer.Expect(1, 206, [0, 4, 0]); peer.Barrier();
            That(opened == Snapshot(), "空材料槽重复请求拒绝，连接仍可继续处理");
        }
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            var full = peer.SpecialContainers.Single(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 1).Body;
            That(full.Length == 335, "服务重建后完整时装列表保留两个165字节实例");
            CheckRow(full, 5, 0, 101550535, 1);
            CheckRow(full, 170, 1, 415580044, 0xFFEF);
            That(Identity() == identity && fixture.Query("PRAGMA integrity_check") is [["ok"]], "重建和重连恢复全部孔位，完整身份与数据库一致");
        }
        await fixture.StopAsync();

        string Identity() => fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE json_extract(item_core,'$.Kind')=8 ORDER BY item_uid") +
            fixture.Snapshot("SELECT item_uid,json_remove(detail,'$.Socket0','$.Socket1','$.Socket2','$.Socket3','$.Socket4') FROM inventory_avatar_details ORDER BY item_uid");
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }

    static byte[] Request(short target, int id, short source) => Build(writer =>
    {
        writer.Write(target); writer.Write(id); writer.Write(source);
    });

    static void CheckRow(byte[] body, int at, ushort slot, int id, ushort socket)
    {
        That(body[0] == 1 && U16(body, at) == slot && U32(body, at + 2) == id && body[at + 10] == 3 &&
            U16(body, at + 11) == 3 && U32(body, at + 123) == 30 &&
            U16(body, at + 127) == socket && U32(body, at + 129) == uint.MaxValue &&
            U16(body, at + 133) == socket && U32(body, at + 135) == uint.MaxValue &&
            body.AsSpan(at + 139, 18).SequenceEqual(new byte[18]) && U32(body, at + 157) == 4 &&
            U16(body, at + 161) == 1 && U16(body, at + 163) == 2,
            "独立解码完整165B目标的槽位、模板、核心、双孔、三空孔及染色");
    }
}
