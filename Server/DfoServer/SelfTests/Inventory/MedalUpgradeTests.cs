using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class MedalUpgradeTests
{
    const int Medal = 100380058, Coin = 490011648;
    static readonly InventorySlot BagSlot = new(InventoryListType.GuildMedal, 0);
    public static void RunCore()
    {
        int before = Count;
        string Resolve(int index) => index switch
        {
            -1 => "[table]", -2 => "[table type]", -3 => "[/table]",
            -4 => "[cost weights by guild flag rarity]", -5 => "[/cost weights by guild flag rarity]", -6 => "flag_upgrade_table",
            _ => throw new InvalidOperationException("unknown token")
        };
        var tokens = ImmutableArray.CreateBuilder<PvfToken>();
        tokens.Add(new(3, -1)); tokens.Add(new(3, -2)); tokens.Add(new(6, -6));
        tokens.AddRange(Zeros(8));
        tokens.AddRange(ImmutableArray.Create(new PvfToken(0, 0), new PvfToken(0, 0), new PvfToken(0, 1), new PvfToken(0, 0),
            new PvfToken(0, 1), new PvfToken(0, 0), new PvfToken(0, Coin), new PvfToken(0, 1)));
        for (int level = 2; level <= 20; level++)
        {
            tokens.AddRange(Zeros(6)); tokens.Add(Float(0.23f)); tokens.Add(new(0, 0));
            tokens.AddRange(Zeros(6)); tokens.Add(Float(1.20f)); tokens.Add(new(0, 0));
            tokens.AddRange(ImmutableArray.Create(new PvfToken(0, level == 2 ? 15000 : level * 4000), new PvfToken(0, level == 2 ? 15000 : level * 4000),
                new PvfToken(0, 1), new PvfToken(0, 0), new PvfToken(0, 1), new PvfToken(0, 0), new PvfToken(0, Coin), new PvfToken(0, level)));
        }
        tokens.AddRange(Zeros(6)); tokens.Add(Float(34.63f)); tokens.Add(new(0, 0));
        tokens.AddRange(Zeros(6)); tokens.Add(Float(33.50f)); tokens.Add(new(0, 0));
        tokens.Add(new(3, -3)); tokens.Add(new(3, -4));
        tokens.AddRange(ImmutableArray.Create(new PvfToken(0, 1), Float(2.20f), Float(5.50f), Float(10.50f), Float(20.00f),
            new PvfToken(0, 15), Float(18.40f)));
        tokens.Add(new(3, -5));
        var rules = MedalUpgradeCatalog.Parse(new(tokens.ToImmutable(), Resolve), Resolve);
        That(rules.Rows.Length == 20 && rules.Rows[0] == new MedalUpgradeRow(0, 1, 0, Coin, 1) &&
            rules.Rows[1] == new MedalUpgradeRow(15000, 1, 0, Coin, 2) &&
            rules.Rows[19] == new MedalUpgradeRow(80000, 1, 0, Coin, 20),
            "首行16项与后续24项按行尾材料列切分，表尾残缺16项因行尾校验失败被忽略");
        That(rules.CoinCost(6, 1) == 18 && rules.CoinCost(6, 2) == 36 && rules.CoinCost(6, 3) == 55 &&
            rules.CoinCost(3, 1) == 10 && rules.CoinCost(0, 5) == 5, "公会硬币按稀有度权重放大后向下取整");
        Reject<InvalidDataException>(() => rules.CoinCost(7, 1), "稀有度超出权重列拒绝");
        Reject<InvalidDataException>(() => rules.CoinCost(-1, 1), "负稀有度拒绝");
        Console.WriteLine($"PASS {Count - before} 勋章强化表切分、行尾校验与硬币取整断言");
    }

    public static void RunResources(TownCatalog towns, ItemResources r)
    {
        int before = Count;
        var rules = r.MedalUpgrades.Rules;
        That(rules.Rows.Length == 20 &&
            rules.Rows[0] == new MedalUpgradeRow(0, 1, 0, Coin, 1) &&
            rules.Rows[1] == new MedalUpgradeRow(15000, 1, 0, Coin, 2) &&
            rules.Rows[7] == new MedalUpgradeRow(68000, 2, 3, Coin, 8) &&
            rules.Rows[9] == new MedalUpgradeRow(77000, 2, 3, Coin, 10) &&
            rules.Rows[10] == new MedalUpgradeRow(80000, 3, 0, Coin, 11) &&
            rules.Rows[19] == new MedalUpgradeRow(95000, 3, 0, Coin, 20),
            "真实PVF的flag_upgrade_table为20行：+1..+7保级、+8..+10掉3级、+11起销毁");
        That(rules.RarityWeights.Select(value => MathF.Round(value, 2)).SequenceEqual([1f, 2.20f, 5.50f, 10.50f, 20.00f, 15f, 18.40f]),
            "公会硬币权重按稀有度0..6读取；采用guild flag列而非普通rarity列");
        var medal = r.Items.Get(Medal);
        var rough = r.Items.Get(100380045);
        That(medal.Kind == ItemKind.GuildMedal && medal.Rarity == 6 && rough.Rarity == 3 && r.Items.Get(Coin).Kind == ItemKind.Material,
            "客户端实测勋章稀有度6/3与PVF一致，公会硬币是可堆叠材料");
        That(rules.CoinCost(medal.Rarity, 1) == 18 && rules.CoinCost(medal.Rarity, 2) == 36 && rules.CoinCost(medal.Rarity, 3) == 55 &&
            rules.CoinCost(rough.Rarity, 1) == 10, "四次实机读数18/36/55/10由公式复现");

        var coin = r.Items.Get(Coin);
        var coinStack = new InventoryItem(2, new(InventoryListType.Main, 121), InventoryRewardGrantService.Create(coin, 100));
        Character Bag(byte level = 0, int coins = 100, int expire = 0, byte lockId = 0, int materialId = Coin,
            InventorySlot? target = null)
        {
            var slot = target ?? BagSlot;
            var item = new InventoryItem(1, slot, InventoryRewardGrantService.Create(medal) with { Attr = level, ExpireTime = expire, EquipmentLockId = lockId });
            return new Character(1, 10000, "MedalUpgrade", 0, towns.Room, null)
            {
                Level = 95,
                Inventory = new([item, coinStack with { Core = InventoryRewardGrantService.Create(r.Items.Get(materialId), coins) }])
            };
        }
        EnhancementRequest Request(InventorySlot target, EnhancementMode mode = EnhancementMode.Reinforce, int itemId = Medal) =>
            new(mode, target, itemId, 121, -1, ReadOnlyMemory<byte>.Empty, -1, -1);
        var bag = Bag(level: 0xE0);
        var service = new InventoryEnhancementService(r.Items, r.Enhancements, r.MedalUpgrades, new FixedRandom(0));
        var bagItem = bag.Inventory.Find(BagSlot)!;
        var result = service.Plan(bag, Request(BagSlot), out var receipt);
        That(result.Accepted && receipt is { OutcomeCode: 0, OldLevel: 0, NewLevel: 1, MaterialRemaining: 82, GoldCost: 0, InPlace: true } &&
            result.Plan!.After.Find(BagSlot) == bagItem with { Core = bagItem.Core with { Attr = 0xE1 } } &&
            result.Plan.After.Find(new(InventoryListType.Main, 121))!.Core.Count == 82 && result.Plan.After.Gold == 0,
            "列表38勋章+0强化成功只改Attr低五位，按PVF扣18枚公会硬币且不收金币");
        var ack = EnhancementPackets.Ack(new(true, 0), receipt);
        That(ack.Length == 27 && ack[1] == 0 && BitConverter.ToInt16(ack, 2) == 121 && BitConverter.ToInt32(ack, 4) == 82 &&
            ack[10] == 0 && ack[11] == 0 && ack[12] == 0 && ack[13] == 1 && ack[14] == 38 && BitConverter.ToInt16(ack, 15) == 0 &&
            BitConverter.ToInt16(ack, 17) == -1 && BitConverter.ToInt16(ack, 19) == -1, "勋章强化沿用CMD80的27B成功回包");
        foreach (var slot in new[] { new InventorySlot(InventoryListType.GuildMedal, 9),
            new InventorySlot(InventoryListType.Main, 3), new InventorySlot(InventoryListType.Equipment, 32) })
        {
            result = service.Plan(Bag(target: slot), Request(slot), out receipt);
            That(result.Accepted && receipt is { NewLevel: 1 }, "勋章页任意槽、快捷栏与穿戴槽32都能强化，不绑定具体槽位");
        }
        // roll必须小于目标+8行的失败权重68000才算失败；72999已达到成功下界。
        var fail = new InventoryEnhancementService(r.Items, r.Enhancements, r.MedalUpgrades, new FixedRandom(67999));
        var drop = fail.Plan(Bag(level: 7, coins: 1000), Request(BagSlot), out receipt);
        That(drop.Accepted && receipt is { OutcomeCode: 2, OldLevel: 7, NewLevel: 4, MaterialRemaining: 1000 - 147 } &&
            drop.Plan!.After.Find(BagSlot)!.Core.Upgrade == 4, "目标+8失败掉3级并照常扣8枚硬币的稀有度换算数");
        var destroy = new InventoryEnhancementService(r.Items, r.Enhancements, r.MedalUpgrades, new FixedRandom(79999));
        var destroyed = destroy.Plan(Bag(level: 10, coins: 1000), Request(BagSlot), out receipt);
        That(destroyed.Accepted && receipt is { OutcomeCode: 3, OldLevel: 10, NewLevel: 0, Refunds.Length: 0 } &&
            destroyed.Plan!.After.Find(BagSlot) is null && destroyed.Plan.After.Find(new(InventoryListType.Main, 121))!.Core.Count == 1000 - 202,
            "目标+11失败销毁，原槽清空、材料照扣并按当前方案不给补偿行");
        var boundary = new InventoryEnhancementService(r.Items, r.Enhancements, r.MedalUpgrades, new FixedRandom(15000));
        That(boundary.Plan(Bag(level: 1), Request(BagSlot), out receipt).Accepted && receipt is { OutcomeCode: 0, NewLevel: 2 },
            "失败权重为闭区间下界：roll等于15000仍成功");
        var rng = new FixedRandom(0);
        var guarded = new InventoryEnhancementService(r.Items, r.Enhancements, r.MedalUpgrades, rng);
        int calls = rng.Calls;
        foreach (var (role, request) in new[]
        {
            (Bag(coins: 17), Request(BagSlot)),
            (Bag(level: 20), Request(BagSlot)),
            (Bag(), Request(BagSlot, EnhancementMode.Amplify)),
            (Bag(), Request(BagSlot, itemId: Medal + 1)),
            (Bag(expire: 1), Request(BagSlot)),
            (Bag(lockId: 1), Request(BagSlot)),
            (Bag(materialId: 3242), Request(BagSlot))
        })
            That(!guarded.Plan(role, request, out _).Accepted && rng.Calls == calls,
                "材料不足、满级、错误模式、错误身份、过期、锁定与非公会硬币在随机前拒绝");
        Console.WriteLine($"PASS {Count - before} 勋章强化真实PVF行、硬币费用、成功/掉级/销毁与拒绝路径断言");
    }

    static PvfToken Float(float value) => new(2, BitConverter.SingleToInt32Bits(value));
    static IEnumerable<PvfToken> Zeros(int count) => Enumerable.Repeat(new PvfToken(0, 0), count);
    sealed class FixedRandom(int value) : Random
    {
        public int Calls { get; private set; }
        public override int Next(int maxValue)
        {
            Calls++;
            return Math.Min(value, maxValue - 1);
        }
    }
    static void Reject<T>(Action action, string message) where T : Exception
    {
        bool rejected = false;
        try { action(); }
        catch (T) { rejected = true; }
        That(rejected, message);
    }
}
