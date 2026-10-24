using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class PlayerDisjointTests
{
    const string Results = """
        0 0 0 3037 1.0 0 0 0
        0 1 0 3037 1.0 1 1 9
        0 2 0 3037 1.0 2 1 9
        0 3 0 3037 1.0 3 1 0
        0 3 2 3037 1.0 2 1 0
        3 2 1 3037 1.0 2 1 0
        8 1 0 3037 1.0 1 1 9
        """;
    const string Additional = """
        1 0 99 3033 2500 7.8
        1 0 99 3034 2500 7.8
        1 0 99 3035 2500 7.8
        1 0 99 3036 2500 7.8
        2 0 59 3166 10000 30
        2 60 99 3300 10000 14
        3 0 99 3167 4000 5.7
        """;
    const string BigWin = """
        1 0 99 3033 2500 0.29
        1 0 99 3034 2500 0.29
        1 0 99 3035 2500 0.29
        1 0 99 3036 2500 0.29
        """;

    public static void RunCore()
    {
        int checks = Count;
        var rules = new ExpertJobDisjointRules(Results, Additional, BigWin, 150, "9 5");
        That(rules.TryGet(0, 1, 0, out var row) && row == new ExpertJobDisjointResult(3037, 1, 1, 1, 9),
            "分解机八列完整保留两张奖励表及大成功概率");
        var template = new ItemTemplate(2000, ItemKind.Equipment, 12, 70, 100, 1, 0, 1, 60, "[trade]",
            ushort.MaxValue, 0, "synthetic") { DisjointValue = 150 };
        var core = InventoryRewardGrantService.Create(template);
        var normal = Calculate(template, core, 900, 2500);
        That(normal.SequenceEqual(new InventoryReward[] { new(3037, 1), new(3033, 7) }),
            "9%阈值不含900，附加权重包含2500边界，数量按grade而非使用等级");
        That(Calculate(template, core, 900, 2501)[1] == new InventoryReward(3034, 7),
            "附加权重跨边界后选择下一声明，不能只取第一行");
        That(Calculate(template, core, 899, 2501)[1] == new InventoryReward(3034, 206),
            "大成功使用独立奖励池与除数，不叠加普通附加奖励");
        That(ExpertJobCraft.DisjointProducts(9, rules, template, core, Draw(1399, 0), ownMachine: true)[1].Count == 206 &&
            ExpertJobCraft.DisjointProducts(9, rules, template, core, Draw(1399, 0), ownMachine: false)[1].Count == 7,
            "PVF按机器档位追加自用大成功概率，他人机器不能享用机主自用加成");
        That(Calculate(template with { Rarity = 2 }, core, 9999, 0)[1] == new InventoryReward(3300, 4),
            "同一表按包含两端的grade范围筛选，不使用角色或装备最低等级");
        That(Calculate(template with { Rarity = 3, BossDrop = true }, core, 0, 0)[1] == new InventoryReward(3300, 4) &&
            Calculate(template with { Rarity = 3 }, core with { Chronicle0 = new(1, 0, 0, 0, 0) }, 0, 0)[1].ItemId == 3167,
            "本版state2按boss drop领主神器分类，不能把任意异界属性当作领主来源");
        That(Calculate(template with { Grade = 100 }, core, 9999, 0).Length == 1 &&
            Calculate(template with { Rarity = 3 }, core, 9999, 9999).Length == 1,
            "奖励池没有等级匹配或权重未命中时不补造产物、不归一化");
        That(Calculate(template, core with { Durability = 0 }, 900, 2500).SequenceEqual(normal),
            "分解机基础数量不受NPC耐久出售折价影响");
        That(ExpertJobCraft.DisjointProducts(1, rules, template with { Rarity = 2 }, core with { AmplifyType = 0x80 }, Draw(0, 0)).Length == 0 &&
            ExpertJobCraft.DisjointProducts(4, rules, template with { Rarity = 2 }, core with { AmplifyType = 0x80 }, Draw(0, 0)).Length == 2,
            "污染装备要求对应机器档位及状态行，不回退普通状态");
        foreach (var invalid in new[]
        {
            Results + " 0", Results + "\n0 1 0 3037 1 1 1 9", Results.Replace("1.0 1 1 9", "1.0 99 1 9"),
            Results.Replace("1.0 1 1 9", "1.0 1 1 101"), Results.Replace("3037 1.0", "3037 1e-100")
        })
            Reject<InvalidDataException>(() => new ExpertJobDisjointRules(invalid, Additional, BigWin, 150),
                "截断、重复、未知池、概率越界和float下溢拒绝资源装配");
        Reject<InvalidDataException>(() => new ExpertJobDisjointRules(Results, Additional + " 1", BigWin, 150),
            "附加产物截断不静默忽略");
        Reject<InvalidDataException>(() => new ExpertJobDisjointRules(Results, Additional.Replace("2500 7.8", "2500 0"), BigWin, 150),
            "零数量除数不能产生非法奖励");
        Console.WriteLine($"PASS {Count - checks} 玩家分解机完整资源、抽样与数量断言");

        InventoryReward[] Calculate(ItemTemplate item, ItemCore input, params int[] values) =>
            ExpertJobCraft.DisjointProducts(1, rules, item, input, Draw(values));
    }

    static Func<int, int> Draw(params int[] values)
    {
        var queue = new Queue<int>(values);
        return maximum => queue.TryDequeue(out int value) && value >= 0 && value < maximum ? value :
            throw new InvalidOperationException("测试随机调用或取值越界");
    }
}
