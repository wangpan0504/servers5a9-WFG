using System.Collections.Immutable;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Pvp;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpMissionTests
{
    public static void RunCore()
    {
        PvpDailyMissionTests.RunCore();
        int before = Count;
        var definition = Read("normal", "arcade mode", "all", -1, 1)!;
        That(definition.Target == 1 && definition.Parameter == -1, "道场负难度参数不当作目标数");
        That(Read("daily", "hereafter join", "all", 1, -1, dailyExtension: true) is { Grade: 2, Group: 0 },
            "日常grade后额外参数保留独立分组");
        Reject<InvalidDataException>(() => Read("normal", "hereafter join", "all", -1, -1), "负目标拒绝");
        Reject<InvalidDataException>(() => Read("normal", "hereafter join", "unknown", 1, -1), "未知比赛范围不默认全部");
        var combo = Read("normal", "combo clear", "swordman", 1, 4)!;
        That(combo.Job == 0 && combo.Target == 1 && combo.Parameter == 4, "连招列表长度和难度不混作累计目标");
        var all = Read("normal", "hereafter join", "all", 100, -1)!;
        var total = Read("total match normal", "hereafter winning count", "total match mode", 100, -1)! with { Id = 824 };
        var rows = PvpMissionBook.Rows([all, total, combo with { Id = 578 }, definition with { Id = 604 }],
            0, 3, new(9, 4, 2, 1));
        That(rows.Select(row => row.Current).SequenceEqual([9, 1, 0, 0]),
            "普通与总力来源独立，连招及道场无完成事实时为零");
        var trained = new PvpMissionProgress(0, 0, 0, 0).Apply(new(0, 0, 4, Guid.NewGuid()))
            .Apply(new(1, 0, 2, Guid.NewGuid()));
        That(PvpMissionBook.Rows([combo, combo with { Id = 1, Parameter = 0 }, definition], 0, 0, trained)
            .Select(row => row.Current).SequenceEqual([1, 0, 1]), "只推进本职业实际连招索引，勇士道场同时满足普通难度");
        That(trained.Same(trained.Apply(new(0, 0, 4, Guid.NewGuid()))), "同连招再次完成不新增计数");
        Reject<InvalidDataException>(() => trained.Apply(new(0, 0, 12, Guid.NewGuid())), "越界索引不产生事实");
        Reject<InvalidDataException>(() => trained.Apply(new(1, 0, 2, Guid.Empty)), "空实例不能推进");
        var first = combo with { Id = 600 };
        var second = combo with { Id = 599, Parent = 600 };
        That(PvpMissionBook.Eligible([second, first], 0, 0, trained, []).Select(row => (int)row.Id).SequenceEqual([600, 599]),
            "前置按提交事实闭合，不依赖目录排列或跳过父任务");
        That(PvpMissionBook.Eligible([second, first], 0, 0, trained, [600, 599]).IsEmpty,
            "已领取链重放不重复规划奖励");
        That(PvpMissionBook.Rows([first with { Condition = "rank", Target = 5 }], 0, 1, trained, [600]).Single().Current == 5,
            "已领取目标保留完成状态，后来掉段不能把完成任务倒退");
        That(PvpMissionBook.Rows([combo], 10, 0, new(0, 0, 0, 0)).IsEmpty &&
            PvpMissionBook.Rows([combo with { MinimumRank = 4 }], 0, 3, new(0, 0, 0, 0)).IsEmpty,
            "职业和段位不适用的定义不发送");
        byte[] wire = PvpMissionPackets.List([new(499, 2, 100), new(604, 0, 1)]);
        Bytes(wire, [2, 243, 1, 2, 0, 0, 0, 100, 0, 0, 0, 92, 2, 0, 0, 0, 0, 1, 0, 0, 0],
            "323与578共用独立金样：count及十字节行，无扩展尾部");
        foreach (var invalid in new ImmutableArray<PvpMissionRow>[] { [new(1, 0, -1)], [new(32768, 0, 1)],
            [new(1, -1, 1)], [new(1, 0, 1), new(1, 0, 1)] })
            Reject<InvalidDataException>(() => PvpMissionPackets.List(invalid), "拒绝客户端有符号比较及重复身份陷阱");
        Console.WriteLine($"PASS {Count - before} 决斗任务书解析、投影及字节断言");
    }

    public static void RunResources(PvfArchive pvf)
    {
        int before = Count;
        var catalog = new PvpMissionCatalog(pvf);
        var definitions = catalog.Definitions;
        That(catalog.DailyDefinitions.Length == 16 && catalog.DailyDefinitions.Count(row => row.Grade == 2) == 12 &&
            catalog.DailyDefinitions.Count(row => row.Grade == 4) == 4 &&
            catalog.DailyDefinitions.Single(row => row.Id == 121).Jobs.SequenceEqual(new byte[] { 1, 7, 4 }) &&
            catalog.DailyDefinitions.Single(row => row.Id == 140) is { Target: 9, Parameter: 9 },
            "正式日常候选普通12/总力4，职业列表及总力9击杀不猜固定四token");
        That(definitions.Length == 154 && definitions.Count(row => row.Grade == 0) == 108 &&
            definitions.Count(row => row.Grade == 3) == 46, "正式索引普通108与总力46，日常及重复不混入任务树");
        That(definitions.Where(row => row.Parent != -1).All(row => definitions.Any(parent => parent.Id == row.Parent)),
            "前置定义完整，不通过缺父实例意外放开后续目标");
        foreach (byte job in new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13, 10, 14, 15 })
        {
            var rows = PvpMissionBook.Rows(definitions, job, 0, new(0, 0, 0, 0));
            That(rows.Length == (job is 10 or 14 or 15 ? 115 : 118), "每职业只收到适用的完整任务链");
            That(rows.Where(row => row.Id is >= 565 and <= 603).All(row =>
                definitions.Single(definition => definition.Id == row.Id).Job == job), "连招按资源职业筛选");
        }
        That(definitions.Where(row => row.Condition == "rank").All(row => row.Category == 4),
            "保留原生段位分类4，不擅自改作窗口枚举0至3");
        That(definitions.Single(row => row.Id == 565).Rewards.SequenceEqual([new PvpMissionReward(10092640, 30)]) &&
            definitions.Single(row => row.Id == 578).Rewards.SequenceEqual([new PvpMissionReward(10092640, 60)]) &&
            definitions.Single(row => row.Id == 591).Rewards.SequenceEqual([new PvpMissionReward(10092640, 100)]),
            "连招三档发真实胜利证明30/60/100，不发胜点");
        That(definitions.Single(row => row.Id == 604).Rewards.SequenceEqual([new PvpMissionReward(10093026, 1)]) &&
            definitions.Single(row => row.Id == 605).Rewards.SequenceEqual([new PvpMissionReward(10093082, 1)]),
            "道场普通及勇士目标使用各自袖珍罐资源");
        Console.WriteLine($"PASS {Count - before} 正式MSN任务链与职业过滤断言");
    }

    static PvpMissionDefinition? Read(string grade, string kind, string scope, int first, int second, bool dailyExtension = false)
    {
        string[] strings = ["[grade]", grade, "[normal category]", "training", "[rank range]", "[condition]", kind, scope, "[/condition]"];
        var tokens = new List<PvfToken> { new(3, 0), new(6, 1) };
        if (dailyExtension) tokens.Add(new(0, 0));
        tokens.AddRange([new(3, 2), new(6, 3), new(3, 4), new(0, 0), new(0, 30), new(3, 5),
            new(6, 6), new(6, 7), new(0, first), new(0, second), new(3, 8)]);
        return PvpMissionCatalog.Read(499, new(tokens, id => strings[id]), id => strings[id]);
    }
}
