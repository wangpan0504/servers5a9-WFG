using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EquipmentTransmissionTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        const int targetId = 100180613, donorId = 100180578, materialId = 10307438;
        var items = resources.ItemResources;
        That(items.EquipmentTransmissions.Targets.Contains(targetId) &&
            !items.EquipmentTransmissions.Targets.Contains(donorId) &&
            items.EquipmentTransmissions.DonorRarities.SetEquals([4, 5]) &&
            items.EquipmentTransmissions.Materials.SequenceEqual(new DfoServer.GameWorld.Items.ItemMaterialCost[] { new(materialId, 400) }),
            "真实传承表确认接收方向、供体稀有度与400黑暗之眼灵能碎片，无额外刻印金币费");
        That(items.Items.Get(donorId).AttachType.Trim().Trim('`') == "[trade]" &&
            items.Items.Get(donorId).EquipmentSlot == items.Items.Get(targetId).EquipmentSlot,
            "现场供体模板不可交易且与接收装备同部位");
        var targetCore = InventoryRewardGrantService.Create(items.Items.Get(targetId)) with
        {
            Attr = 15, AmplifyType = 3, AmplifyValue = 78, GenuineUpgrade = 8,
            EnchantCardId = 490705382, EnchantUpgrade = 3, EmblemSocketCount = 2,
            Emblem1 = 2510044, Emblem2 = 2510044, Extension114 = new(2, 23456), Extension121 = 3456
        };
        var donorCore = InventoryRewardGrantService.Create(items.Items.Get(donorId)) with
        {
            Attr = 11, AmplifyType = 1, AmplifyValue = 33, GenuineUpgrade = 5,
            EnchantCardId = 490705382, EnchantUpgrade = 2, EmblemSocketCount = 1, Emblem1 = 2510044
        };
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "equipment-transmission"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("TransmitTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, Skills = resources.Characters.Get(0).Skills,
                AcceptedStoryLevel = 1, DungeonState = CharacterDungeonState.NewOrdinary,
                Inventory = new([
                    new(0, Bag(50), targetCore), new(0, Bag(12), donorCore),
                    new(0, Bag(121), InventoryRewardGrantService.Create(items.Items.Get(materialId), 800))
                ], 24, 1234567)
            });
        byte[] request = Convert.FromHexString("00A5765000B81D430CF81A006F320085A2F8050C0062A2F805");
        string baseline = Snapshot();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            // 未选角时1347屏障不提供响应；1688拒绝回执已在业务提交路径结束后发送。
            peer.Send(1688, request); peer.Expect(1, 1688, [0, 1, 0]);
            That(Snapshot() == baseline, "未选角不能凭请求前缀定位角色或消费装备");
            peer.Select();
            baseline = Snapshot();
            peer.Send(1688, request[..^1]); peer.Expect(1, 1688, [0, 1, 0]); peer.Barrier();
            That(Snapshot() == baseline, "截断请求失败且连接保持，资产和审计不变");
            fixture.Sql("CREATE TRIGGER fail_transmission AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'transmission rollback'); END");
            peer.Send(1688, request); peer.Closed();
            That(Snapshot() == baseline, "事务末期SQL失败完整回滚供体、附魔徽章、材料和revision");
            fixture.Sql("DROP TRIGGER fail_transmission");
        }
        long targetUid;
        using (var peer = Open(fixture))
        {
            peer.Select();
            targetUid = Reload().Inventory.Find(Bag(50))!.Uid;
            peer.Send(1688, request);
            byte[] delta = peer.Expect(0, 14).Body;
            That(delta[0] == 0 && U16(delta, 1) == 3, "成功先发送接收装备、供体清除与材料扣减的权威差量");
            peer.Expect(1, 1688, Convert.FromHexString("0100000000320085A2F805")); peer.Barrier();
            AssertCommitted(Reload(), targetUid);
            baseline = Snapshot();
            peer.Send(1688, request); peer.Expect(1, 1688, [0, 1, 0]); peer.Barrier();
            That(Snapshot() == baseline, "重复旧请求不能再次消费400材料或覆盖其他实例");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            AssertCommitted(Reload(), targetUid);
        }
        await fixture.StopAsync();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            AssertCommitted(new CharacterRepository(fixture.Database).List(owner).Single(), targetUid);

        void AssertCommitted(Character value, long uid)
        {
            var target = value.Inventory.Find(Bag(50));
            var expected = targetCore with
            {
                EnchantCardId = donorCore.EnchantCardId, EnchantUpgrade = donorCore.EnchantUpgrade,
                EmblemSocketCount = donorCore.EmblemSocketCount, Emblem1 = donorCore.Emblem1, Emblem2 = donorCore.Emblem2
            };
            That(target?.Uid == uid && target.Core == expected && value.Inventory.Find(Bag(12)) is null &&
                value.Inventory.Find(Bag(121))?.Core.Count == 400 && value.Inventory.Gold == 1234567,
                "提交、重连和新仓储读取均保留目标完整核心及UID，仅扣供体与规定材料");
        }
        Character Reload()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.List(owner).Single();
        }
        string Snapshot() => fixture.Snapshot() +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT count(*) FROM inventory_audit_log") + fixture.Snapshot("SELECT count(*) FROM currency_audit_log");
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
}
