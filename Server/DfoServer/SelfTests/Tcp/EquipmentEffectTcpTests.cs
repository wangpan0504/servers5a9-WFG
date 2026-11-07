using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EquipmentEffectTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        const int attachId = 2682369, removeId = 2682371, weaponId = 101030828;
        var items = resources.ItemResources.Items;
        That(items.EquipmentEffect(attachId) is { Effect: 1 } && items.EquipmentEffect(removeId) is { Effect: 0 } &&
            items.Get(weaponId) is { EquipmentSlot: 12, Rarity: >= 2 }, "累计候选真实桃花/解除符文及武器资格");
        var weapon = InventoryRewardGrantService.Create(items.Get(weaponId)) with
        {
            SealFlag = 0, Attr = 15, AmplifyType = 2, AmplifyValue = 123, GenuineUpgrade = 8,
            EnchantCardId = 490705382, EnchantUpgrade = 2, Extension114 = new(1, 12345), Extension121 = 2345
        };
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "equipment-effect"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("EffectTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Bag(9), weapon),
                    new(0, Bag(108), InventoryRewardGrantService.Create(items.Get(attachId), 2)),
                    new(0, Bag(109), InventoryRewardGrantService.Create(items.Get(removeId), 1))], 24, 1234567)
            });
        var request = EquipmentEffectTests.Request(108, 9);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            string before = Snapshot();
            peer.Send(953, request); peer.Expect(1, 953, [0, 19, 0]);
            That(Snapshot() == before, "未选角953不能修改任何角色或资产");
            peer.Select();
            before = Snapshot();
            peer.Send(953, request[..^1]); peer.Expect(1, 953, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == before, "畸形请求完整拒绝且连接继续");
            fixture.Sql("CREATE TRIGGER fail_equipment_effect BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='equipment_effect' BEGIN SELECT RAISE(ABORT,'effect rollback'); END");
            peer.Send(953, request); peer.Closed();
            That(Snapshot() == before, "事务末段SQL失败同时回滚符文、目标、revision和审计，不提前发成功");
            fixture.Sql("DROP TRIGGER fail_equipment_effect");
        }
        long uid = Reload().Inventory.Find(Bag(9))!.Uid;
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Send(953, request);
            Delta(peer, 1);
            peer.Expect(1, 953, Ack(1)); peer.Barrier();
            var current = Reload();
            That(current.Inventory.Find(Bag(9)) == new InventoryItem(uid, Bag(9), weapon with { Rune = 1 }) &&
                current.Inventory.Find(Bag(108))?.Core.Count == 1 && current.Inventory.Gold == 1234567,
                "真实953只扣一张并持久化特效，保留UID、打造和金币");
            string after = Snapshot();
            peer.Send(953, request); peer.Expect(1, 953, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == after, "重放同特效请求不重复扣符文或写审计");
            peer.Send(953, EquipmentEffectTests.Request(109, 9));
            Delta(peer, 0);
            peer.Expect(1, 953, Ack(0)); peer.Barrier();
            That(Reload().Inventory.Find(Bag(9))?.Core == weapon && Reload().Inventory.Find(Bag(109)) is null,
                "解除符文实际清零且扣尽清槽，不丢武器属性");
            after = Snapshot();
            peer.Send(953, EquipmentEffectTests.Request(109, 9)); peer.Expect(1, 953, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == after, "已耗尽的解除符文不能重复使用");
            peer.Send(953, request); Delta(peer, 1); peer.Expect(1, 953, Ack(1)); peer.Barrier();
            That(Reload().Inventory.Find(Bag(108)) is null, "最后一张附加符文准确清槽");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("restart");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            var current = Reload();
            That(current.Inventory.Find(Bag(9)) == new InventoryItem(uid, Bag(9), weapon with { Rune = 1 }) &&
                current.Inventory.Find(Bag(108)) is null && current.Inventory.Find(Bag(109)) is null,
                "重连与独立进程重启保留最终特效，符文不回滚复活");
        }

        byte[] Ack(int effect) => Build(writer =>
        { writer.Write((byte)1); writer.Write(0); writer.Write(weaponId); writer.Write(effect); writer.Write(9); });
        void Delta(TcpPeer peer, ushort effect)
        {
            byte[] delta = peer.Expect(0, 14).Body;
            That(delta[0] == 0 && U16(delta, 1) == 2, "结果窗前下发源与武器两行完整差量");
            int row = Enumerable.Range(0, 2).Single(index => U16(delta, 3 + 123 * index) == 9);
            That(U16(delta, 3 + 123 * row + 69) == effect, "123B条目offset69发实际特效编号");
        }
        Character Reload()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.List(owner).Single();
        }
        string Snapshot() => fixture.Snapshot() +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id");
    }

    static InventorySlot Bag(short index) => new(InventoryListType.Main, index);
}
