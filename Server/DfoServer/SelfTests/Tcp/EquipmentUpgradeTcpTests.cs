using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EquipmentUpgradeTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        const int weaponId = 101030828;
        var items = resources.ItemResources;
        That(items.DarkRevelations.Weapon(weaponId) is { Slots: 2, Upgraded: true }, "真实界武器声明两个黑暗诉求权能槽");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "equipment-upgrade"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        ItemCore Core(int id) => InventoryRewardGrantService.Create(items.Items.Get(id));
        var inventoryItems = new List<InventoryItem>
        {
            new(0, Bag(26), Core(weaponId) with { Attr = 15, Extension114 = new(0, 0) }),
            new(0, Bag(27), Core(100070603) with { Attr = 11 }),
            new(0, Bag(28), Core(100080651) with { Attr = 15, Value = 123456 }),
            new(0, Bag(29), Core(101030827) with { Attr = 11 }),
            new(0, Bag(30), Core(100070604) with { Attr = 11 }),
            new(0, Bag(31), Core(100301034) with { Attr = 15, Extension114 = new(2, 0x1234) }),
            new(0, Bag(32), Core(100300946) with { Attr = 11 }),
            new(0, Bag(33), Core(101030803) with { Attr = 11 }),
            new(0, Bag(34), Core(100300947) with { Attr = 11 }),
            new(0, Bag(35), Core(100301036) with { Attr = 15 }),
            new(0, Bag(121), Core(10158964) with { Value = 75 }),
            new(0, Bag(122), Core(10308559) with { Value = 500 }),
            new(0, Bag(123), Core(10309021) with { Value = 50 }),
            new(0, Bag(124), Core(10306750) with { Value = 96 })
        };
        foreach (var condition in items.DarkRevelations.Conditions.Values.SelectMany(value => value))
            inventoryItems.Add(new(0, new(InventoryListType.Equipment,
                DfoServer.GameWorld.Items.ItemCatalog.Slot(condition.EquipmentType.Trim('[', ']'))), Core(condition.Items[0])));
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var created = repository.Create(owner, new("UpgradeTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, Skills = resources.Characters.Get(0).Skills,
                AcceptedStoryLevel = 1, DungeonState = CharacterDungeonState.NewOrdinary,
                Inventory = new(inventoryItems.ToImmutableArray(), Gold: 16600000)
            });
            repository.CommitInventory(owner, created, new(created.Inventory,
                created.Inventory with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.LegendarySoul, 25) }, "upgrade_fixture"));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            string baseline = Snapshot();
            peer.Send(1605, [26]); peer.Expect(1, 1605, [0, 1, 0]); peer.Barrier();
            That(Snapshot() == baseline, "畸形黑暗诉求请求无扣费且连接可继续使用");
            Apply(peer, 0, 3, 0x00000003);
            var committed = Reload();
            That(committed.Inventory.Find(Bag(121))?.Core.Count == 74 &&
                committed.Inventory.Find(Bag(122))?.Core.Count == 500 &&
                committed.Inventory.AccountWallet.Get(CurrencyKind.LegendarySoul) == 24,
                "首次诉求持久扣精炼时空石和传说灵魂，不扣永恒黑瞳石");
            baseline = Snapshot();
            peer.Send(1605, Request(1, 4)); peer.Expect(1, 1605, [0, 1, 0]); peer.Barrier();
            That(Snapshot() == baseline, "第二权能选重复状态条件时整笔拒绝");
            Apply(peer, 1, 11, 0x00000B03);
            Apply(peer, 0, 1, 0x00000B01);
            committed = Reload();
            That(committed.Inventory.Find(Bag(121))?.Core.Count == 72 &&
                committed.Inventory.Find(Bag(122))?.Core.Count == 499 &&
                committed.Inventory.AccountWallet.Get(CurrencyKind.LegendarySoul) == 22 &&
                committed.Inventory.Gold == 16600000, "分别首次与重新祈求的费用准确落库，金币不变");
            baseline = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_dark AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'dark rollback'); END");
            peer.Send(1605, Request(0, 2)); peer.Closed();
            That(Snapshot() == baseline, "写库末期失败回滚能力和全部材料费用");
            fixture.Sql("DROP TRIGGER fail_dark");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            string baseline = Snapshot();
            fixture.Sql("CREATE TRIGGER fail_imprint AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'imprint rollback'); END");
            peer.Send(1644, Convert.FromHexString("1B00CBF4F6051C000B1CF705")); peer.Closed();
            That(Snapshot() == baseline, "刻印写库末期失败完整回滚源消耗、目标打造及材料金币");
            fixture.Sql("DROP TRIGGER fail_imprint");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(Reload().Inventory.Find(Bag(26))?.Core.Extension114.Value == 0x00000B01,
                "重连后黑暗诉求能力保持，失败事务没有污染持久状态");
            var recipient = Reload().Inventory.Find(Bag(28))!;
            byte[] imprintRequest = Convert.FromHexString("1B00CBF4F6051C000B1CF705");
            peer.Send(1644, imprintRequest);
            var delta = peer.Expect(0, 14).Body;
            int row = Enumerable.Range(0, U16(delta, 1)).Single(index => U16(delta, 3 + index * 123) == 28);
            That((delta[3 + row * 123 + 10] & 0x1F) == 11,
                "刻印先通知目标打造从十五覆盖为十一");
            peer.Expect(1, 1644, Convert.FromHexString("011C000B1CF705")); peer.Barrier();
            var imprinted = Reload();
            That(imprinted.Inventory.Find(Bag(27)) is null &&
                imprinted.Inventory.Find(Bag(28)) is { } result && result.Uid == recipient.Uid &&
                result.Core.Value == recipient.Core.Value && result.Core.Upgrade == 11 &&
                imprinted.Inventory.Gold == 15100000 && imprinted.Inventory.Find(Bag(121))?.Core.Count == 62 &&
                imprinted.Inventory.Find(Bag(123))?.Core.Count == 40,
                "真实跨材质刻印消耗源和原生费用，目标实例及品级保持");
            string beforeReplay = Snapshot();
            peer.Send(1644, imprintRequest); peer.Expect(1, 1644, [0, 1, 0]); peer.Barrier();
            That(Snapshot() == beforeReplay, "重复刻印旧源不能二次扣除");
            byte[] weaponRequest;
            using (var stream = new MemoryStream())
            {
                using var writer = new BinaryWriter(stream);
                writer.Write((short)29); writer.Write(101030827); writer.Write((short)26); writer.Write(weaponId);
                weaponRequest = stream.ToArray();
            }
            peer.Send(1604, weaponRequest); peer.Expect(0, 14);
            byte[] weaponAck;
            using (var stream = new MemoryStream())
            {
                using var writer = new BinaryWriter(stream);
                writer.Write((byte)1); writer.Write((short)26); writer.Write(weaponId);
                weaponAck = stream.ToArray();
            }
            peer.Expect(1, 1604, weaponAck); peer.Barrier();
            var fused = Reload();
            That(fused.Inventory.Find(Bag(29)) is null && fused.Inventory.Find(Bag(26))?.Core.Upgrade == 11 &&
                fused.Inventory.Gold == 14100000 && fused.Inventory.AccountWallet.Get(CurrencyKind.LegendarySoul) == 12,
                "真实普通夜语与界夜语融合使用独立配方，源打造覆盖目标并原子扣费");
            byte[] upgradeBody = UpgradeRequest(30, 100070604, 100070629, 11);
            peer.Send(1564, upgradeBody); peer.Expect(0, 14); peer.Expect(0, 14);
            peer.Expect(1, 1564, UpgradeAck(30, 100070629)); peer.Barrier();
            var upgraded = Reload();
            That(upgraded.Inventory.Find(Bag(30)) is { Core.ItemId: 100070629, Core.Upgrade: 11 } &&
                upgraded.Inventory.Gold == 12600000 && upgraded.Inventory.Find(Bag(124)) is null &&
                upgraded.Inventory.Find(Bag(121))?.Core.Count == 52 && upgraded.Inventory.Find(Bag(123))?.Core.Count == 30,
                "真实泰波尔斯升级超界按加十一倍率扣费，升级差量先于完整八字节回执");
            string beforeUpgradeReplay = Snapshot();
            peer.Send(1564, upgradeBody); peer.Expect(1, 1564, [0, 1, 0]); peer.Barrier();
            That(Snapshot() == beforeUpgradeReplay, "重复旧升级源编号不能重复扣料");
            var conversionSource = upgraded.Inventory.Find(Bag(31))!;
            peer.Send(1564, UpgradeRequest(31, 100301034, 100301035, 0));
            peer.Expect(0, 14); peer.Expect(0, 14);
            peer.Expect(1, 1564, UpgradeAck(31, 100301035)); peer.Barrier();
            var converted = Reload();
            That(converted.Inventory.Find(Bag(31)) is { } conversionTarget &&
                conversionTarget.Uid == conversionSource.Uid &&
                conversionTarget.Core with { ItemId = 100301034 } == conversionSource.Core &&
                converted.Inventory.Gold == 12100000 && converted.Inventory.AccountWallet.Get(CurrencyKind.LegendarySoul) == 7,
                "真实权能转换使用五传说灵魂及五十万金币，忽略继承选项而保留完整打造");
            foreach (var (slot, id, target) in new (short, int, int)[]
                { (32, 100300946, 100301034), (33, 101030803, 101030827) })
            {
                peer.Send(1564, UpgradeRequest(slot, id, target, 11));
                peer.Expect(0, 14); peer.Expect(0, 14);
                peer.Expect(1, 1564, UpgradeAck(slot, target)); peer.Barrier();
                That(Reload().Inventory.Find(Bag(slot))?.Core.ItemId == target,
                    "普雷首饰与武器黑暗低语分别使用对应升级组");
            }
            byte[] preyImprint;
            using (var stream = new MemoryStream())
            {
                using var writer = new BinaryWriter(stream);
                writer.Write((short)34); writer.Write(100300947); writer.Write((short)35); writer.Write(100301036);
                preyImprint = stream.ToArray();
            }
            peer.Send(1644, preyImprint); peer.Expect(0, 14);
            using (var stream = new MemoryStream())
            {
                using var writer = new BinaryWriter(stream);
                writer.Write((byte)1); writer.Write((short)35); writer.Write(100301036);
                peer.Expect(1, 1644, stream.ToArray());
            }
            peer.Barrier();
            var prey = Reload();
            That(prey.Inventory.Find(Bag(34)) is null && prey.Inventory.Find(Bag(35))?.Core.Upgrade == 11 &&
                prey.Inventory.Gold == 6130000 && prey.Inventory.Find(Bag(121))?.Core.Count == 7 &&
                prey.Inventory.Find(Bag(122))?.Core.Count == 81 && prey.Inventory.Find(Bag(123)) is null,
                "黑暗低语与权能刻印准确区分一百九十、二百二十八黑瞳石及刻印免黑瞳石配方");
        }
        await fixture.StopAsync();
        Character Reload()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.List(owner).Single();
        }
        string Snapshot() => fixture.Snapshot() + fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT count(*) FROM inventory_audit_log") + fixture.Snapshot("SELECT count(*) FROM currency_audit_log");
        void Apply(TcpPeer peer, byte slot, byte ability, uint expected)
        {
            peer.Send(1605, Request(slot, ability));
            byte[] delta = peer.Expect(0, 14).Body;
            int count = U16(delta, 1);
            int index = Enumerable.Range(0, count).Single(row => U16(delta, 3 + row * 123) == 26);
            That(delta[0] == 0 && U32(delta, 3 + index * 123 + 114) == expected,
                "成功先推送所选权能字节的完整物品增量");
            peer.Expect(1, 1605, [1]); peer.Barrier();
            That(Reload().Inventory.Find(Bag(26))?.Core.Extension114.Value == expected,
                "公共成功头与已持久化能力一致");
        }
        byte[] Request(byte slot, byte ability)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((short)26); writer.Write(weaponId); writer.Write(slot); writer.Write(ability);
            return stream.ToArray();
        }
        byte[] UpgradeRequest(short slot, int id, int target, byte level)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(slot); writer.Write(id); writer.Write((byte)0); writer.Write(level);
            writer.Write((byte)0); writer.Write((byte)0); writer.Write(target); writer.Write((byte)0);
            return stream.ToArray();
        }
        // 回执第3字段是提交后的装备编号：客户端 0234C290→0402D110 靠它回查升级/刻印结果窗。
        byte[] UpgradeAck(short slot, int resultId)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)1); writer.Write(slot); writer.Write(resultId); writer.Write((byte)0);
            return stream.ToArray();
        }
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
}
