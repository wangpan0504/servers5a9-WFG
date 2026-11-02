using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 正式活动etc1动作券沿原生确认后的515合同使用；旧材料券865由aura-skin组覆盖。
internal static class AuraActionTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        const int ticket = 490013030;
        var template = resources.ItemResources.Items.Get(ticket);
        That(template is { Kind: ItemKind.Consumable, StackableType: "[etc] 1", OpensAuraSkinSlot: true } &&
            resources.ItemResources.Items.Get(490700411) is
                { Kind: ItemKind.Material, StackableType: "[material] 0", OpensAuraSkinSlot: false },
            "127正向组使用正式活动etc1动作券，旧材料券保持无动作声明");
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "aura-action"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        short slot;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var role = repository.Create(owner, new("AuraAction", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 60, AcceptedStoryLevel = 1, EquipmentSlotFlags = 17,
                Skills = resources.Characters.Get(0).Skills });
            var inventory = new InventoryService(resources.ItemResources.Items, resources.ItemResources.GoldLimits,
                resources.ItemResources.EpicPieces);
            role = InventoryService.Commit(repository, owner, role, inventory.Grant(role, [new(ticket, 1)]));
            slot = role.Inventory.Items.Single(item => item.Core.ItemId == ticket).Position.Index;
            That(slot is >= 65 and <= 120, "真实活动券通过正式Grant进入消耗品页，不改类型或移入材料页");
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            string before = State();
            foreach (byte[] body in new[] { Action(slot, list: 1), Action(slot, reserved: 1), Action(slot, first: 1),
                Action(slot, second: 1), Action(slot, action: 224), Action((short)(slot + 1)) })
            {
                peer.Send(515, body);
                That(peer.Expect(1, 515).Body[0] == 0, "错源、错动作或非零参数必须拒绝");
            }
            peer.Barrier();
            That(State() == before, "非法515组合不扣券也不开栏");
            fixture.Sql("CREATE TRIGGER fail_aura_action BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='open_aura_skin_slot' BEGIN SELECT RAISE(ABORT,'aura rollback'); END");
            peer.Send(515, Action(slot));
            peer.Expect(1, 515, [0, 0, 0, (byte)slot, (byte)(slot >> 8), 0, 127]);
            peer.Barrier();
            That(State() == before, "515后段SQL失败回失败ACK且保持连接，资产及资格同事务回滚");
            fixture.Sql("DROP TRIGGER fail_aura_action");
            peer.Send(515, Action(slot));
            peer.Expect(1, 515, [1, (byte)slot, (byte)(slot >> 8), 0, 127, 0, 0, 0]);
            byte[] player = peer.Expect(0, 2).Body;
            That(player[0] == 1 && player[36 + U32(player, 32)] == 25,
                "515成功由客户端消费源一张，随后Player同步已提交bit3与其它flags");
            peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=$p0", ticket) is [[0L]],
                "服务端真实扣除活动券且没有额外14让客户端重复消费");
            before = State();
            peer.Send(515, Action(slot)); That(peer.Expect(1, 515).Body[0] == 0, "重复动作拒绝");
            peer.Send(865, [255, 255, 11, 0]); peer.Expect(1, 865, [0, 0, 0, 11, 0]);
            peer.Barrier();
            That(State() == before, "重复515及865都不二次扣券或误导购买");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            var selected = peer.Select();
            byte[] player = selected.Single(packet => packet.Kind == 0 && packet.Opcode == 2 && packet.Body[0] == 1).Body;
            That(player[36 + U32(player, 32)] == 25, "重启恢复515开通的光环资格");
            peer.Barrier();
        }
        That(fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE action_name='open_aura_skin_slot'") is [[1L]] &&
            fixture.Query("PRAGMA integrity_check") is [["ok"]], "127与865共用一笔库存审计及合法数据库");

        string State() => fixture.Snapshot("SELECT id,revision,equipment_slot_flags FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
    }

    static byte[] Action(short slot, byte list = 0, uint reserved = 0, uint action = 127, uint first = 0, uint second = 0) =>
        Build(writer =>
        {
            writer.Write((ushort)slot); writer.Write(list); writer.Write(reserved); writer.Write(action);
            writer.Write(first); writer.Write(second);
        });
}
