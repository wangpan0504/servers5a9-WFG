using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class MonsterCardUpgradeTests
{
    public static void RunCore()
    {
        int checks = Count;
        var request = MonsterCardUpgradeRequests.Read(Convert.FromHexString("00E9000200EA0000EB00"));
        That(request.TargetSlot == 233 && request.MaterialSlots.SequenceEqual([(short)234, (short)235]),
            "CMD603独立解析Main目标、材料数及每个Main材料槽");
        foreach (string invalid in new[] { "", "00E900", "00E90000", "00E9000500EA00", "01E9000100EA00",
            "00E9000101EA00", "0060010100EA00",
            "00E90001006001", "00E9000100EA0000", "00E9000100EA" })
            Reject<InvalidDataException>(() => MonsterCardUpgradeRequests.Read(Convert.FromHexString(invalid)),
                "CMD603拒绝缺项、尾字节、非Main容器及越界");
        That(MonsterCardUpgradeRequests.Read(Convert.FromHexString("00E9000200EA0000EA00")).MaterialSlots
                .SequenceEqual([(short)234, (short)234]) &&
            MonsterCardUpgradeRequests.Read(Convert.FromHexString("00E9000100E900")).MaterialSlots[0] == 233,
            "CMD603保留重复材料源槽及与目标同源的材料，张数交领域校验");
        That(Convert.ToHexString(MonsterCardUpgradePackets.Success(new(true, 10310789, 1))) == "010185549D0001" &&
            Convert.ToHexString(MonsterCardUpgradePackets.Success(new(false, 10310789, 0))) == "010085549D0000" &&
            Convert.ToHexString(MonsterCardUpgradePackets.Failure()) == "001600",
            "603提交成功与概率失败都携带实际结果，业务拒绝另走公共错误码");

        var fixture = new Fixture();
        var role = fixture.Role();
        var target = role.Inventory.Find(new(InventoryListType.Main, 233))!;
        var success = fixture.Service(69).Apply(role, request, out var receipt, out _);
        That(success.Accepted && receipt is { Upgraded: true, ItemId: 1001, Level: 1 } &&
            success.Plan!.After.Find(target.Position) == target with { Core = target.Core with { EnchantUpgrade = 1 } } &&
            success.Plan.After.Items.Length == 1 && success.Plan.After.Gold == role.Inventory.Gold,
            "两张材料70%边界命中：目标原UID与全部字段保留，仅卡片等级加一且不扣金币");
        var failed = fixture.Service(70).Apply(role, request, out var failedReceipt, out _);
        That(failed.Accepted && failedReceipt is { Upgraded: false, Level: 0 } &&
            failed.Plan!.After.Items.SequenceEqual([target]) && role.Inventory.Items.Length == 3,
            "70%边界外概率失败只扣各一张材料，保留目标和原始快照");
        var stack = fixture.Role(targetCount: 3);
        var split = fixture.Service(0).Apply(stack, request, out _, out _);
        That(split.Accepted && split.Plan!.After.Find(target.Position)?.Core is { Value: 2, EnchantUpgrade: 0 } &&
            split.Plan.After.Items.Count(row => row.Core is { ItemId: 1001, Value: 1, EnchantUpgrade: 1 }) == 1 &&
            split.Plan.After.Items.Single(row => row.Core.EnchantUpgrade == 1).Uid == 0,
            "堆叠只升级一张，旧等级余量保留原UID，新增实例等待提交分配UID");
        var repeated = fixture.Service(0).Apply(fixture.Role(materialCount: 3), new(233, [234, 234]), out _, out _);
        That(repeated.Accepted && repeated.Plan!.After.Find(new(InventoryListType.Main, 234))?.Core.Count == 1 &&
            repeated.Plan.After.Find(target.Position)?.Core.EnchantUpgrade == 1,
            "重复材料源槽扣实际两张，目标只升级一张");
        foreach (int count in new[] { 3, 4 })
        {
            var sameSource = fixture.Role(targetCount: count);
            var sameSuccess = fixture.Service(0).Apply(sameSource, new(233, [233, 233]), out _, out _);
            That(sameSuccess.Accepted && sameSuccess.Plan!.After.Items.Where(row => row.Core.ItemId == 1001)
                    .Sum(row => row.Core.Count) == count &&
                sameSuccess.Plan.After.Items.Count(row => row.Core.EnchantUpgrade == 1 && row.Core.Count == 1) == 1 &&
                sameSource.Inventory.Find(target.Position)?.Core.Count == count,
                "目标同源堆扣两张材料后升级一张，覆盖原槽仅余目标及需要分堆，原快照不变");
            var sameFailed = fixture.Service(80).Apply(sameSource, new(233, [233, 233]), out _, out _);
            That(sameFailed.Accepted && sameFailed.Plan!.After.Find(target.Position)?.Core is { EnchantUpgrade: 0 } remaining &&
                remaining.Count == count - 2,
                "目标同源堆概率失败仅扣两张材料，保留目标及其余未升级卡");
        }
        foreach (var invalidRequest in new MonsterCardUpgradeRequest[] { new(233, [234, 234]), new(233, [233]) })
            That(!fixture.Service(0).Apply(role, invalidRequest, out _, out var countReason).Accepted && countReason == "material-count",
                "不足重复材料或不能另留目标一张时整笔拒绝");
        var mergeRole = fixture.Role(targetCount: 3, materialCount: 2);
        var existing = new InventoryItem(999, new(InventoryListType.Main, 236), target.Core with { Value = 4, EnchantUpgrade = 1 });
        mergeRole = mergeRole with { Inventory = mergeRole.Inventory with { Items = mergeRole.Inventory.Items.Add(existing) } };
        var merged = fixture.Service(0).Apply(mergeRole, request, out _, out _);
        That(merged.Accepted && merged.Plan!.After.Find(existing.Position) == existing with { Core = existing.Core with { Value = 5 } } &&
            merged.Plan.After.Find(new(InventoryListType.Main, 234))?.Core.Count == 1 &&
            merged.Plan.After.Find(target.Position)?.Core.Count == 2,
            "成功优先合并同等级完整属性堆，保留既有UID，材料堆各只扣一张");
        var upgradedMaterial = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Select(row =>
            row.Position.Index == 234 ? row with { Core = row.Core with { EnchantUpgrade = 1 } } : row).ToImmutableArray() } };
        That(fixture.Service(0).Apply(upgradedMaterial, request, out _, out _).Accepted,
            "同稀有度已升级材料仍可消耗，不擅加客户端没有的等级限制");
        foreach (var core in new[] { target.Core with { EnchantUpgrade = 1 }, target.Core with { EquipmentLockId = 1 },
            target.Core with { ExpireTime = 1 }, target.Core with { Kind = ItemKind.Material }, target.Core with { ItemId = 1003 } })
        {
            var invalid = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Replace(target, target with { Core = core }) } };
            That(!fixture.Service(0).Apply(invalid, request, out var rejected, out _).Accepted && rejected is null,
                "满级、锁定、到期、类别不符及非卡片目标整笔拒绝");
        }
        var material = role.Inventory.Items[1];
        foreach (var core in new[] { material.Core with { ItemId = 1002 }, material.Core with { EquipmentLockId = 1 },
            material.Core with { ExpireTime = 1 }, material.Core with { ItemId = 1003 } })
        {
            var invalid = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Replace(material, material with { Core = core }) } };
            That(!fixture.Service(0).Apply(invalid, request, out _, out _).Accepted,
                "不同稀有度、锁定、过期、非卡片材料拒绝且无扣费计划");
        }
        var full = fixture.Role(targetCount: 2, materialCount: 2);
        var slots = Enumerable.Range(236, 53).Select(slot => new InventoryItem(slot, new(InventoryListType.Main, (short)slot),
            new(1003, ItemKind.Profession, 1)));
        full = full with { Inventory = full.Inventory with { Items = full.Inventory.Items.AddRange(slots) } };
        That(!fixture.Service(0).Apply(full, request, out _, out var reason).Accepted && reason == "card-capacity",
            "满卡片页且源堆仍有余量时整笔拒绝，不吞升级卡或材料");
        That(!fixture.Service(80).Apply(full, request, out _, out _).Accepted,
            "容量资格先于随机结果，不能无空间时只结算失败");
        That(fixture.Rates.CardUpgradeChance(6, 1) == 40 && fixture.Rates.CardUpgradeChance(6, 2) == 80 &&
            fixture.Rates.CardUpgradeChance(6, 3) == 100, "目录保留原资源40/80/100，材料业务成功率由本服规则覆盖");
        Reject<InvalidDataException>(() => fixture.Rates.CardUpgradeChance(6, 4), "零概率材料数不可冒充有效升级配置");
        MaterialSuccessRules();
        foreach (int[] levels in new[] { new[] { 1 }, new[] { 0, 2 }, new[] { 0, 0 } })
        {
            var catalog = new EnchantmentCatalog(_ => "", new Dictionary<int, string> { [1001] = "card.stk" }, _ => Table(levels));
            Reject<InvalidDataException>(() => catalog.CardMaximumLevel(1001), "拒绝缺零级、跳级与重复等级资源");
        }
        Console.WriteLine($"PASS {Count - checks} 卡片升级协议、概率、分堆与资产边界断言");
    }

    public static void RunResources(ItemResources resources)
    {
        That(resources.Items.Get(10310789) is { IsMonsterCard: true, Rarity: 6 } &&
            resources.Enchantments.CardMaximumLevel(10310789) == 1, "报告四维100→150卡为传说且可升一级");
        var effects = resources.Enchantments.ReadEffects(10310789, 1);
        That(effects.Length == 4 && effects.All(row => row.IntegerValues.SequenceEqual([150])),
            "升级后的四维由正式卡片index1完整效果派生");
        That(resources.Enchantments.CardMaximumLevel(3620) == 10 &&
            resources.MonsterCardBinds.CardUpgradeChance(0, 1) == 100,
            "普通猫妖卡保留十级上限，原资源一张100不作为本服实际成功率");
    }

    static void MaterialSuccessRules()
    {
        var defaults = MonsterCardUpgradeRules.Parse("{}");
        That(defaults.MaterialSuccessPercent.SequenceEqual([30, 70, 100]), "默认材料成功率为30/70/100，不是翻牌等级分布");
        foreach (string json in new[] { "null", "{\"MaterialSuccessPercent\":[30,70]}",
            "{\"MaterialSuccessPercent\":[-1,70,100]}", "{\"MaterialSuccessPercent\":[71,70,100]}",
            "{\"MaterialSuccessPercent\":[30,70,99]}", "{\"MaterialSuccessPercent\":[30,101,100]}" })
            Reject<InvalidDataException>(() => MonsterCardUpgradeRules.Parse(json), "无效规则不能静默回退或破坏三材料必成");
        Reject<System.Text.Json.JsonException>(() => MonsterCardUpgradeRules.Parse("{\"MaterialSuccessPercents\":[30,70,100]}"), "配置拼写错误必须拒绝");
        Reject<System.Text.Json.JsonException>(() => MonsterCardUpgradeRules.Parse("{\"MaterialSuccessPercent\":null}"), "集合字段为null须明确拒绝");
        foreach (int rarity in new[] { 0, 6 })
        {
            var fixture = new Fixture(3, rarity);
            for (byte level = 0; level < 3; level++)
                foreach (int materials in new[] { 1, 2, 3 })
                {
                    var role = fixture.Role(materialCount: 3);
                    var target = role.Inventory.Items[0];
                    role = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Replace(target,
                        target with { Core = target.Core with { EnchantUpgrade = level } }) } };
                    int successes = 0;
                    for (int roll = 0; roll < 100; roll++)
                    {
                        var result = fixture.Service(roll).Apply(role,
                            new(233, Enumerable.Repeat((short)234, materials).ToImmutableArray()), out var receipt, out _);
                        That(result.Accepted && receipt is not null && receipt.Level == level + (receipt.Upgraded ? 1 : 0),
                            "各稀有度各升级级别只增一级，概率失败保留原级别");
                        That(result.Plan!.After.Items.Where(item => item.Position.Index == 234).Sum(item => item.Core.Count) == 3 - materials &&
                            result.Plan.After.Gold == role.Inventory.Gold, "每个随机结果都只消耗实际材料张数，不扣金币");
                        if (receipt!.Upgraded) successes++;
                    }
                    That(successes == new[] { 30, 70, 100 }[materials - 1], "穷举百个抽样点，所有级别和稀有度都精确30/70/100");
                }
            That(!fixture.Service(0).Apply(fixture.Role(materialCount: 4), new(233, [234, 234, 234, 234]), out _, out _).Accepted,
                "四张材料不扩成未经授权的新档位");
        }
        var configured = MonsterCardUpgradeRules.Parse("{\"MaterialSuccessPercent\":[0,100,100]}");
        var customized = new Fixture();
        That(customized.Service(0, configured).Apply(customized.Role(), new(233, [234]), out var miss, out _).Accepted &&
            miss is { Upgraded: false }, "配置零成功率实际生效，失败仍消耗材料");
        That(customized.Service(99, configured).Apply(customized.Role(), new(233, [234, 235]), out var hit, out _).Accepted &&
            hit is { Upgraded: true }, "配置两材料100实际生效，保持真实升级流程");
    }

    static PvfTokenDocument Table(params int[] levels)
    {
        var strings = new[] { "[enchant table]", "[enchant index]", "[physical attack]", "[/enchant index]", "[/enchant table]" };
        var tokens = new List<PvfToken> { new(3, 0) };
        foreach (int level in levels)
            tokens.AddRange([new(3, 1), new(0, level), new(3, 2), new(0, 100 + level * 50), new(3, 3)]);
        tokens.Add(new(3, 4));
        return new(tokens.ToArray(), index => strings[index]);
    }

    sealed class Fixture
    {
        readonly ItemCatalog items;
        readonly EnchantmentCatalog enchantments;
        readonly GoldLimitCatalog limits = new("[gold limit from level]\n0 1000000\n[/gold limit from level]");
        readonly EpicPieceCatalog pieces = new("[piece list]\n50000 50001\n[/piece list]");
        public MonsterCardBindCatalog Rates { get; } = new("""
            [upgrade infos]
            [upgrade info]
            6 40 80 100 0 0
            [upgrade info]
            0 100 0 0 0 0
            [/upgrade infos]
            [bind result infos]
            [bind result info]
            0 1.00 5 1001 1
            [/bind result info]
            [/bind result infos]
            [bind shpere result infos]
            [bind result info]
            0 1.00 5 1001 1
            [/bind result info]
            [/bind shpere result infos]
            [bind shpere upgrade info]
            3 200
            [/bind shpere upgrade info]
            """);

        public Fixture(int maximumLevel = 1, int rarity = 6)
        {
            var paths = new Dictionary<int, string> { [1001] = "target.stk", [1002] = "mixed.stk", [1003] = "other.stk" };
            items = new(null!, new Dictionary<int, string>(), paths, 0);
            items.PrefillTemplates(paths.Keys.Select(id => new ItemTemplate(id, ItemKind.Profession, 0, 1, 0, 1000, 0,
                id == 1002 ? 3 : rarity, 0, "[trade]", ushort.MaxValue, 0, "card", "[material expert job] 1")
                { IsMonsterCard = id != 1003 }).ToDictionary(row => row.Id));
            enchantments = new(_ => "", paths, _ => Table(Enumerable.Range(0, maximumLevel + 1).ToArray()));
        }
        public InventoryMonsterCardUpgradeService Service(int roll, MonsterCardUpgradeRules? rules = null) =>
            new(items, enchantments, Rates, limits, pieces, _ => roll, rules);
        public Character Role(int targetCount = 1, int materialCount = 1) => new(1, 0, "CardUpgrade", 0, new(1, 0, 10, 10), null)
        {
            Level = 95,
            Inventory = new(Enumerable.Range(233, 3).Select(slot => new InventoryItem(slot,
                new(InventoryListType.Main, (short)slot), new(1001, ItemKind.Profession, slot == 233 ? targetCount : materialCount)
                { Attr = 0x20, SortLockFlag = 1 })).ToImmutableArray())
        };
    }
}
