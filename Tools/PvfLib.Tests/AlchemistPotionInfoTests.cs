using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class AlchemistPotionInfoTests
{
    public static void Run()
    {
        string[] names = ["[potion info]", "[/potion info]", "[unrelated]", "text"];
        AlchemistPotionInfoFile Parse(PvfToken[] tokens) => AlchemistPotionInfoFile.Parse(tokens, index => names[index]);
        PvfToken[] valid = [new(3, 0), new(0, 9900001), new(0, 0), new(0, 2600025), new(0, 100),
            new(0, 9900002), new(0, -1), new(3, 1), new(3, 2), new(6, 3)];
        var table = Parse(valid).PotionInfo;
        That(table.Count == 3 && table[9900001] == 0 && table[2600025] == 100 && table[9900002] == -1,
            "参数表完整保留任意编号及0/负整数，不依赖已知财宝编号或在格式层解释效果");
        That(Parse([]).PotionInfo.IsEmpty && Parse([new(3, 0), new(3, 1)]).PotionInfo.IsEmpty,
            "未声明与显式空表均合法，不虚构默认药效");
        foreach (var bad in new PvfToken[][]
        {
            [new(3, 0), new(0, 1), new(3, 1)],
            [new(3, 0), new(0, 0), new(0, 5000), new(3, 1)],
            [new(3, 0), new(0, 1), new(4, 5000), new(3, 1)],
            [new(3, 0), new(0, 1), new(0, 5000), new(0, 1), new(0, 10000), new(3, 1)],
            [..valid, new(3, 0), new(0, 2), new(0, 1000), new(3, 1)]
        }) Reject<InvalidDataException>(() => Parse(bad));

    }
}
