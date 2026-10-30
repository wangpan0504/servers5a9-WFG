using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class MonsterCardBindTests
{
    public static void RunCore()
    {
        int checks = Count;
        var parsed = MonsterCardBindRequests.Read(Body(65, 233, 234));
        That(parsed.SynthesizerSlot == 65 && parsed.CardSlots.SequenceEqual([(short)233, (short)234]),
            "CMD414按合成器槽、张数和卡片Main槽解析");
        foreach (byte[] malformed in new byte[][]
        {
            [], [1], [65, 0], [65, 0, 1], [65, 0, 2, 1],
            [65, 0, 0], [65, 0, 6, 1, 0, 2, 0, 3, 0, 4, 0, 5, 0, 6, 0],
            [0x60, 1, 2, 1, 0, 2, 0],
            [65, 0, 2, 65, 0, 233, 0],
            [65, 0, 2, 233, 0, 233, 0]
        })
            Reject<InvalidDataException>(() => MonsterCardBindRequests.Read(malformed),
                "CMD414拒绝长度、槽位、合成器占用和重复卡片槽");

        var contents = MonsterCardBindCatalog.ParsePools("""
            [bind result info]
            0 1.00 5 2001 10
            [/bind result info]
            [bind result info]
            1 3.00 4 2002 10
            [/bind result info]
            """, "bind");
        That(contents.Count == 2 && contents[0] is { Coefficient: 1.00m, CountHint: 5, Rewards: [{ ItemId: 2001, Weight: 10 }] } &&
            contents[1] is { Coefficient: 3.00m, Rewards: [{ ItemId: 2002 }] },
            "bind result infos按稀有度保留系数、提示张数和正权重卡片");
        That(MonsterCardBindCatalog.UpgradeChance(1.00m) == 100 &&
            MonsterCardBindCatalog.UpgradeChance(12.00m) == 1200 &&
            MonsterCardBindCatalog.UpgradeChance(0.00m) == 0,
            "旧合成器升档把PVF系数读作万分比，1.00对应1%");
        var liveUpgrade = MonsterCardBindCatalog.ParseUpgradeRows("""
            [upgrade info]
            0 100 0 0 0 0
            [upgrade info]
            1 80 100 0 0 0
            """);
        That(liveUpgrade.Count == 2 && liveUpgrade[0].Weights[0] == 100 && liveUpgrade[1].Weights[0] == 80,
            "现场upgrade info没有闭合标签，按下一标签切行");
        That(MonsterCardBindCatalog.ParseUpgradeRows("").Count == 0 &&
            MonsterCardBindCatalog.ParseUpgradeRows("[upgrade info]\n0 100 0 0 0 0\n[/upgrade info]")[0].Weights[0] == 100,
            "upgrade infos允许缺省，闭合标签仍可读");
        Reject<InvalidDataException>(() => MonsterCardBindCatalog.ParseUpgradeRows("[upgrade info]\n[/upgrade info]"),
            "出现upgrade info行但没有权重时仍拒绝");
        Reject<InvalidDataException>(() => MonsterCardBindCatalog.ParsePools(
            "[bind result info]\n0 1.00 5 2001\n[/bind result info]", "bind"),
            "奖池拒绝截断的物品/权重对");
        Reject<InvalidDataException>(() => MonsterCardBindCatalog.ParsePools(
            "[bind result info]\n0 1.00 5 2001 10\n[/bind result info]\n[bind result info]\n0 1.00 5 2002 10\n[/bind result info]", "bind"),
            "同一套奖池拒绝重复稀有度");

        var fixture = new Fixture();
        var request = new MonsterCardBindRequest(65, [233, 234]);
        var same = fixture.Service(Rolls(100, 0)).Apply(fixture.Character((65, 9000, 1), (233, 1001, 1), (234, 1002, 1)),
            request, out var sameReceipt, out var sameRejection);
        That(same.Accepted && sameRejection is null && sameReceipt is
            { BindType: 0, InputRarity: 0, OutputRarity: 0, RarityUpgraded: false, Rewards: [{ ItemId: 2001, Extra: 0 }] },
            "旧合成器两张同稀有度未命中升档时从原档奖池抽取");
        That(same.Plan!.After.Find(new(InventoryListType.Main, 65)) is null &&
            same.Plan.After.Find(new(InventoryListType.Main, 234)) is null &&
            same.Plan.After.Find(new(InventoryListType.Main, 233))?.Core.ItemId == 2001,
            "先扣合成器与卡片，奖励可复用刚腾出的卡片槽");

        var upgraded = fixture.Service(Rolls(0, 0)).Apply(fixture.Character((65, 9000, 1), (233, 1001, 1), (234, 1002, 1)),
            request, out var upgradedReceipt, out var upgradedRejection);
        That(upgraded.Accepted && upgradedRejection is null && upgradedReceipt is
            { OutputRarity: 1, RarityUpgraded: true, Rewards: [{ ItemId: 2002 }] },
            "命中升档时从高一档奖池抽取，不把upgrade infos当合成概率");

        var sphere = fixture.Service(Rolls(0)).Apply(
            fixture.Character((65, 9003, 1), (233, 1001, 1), (234, 1002, 1), (235, 1001, 1), (236, 1002, 1), (237, 1001, 1)),
            new(65, [233, 234, 235, 236, 237]), out var sphereReceipt, out var sphereRejection);
        That(sphere.Accepted && sphereRejection is null && sphereReceipt is
            { BindType: 3, OutputRarity: 1, Rewards: [{ ItemId: 3002 }] },
            "普通合成球满五张按说明升到高级奖池，不套旧合成器系数");

        var mixed = fixture.Service(Rolls(0)).Apply(fixture.Character((65, 9000, 1), (233, 1001, 1), (234, 1101, 1)),
            request, out _, out var mixedReason);
        That(mixed is { Accepted: false, Error: 22 } && mixedReason == "mixed-rarity",
            "混合稀有度在缺少客户端合同前整笔拒绝");

        var enchanted = fixture.Service(Rolls(0)).Apply(
            fixture.Character(1, (65, 9000, 1), (233, 1001, 1), (234, 1002, 1)),
            request, out _, out var enchantedReason);
        That(enchanted is { Accepted: false, Error: 22 } && enchantedReason == "material-state",
            "已升级卡片缺少合成合同，整笔拒绝");

        var wrongCount = fixture.Service(Rolls(0)).Apply(
            fixture.Character((65, 9000, 1), (233, 1001, 1), (234, 1002, 1), (235, 1001, 1)),
            new(65, [233, 234, 235]), out _, out var countReason);
        That(wrongCount is { Accepted: false, Error: 22 } && countReason == "invalid-count",
            "旧合成器只接受两张卡片");

        var full = fixture.FullCharacter();
        var fullResult = fixture.Service(Rolls(100, 0)).Apply(full, request, out var fullReceipt, out var fullReason);
        That(fullResult is { Accepted: false, Error: 23, Plan: null } && fullReceipt is null &&
            fullReason == "card-capacity" && full.Inventory.Find(new(InventoryListType.Main, 233))?.Core.Count == 2,
            "卡片页满且输入堆仍有剩余时回23，原快照不扣材料");

        That(Convert.ToHexString(MonsterCardBindPackets.Success(new(65, 0, 0, 0, false, [233, 234],
                [new(233, 3619, 1, 0)]))) == "01410002E900EA0001E900230E00000100000000" &&
            Convert.ToHexString(MonsterCardBindPackets.Failure()) == "001600" &&
            Convert.ToHexString(MonsterCardBindPackets.Failure(23)) == "001700",
            "成功ACK含合成器槽、删除槽和奖励slot/itemId/value/extra，失败写u16错误码");
        Console.WriteLine($"PASS {Count - checks} 怪物卡片合成协议、奖池、升档及领域事务断言");
    }

    public static void RunResources(ItemResources resources)
    {
        var bind = resources.MonsterCardBinds;
        That(bind.BindPools.Count() == 4 && bind.SpherePools.Count() == 4,
            "现场monstercardcontents同时提供旧奖池与五卡奖池各四档");
        foreach (var pool in bind.BindPools.Concat(bind.SpherePools))
        {
            That(pool.Rewards.Length > 0 && pool.Coefficient >= 0 && pool.CountHint >= 0,
                "现场卡片合成奖池保留正权重候选");
            foreach (var reward in pool.Rewards)
            {
                var template = resources.Items.Get(reward.ItemId);
                That(template.IsMonsterCard && template.Rarity == pool.Rarity && template.MonsterCardBindType is null &&
                    reward.Weight > 0, "现场奖池候选必须是对应稀有度的怪物卡片");
            }
        }
        int[] binders = [2600479, 2600480, 2600481, 2682573, 2682574, 2682575, 10307440, 10307439, 10307441];
        int[] types = [0, 1, 2, 0, 1, 2, 3, 4, 5];
        for (int index = 0; index < binders.Length; index++)
        {
            var template = resources.Items.Get(binders[index]);
            That(template.MonsterCardBindType == types[index] && !template.IsMonsterCard,
                $"现场合成器{binders[index]}声明monstercard bind={types[index]}");
        }
        That(bind.SphereUpgradeChances[3] == 200 && bind.SphereUpgradeChances[5] == 200,
            "现场五卡升级附加按张数给出200，不并入本次普通奖励核心");
        That(bind.UpgradeRows.Any(row => row.Rarity == 0 && row.Weights[0] == 100),
            "upgrade infos按CMD603升级成功率保存，不参与414合成概率");
    }

    static byte[] Body(ushort synthesizer, params ushort[] cards)
    {
        byte[] body = new byte[3 + cards.Length * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(body, synthesizer);
        body[2] = checked((byte)cards.Length);
        for (int index = 0; index < cards.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(3 + index * 2), cards[index]);
        return body;
    }

    static Func<long, long> Rolls(params long[] values)
    {
        var queue = new Queue<long>(values);
        return _ => queue.Dequeue();
    }

    sealed class Fixture
    {
        readonly GoldLimitCatalog limits = new("[gold limit from level]\n0 1000000\n[/gold limit from level]");
        readonly EpicPieceCatalog pieces = new("[piece list]\n50000 50001\n[/piece list]");
        public ItemCatalog Items { get; }
        public MonsterCardBindCatalog Compounds { get; }

        public Fixture()
        {
            Compounds = new("""
                [upgrade infos]
                [upgrade info]
                0 100 0 0 0 0
                [/upgrade info]
                [/upgrade infos]
                [bind result infos]
                [bind result info]
                0 1.00 5 2001 10
                [/bind result info]
                [bind result info]
                1 3.00 4 2002 10
                [/bind result info]
                [/bind result infos]
                [bind shpere result infos]
                [bind result info]
                0 2.70 5 3001 10
                [/bind result info]
                [bind result info]
                1 2.60 5 3002 10
                [/bind result info]
                [/bind shpere result infos]
                [bind shpere upgrade info]
                3 200 4 200 5 200
                [/bind shpere upgrade info]
                """);
            Items = new(null!, new Dictionary<int, string>(), new Dictionary<int, string>
            {
                [9000] = "binder0.stk", [9003] = "binder3.stk",
                [1001] = "card0a.stk", [1002] = "card0b.stk", [1101] = "card1.stk",
                [2001] = "reward0.stk", [2002] = "reward1.stk",
                [3001] = "sphere0.stk", [3002] = "sphere1.stk"
            }, 0);
            Items.PrefillTemplates(new[]
            {
                Binder(9000, 0), Binder(9003, 3),
                Card(1001, 0), Card(1002, 0), Card(1101, 1),
                Card(2001, 0), Card(2002, 1), Card(3001, 0), Card(3002, 1)
            }.ToDictionary(template => template.Id));
        }

        public InventoryMonsterCardBindService Service(Func<long, long> random) =>
            new(Items, Compounds, limits, pieces, random);

        public Character Character(params (short Slot, int ItemId, int Count)[] rows) =>
            Character(0, rows);

        public Character Character(byte enchant, params (short Slot, int ItemId, int Count)[] rows)
        {
            ItemKind Kind(int itemId) => Items.Get(itemId).Kind;
            return new(1, 0, "CardBind", 0, new(1, 0, 10, 10), null)
            {
                Job = 0,
                Level = 95,
                Inventory = new(rows.Select((row, index) => new InventoryItem(index + 1,
                    new(InventoryListType.Main, row.Slot),
                    new(row.ItemId, Kind(row.ItemId), row.Count)
                    {
                        EnchantUpgrade = row.ItemId == 9000 || row.ItemId == 9003 ? (byte)0 : enchant
                    })).ToImmutableArray())
            };
        }

        public Character FullCharacter()
        {
            var range = ItemSlotBoundService.Range(ItemKind.Profession, (ushort)24);
            var rows = Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot => new InventoryItem(slot,
                new(InventoryListType.Main, checked((short)slot)),
                new(1001, ItemKind.Profession, slot is 233 or 234 ? 2 : 1))).ToImmutableArray();
            var items = rows.Add(new InventoryItem(9000, new(InventoryListType.Main, 65),
                new(9000, ItemKind.Consumable, 1)));
            return new(1, 0, "FullCard", 0, new(1, 0, 10, 10), null)
            {
                Job = 0,
                Level = 95,
                Inventory = new(items)
            };
        }

        static ItemTemplate Binder(int id, int type) =>
            new(id, ItemKind.Consumable, 0, 1, 0, 1000, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "binder", "[etc] 0")
            {
                MonsterCardBindType = type
            };

        static ItemTemplate Card(int id, int rarity) =>
            new(id, ItemKind.Profession, 0, 1, 0, 1000, 0, rarity, 0, "[trade]", ushort.MaxValue, 0, "card",
                "[material expert job] 0")
            {
                IsMonsterCard = true
            };
    }
}
