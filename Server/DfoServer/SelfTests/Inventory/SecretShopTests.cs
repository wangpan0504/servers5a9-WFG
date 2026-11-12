using System.Text.RegularExpressions;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class SecretShopTests
{
    static readonly string Rules = """
        [eplp wait time]
        3
        [price random]
        10
        [cash user stackable count]
        200
        [add slot cash user stackable count]
        100
        [dungeon npc]
        101 1000 9500 9490 9480 9470 9460
        101 1003 500 510 520 530 540
        [/dungeon npc]
        [level section]
        0 1 92 1 93 100
        [/level section]
        [level npc]
        0 1000 9500 9490 9480 9470 9460
        0 1003 500 510 520 530 540
        1 1000 9500 9490 9480 9470 9460
        1 1003 500 510 520 530 540
        [/level npc]
        [npc]
        1003
        [level]
        0 2 3037 0 1000 0 15 50
        [/level]
        [level]
        1 2 3167 0 2000 0 10 100 3300 0 500 0 5 50
        [/level]
        [cash item]
        1 3 3167 0 2000 0 20 100 3300 0 500 0 10 50
        [/cash item]
        [dungeon]
        101 1 3037 0 100 0 30 10
        [/dungeon]
        [dungeon cash item]
        101 2 3037 0 100 0 60 10
        [/dungeon cash item]
        [/npc]
        [secret shop end]
        """.Replace("\r\n", "\n");

    public static void RunCore()
    {
        int before = Count;
        var rules = Parse(Rules);
        var merchant = rules.Merchants[1003];
        That(Parse(Rules.Replace("[/dungeon]", "[/dungeon item]")).Merchants[1003].Dungeons[101].Products[0].Count == 30,
            "资源中已存在的dungeon item闭合别名保留，仅补明确别名不放宽缺闭合校验");
        That(rules.WaitSeconds == 3 && rules.PriceRandom == 10 && rules.CashStackableCount == 200 &&
            rules.ExtraCashStackableCount == 100 && rules.Sections.Length == 2,
            "普通神秘店读取独立出现表、价格参数和完整等级段，不借用碎片店概率");
        That(rules.DungeonAppearance[0].Merchant == SecretShopCatalog.AbsentMerchant &&
            rules.LevelAppearance[3].Weights.SequenceEqual(new[] { 500, 510, 520, 530, 540 }),
            "未出现占有明确1000权重，五种难度分开保留，不把零权重解释为必出");
        That(merchant.Levels[1].Slots == 2 && merchant.Levels[1].Products[0] == new SecretShopProduct(3167, 0, 2000, 0, 10, 100) &&
            merchant.CashLevels[1].Slots == 3 && merchant.CashLevels[1].Products[0].Count == 20 &&
            merchant.Dungeons[101].Products[0].Count == 30 && merchant.CashDungeons[101].Products[0].Count == 60,
            "普通、契约、指定副本与指定副本契约池分开保存，数量及权重不相互覆盖");
        foreach (string invalid in new[]
        {
            Rules.Replace("3\n[price random]", "-1\n[price random]"),
            Rules.Replace("10\n[cash user", "101\n[cash user"),
            Rules.Replace("0 1 92 1 93 100", "0 1 95 1 93 100"),
            Rules.Replace("0 1 92 1 93 100", "0 1 92 0 93 100"),
            Rules.Replace("101 1000 9500", "101 1000 9501"),
            Rules.Replace("1 1003 500", "1 1004 500"),
            Rules.Replace("3167 0 2000 0 10 100", "3167 0 2000 0 0 100"),
            Rules.Replace("3167 0 2000 0 10 100", "3167 0 2000 0 10"),
            Rules.Replace("[/dungeon cash item]", "")
        })
            Reject<InvalidDataException>(() => Parse(invalid), "神秘店畸形标量、区间、出现权重、悬空商人及截断商品拒绝");
        That(SecretShopRequests.Open([1]) && !SecretShopRequests.Open([0]) &&
            SecretShopRequests.Buy(Convert.FromHexString("5F0C000003000000")) == new SecretShopPurchase(3167, 3),
            "297单字节开关与296两个i32来自实际原生请求，不接收NPC号或报价");
        foreach (byte[] malformed in new byte[][] { [], [2], [1, 0] })
            Reject<InvalidDataException>(() => SecretShopRequests.Open(malformed), "开关严格保持一字节0/1");
        foreach (int length in new[] { 0, 7, 9 })
            Reject<InvalidDataException>(() => SecretShopRequests.Buy(new byte[length]), "购买拒绝截断或尾随字节");
        Bytes(SecretShopPackets.Merchant(1000), Convert.FromHexString("E8030000"), "279的无商人编号是1000");
        Bytes(SecretShopPackets.Offers(new(1003, [new(3167, 0, 1000, 5)])),
            Convert.FromHexString("010000005F0C000000E803000005000000000000000000000000"),
            "280固定u32行数及u32/u8/四u32/u8，两个额外费用字段为零");
        Console.WriteLine($"PASS {Count - before} 神秘商店资源目录断言");
    }

    // 确定性目录只交给隔离Runtime，不改正式PVF和生产出现概率。
    internal static SecretShopCatalog Guaranteed() => Parse(Rules
        .Replace("9500 9490 9480 9470 9460", "0 0 0 0 0")
        .Replace("500 510 520 530 540", "10000 10000 10000 10000 10000")
        .Replace("0 2 3037 0 1000 0 15 50", "0 2 3167 0 1000 0 10 100 101010912 0 2000 0 1 100")
        .Replace("1 2 3167 0 2000 0 10 100 3300 0 500 0 5 50", "1 2 3167 0 1000 0 10 100 101010912 0 2000 0 1 100"));

    internal static SecretShopCatalog Silent() => Parse(Rules
        .Replace("9500 9490 9480 9470 9460", "10000 10000 10000 10000 10000")
        .Replace("500 510 520 530 540", "0 0 0 0 0"));

    internal static SecretShopCatalog Parse(string text)
    {
        var names = new List<string>();
        var tokens = new List<PvfToken>();
        foreach (Match match in Regex.Matches(text.Replace("\r\n", "\n"), @"\[[^\]]+\]|-?\d+"))
        {
            if (match.Value[0] == '[')
            {
                tokens.Add(new(3, names.Count));
                names.Add(match.Value);
            }
            else tokens.Add(new(0, int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture)));
        }
        return new(tokens, index => names[index]);
    }
}
