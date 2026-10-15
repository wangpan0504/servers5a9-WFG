using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AvatarDyeTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        var avatar = InventoryRewardGrantService.CreateItem(items.Get(406560053), new(InventoryListType.Avatar, 0),
            InventoryRewardGrantService.Create(items.Get(406560053)) with { Durability = 4, Attr = 3 });
        avatar = avatar with { Avatar = avatar.Avatar! with { ExpireTime = int.MaxValue,
            Socket0 = AvatarSocket.Empty(1), Socket1 = AvatarSocket.Empty(2) } };
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "avatar-dye"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("DyeTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 3, Level = 16, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(3).Skills,
                Inventory = new([avatar,
                    new(0, new(InventoryListType.Main, 74), InventoryRewardGrantService.Create(items.Get(490704677), 2)),
                    new(0, new(InventoryListType.Main, 75), InventoryRewardGrantService.Create(items.Get(2660674)))], Gold: 4321)
            });
        string identities = TargetIdentity();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            string original = Snapshot();
            peer.Send(498, [74, 0, 0, 0]); peer.Expect(1, 498, [0, 0, 0]);
            That(original == Snapshot(), "未选角漂染拒绝，不消费或改变角色状态");
            peer.Select(); peer.Barrier();
            string before = Snapshot();
            foreach (byte[] invalid in new byte[][] { [74, 0, 0], [74, 0, 0, 0, 0], [255, 255, 0, 0], [74, 0, 210, 0], [75, 0, 0, 0] })
            {
                peer.Send(498, invalid); peer.Expect(1, 498, [0, 0, 0]); peer.Barrier();
                That(before == Snapshot(), "畸形包、错误槽和原色洗色完整拒绝，连接可继续");
            }
            fixture.Sql("CREATE TRIGGER fail_dye_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='avatar_dye' BEGIN SELECT RAISE(ABORT,'dye rollback'); END");
            peer.Send(498, [74, 0, 0, 0]); peer.Closed();
            That(before == Snapshot(), "漂染晚期审计失败回滚源数量、完整时装详情、UID、revision及审计，不提前发成功");
            fixture.Sql("DROP TRIGGER fail_dye_late");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            peer.Send(498, [74, 0, 0, 0]);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, 74, 490704677, 1);
            peer.Expect(1, 498, [1, 0, 0, 4, 0, 0, 0, 1, 0, 0, 0]);
            peer.Barrier();
            That(TargetIdentity() == identities && Colors() is [[1L, 0L]],
                "先同步源数量，再以ACK498更新原对象RAW4，未发送重建目标的NOTI14，身份、属性、孔位和期限不变");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            var full = peer.SpecialContainers.Single(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 1).Body;
            That(full.Length == 170 && U16(full, 166) == 1 && U16(full, 168) == 0 && U16(full, 16) == 4,
                "重连完整时装列表恢复珊瑚色和原属性，不依赖结果窗口临时对象");
            peer.Send(498, [75, 0, 0, 0]);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, 75, -1, 0);
            peer.Expect(1, 498, [1, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0]); peer.Barrier();
            That(Colors() is [[0L, 0L]] && TargetIdentity() == identities, "真实洗色剂只耗一瓶恢复原色，保留其余完整实例");
            peer.Send(498, [74, 0, 0, 0]);
            BoosterTcpTests.Entry(peer.Expect(0, 14).Body, 74, -1, 0);
            peer.Expect(1, 498, [1, 0, 0, 4, 0, 0, 0, 1, 0, 0, 0]); peer.Barrier();
            string before = Snapshot();
            peer.Send(498, [74, 0, 0, 0]); peer.Expect(1, 498, [0, 0, 0]); peer.Barrier();
            That(before == Snapshot(), "末瓶后源槽清空，重放请求不继续染色或增加revision");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "漂染无新增UID、孤立详情或外键损坏");

        object[][] Colors() => fixture.Query("SELECT json_extract(detail,'$.Color1'),json_extract(detail,'$.Color2') FROM inventory_avatar_details");
        string TargetIdentity() => fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=406560053") +
            fixture.Snapshot("SELECT json_remove(detail,'$.Color1','$.Color2') FROM inventory_avatar_details");
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }
}
