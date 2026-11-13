using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer;

internal static class DungeonClearExperienceCatalogTests
{
    public static void Run()
    {
        int checks = Count;
        var catalog = Catalog();
        var dungeon = Dungeon();
        uint[] expectedDifficulty = [130, 200, 250, 300, 400];
        for (byte i = 0; i < expectedDifficulty.Length; i++)
        {
            var reward = catalog.Calculate(dungeon, i, 0);
            That(reward.BaseExperience == expectedDifficulty[i] && reward.RankExperience == 0 &&
                reward.TotalExperience == expectedDifficulty[i] && reward.RankBonusIndex == -1,
                "五难度直接使用PVF倍率，1.30不受float提升误差少一经验");
        }

        var level3 = catalog.Calculate(Dungeon(level: 3), 0, 0);
        That(level3.BaseExperience == 1300, "按副本基准等级取整数对首项，不使用第二项");
        That(catalog.Calculate(Dungeon(level: 2), 0, 0).BaseExperience == 131,
            "基础经验先完整乘倍率再向下取整");
        That(catalog.Calculate(Dungeon(weight: Floating(-0.5f)), 0, 99) == catalog.Calculate(dungeon, 0, 99),
            "缺失及负权重均采用中性1，来源及结果稳定");
        That(catalog.Calculate(Dungeon(weight: Floating(-float.Epsilon)), 0, 99) == catalog.Calculate(dungeon, 0, 99) &&
            catalog.Calculate(Dungeon(weight: Floating(-float.MaxValue)), 0, 99) == catalog.Calculate(dungeon, 0, 99),
            "有限负权重在数值转换前采用中性1，不被decimal范围改变");
        That(catalog.Calculate(Dungeon(weight: Integer(2)), 1, 0).BaseExperience == 400,
            "显式整数权重参与基础经验计算");
        var fractional = catalog.Calculate(Dungeon(level: 2, weight: Floating(0.5f)), 0, 90);
        That(fractional.BaseExperience == 65 && fractional.RankExperience == 9 && fractional.TotalExperience == 74,
            "基础与评分分开取整，评分以已取整基础经验为输入");

        uint[] scores = [0, 10, 20, 30, 49, 50, 59, 60, 79, 80, 89, 90, 98, 99, uint.MaxValue];
        int[] indices = [-1, -1, -1, -1, -1, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4];
        uint[] bonuses = [0, 0, 0, 0, 0, 6, 6, 13, 13, 15, 15, 19, 19, 26, 26];
        for (int i = 0; i < scores.Length; i++)
        {
            var reward = catalog.Calculate(dungeon, 0, scores[i]);
            That(reward.RankBonusIndex == indices[i] && reward.RankExperience == bonuses[i] &&
                reward.TotalExperience == 130 + bonuses[i], "评分各边界对应前五档，低档不误加、高档不丢失");
        }

        That(catalog.GradeForPoint(255) == 99 && catalog.GradeForPoint(99) == 99 && catalog.GradeForPoint(98) == 90,
            "评分档位使用首个满足的实际阈值，高档及临界点保留");
        That(catalog.GradeForPoint(80) == 80 && catalog.GradeForPoint(73) == 60 && catalog.GradeForPoint(49) == 30,
            "评分档位查询保留中间档及后三档，不等同经验倍率索引");
        That(catalog.GradeForPoint(30) == 30 && catalog.GradeForPoint(20) == 20 && catalog.GradeForPoint(10) == 10 &&
            catalog.GradeForPoint(9) == 0 && catalog.GradeForPoint(0) == 0,
            "低分阈值和未命中档位归零");

        var highest = catalog.Calculate(dungeon, 0, 99);
        That(highest.RuleId == DungeonClearExperienceCatalog.RuleId &&
            highest.ResourceSource == $"{DungeonClearExperienceCatalog.QuestParameterPath};" +
                $"{DungeonClearExperienceCatalog.ServerParameterPath};{DungeonClearExperienceCatalog.RankSystemPath};{dungeon.Path}",
            "奖励保存规则版本及四个资源来源，不依赖资源哈希");
        That(Catalog(rank: Enumerable.Repeat(Integer(0), 5).ToArray()).Calculate(dungeon, 0, 99).RankExperience == 0,
            "显式零评分倍率保留零奖励");

        Reject<NotSupportedException>(() => catalog.Calculate(Dungeon(level: null), 0, 0), "缺失基准等级拒绝");
        Reject<NotSupportedException>(() => catalog.Calculate(Dungeon(level: 0), 0, 0), "零基准等级拒绝");
        Reject<NotSupportedException>(() => catalog.Calculate(Dungeon(level: 4), 0, 0), "表外基准等级拒绝");
        Reject<NotSupportedException>(() => catalog.Calculate(dungeon, 5, 0), "未知难度不夹取");
        foreach (var zero in new[] { Integer(0), Floating(0), Floating(-0.0f) })
        {
            var reward = catalog.Calculate(Dungeon(weight: zero), 4, 99);
            That(reward.BaseExperience == 0 && reward.RankExperience == 0 && reward.TotalExperience == 0,
                "显式零通关经验权重保留零基础与评分奖励，不中断正常结算");
        }
        Reject<InvalidDataException>(() => catalog.Calculate(Dungeon(weight: Floating(float.Epsilon)), 0, 0),
            "正权重转换下溢不冒充显式零声明");
        Reject<InvalidDataException>(() => catalog.Calculate(Dungeon(weight: Floating(float.NaN)), 0, 0), "NaN权重拒绝");
        Reject<InvalidDataException>(() => catalog.Calculate(Dungeon(weight: Floating(float.NegativeInfinity)), 0, 0), "负无穷不能降为中性权重");
        Reject<InvalidDataException>(() => catalog.Calculate(Dungeon(weight: new PvfToken(6, 0)), 0, 0), "字符串权重拒绝");
        Reject<InvalidDataException>(() => catalog.Calculate(dungeon with
        {
            RawSections = [new("experience increasing point", [Integer(1), Integer(2)])]
        }, 0, 0), "多值权重拒绝");
        Reject<InvalidDataException>(() => catalog.Calculate(dungeon with
        {
            RawSections = [new("experience increasing point", [Integer(1)]), new("experience increasing point", [Integer(2)])]
        }, 0, 0), "重复权重拒绝");
        Reject<InvalidDataException>(() => catalog.Calculate(Dungeon(weight: Floating(0.001f)), 0, 0), "取整为零的奖励拒绝");

        var overflow = Catalog(quest: [Integer(int.MaxValue), Integer(-1)]);
        Reject<InvalidDataException>(() => overflow.Calculate(dungeon, 4, 0), "基础奖励超u32拒绝");
        Reject<InvalidDataException>(() => overflow.Calculate(dungeon, 1, 99), "分项合法但合计超u32拒绝");
        Reject<InvalidDataException>(() => catalog.Calculate(Dungeon(weight: Floating(float.MaxValue)), 0, 0), "计算字段溢出拒绝");
        var huge = Catalog(difficulty: Enumerable.Repeat(Floating(1e20f), 5).ToArray());
        Reject<InvalidDataException>(() => huge.Calculate(Dungeon(weight: Floating(1e20f)), 0, 0), "中间decimal乘积溢出拒绝");

        Reject<InvalidDataException>(() => Catalog(quest: []), "空经验表拒绝");
        Reject<InvalidDataException>(() => Catalog(quest: [Integer(1)]), "残缺整数对拒绝");
        Reject<InvalidDataException>(() => Catalog(quest: [Integer(0), Integer(-1)]), "零基础经验拒绝");
        Reject<InvalidDataException>(() => Catalog(quest: [Integer(-1), Integer(1)]), "负基础经验拒绝");
        Reject<InvalidDataException>(() => Catalog(quest: [Integer(1), Floating(1)]), "整数对第二项类型同样检查");
        Reject<InvalidDataException>(() => Catalog(difficulty: [Integer(1)]), "难度倍率宽度拒绝");
        Reject<InvalidDataException>(() => Catalog(difficulty: [Integer(1), Integer(1), Integer(1), Integer(1), Integer(0)]), "零难度倍率拒绝");
        Reject<InvalidDataException>(() => Catalog(rank: [Integer(1), Integer(1), Integer(1), Integer(1), Integer(-1)]), "负评分倍率拒绝");
        Reject<InvalidDataException>(() => Catalog(rank: [Integer(1), Integer(1), Integer(1), Integer(1), Floating(float.PositiveInfinity)]), "非有限倍率拒绝");
        Reject<InvalidDataException>(() => Catalog(grades: [Integer(99), Integer(90)]), "评分表宽度拒绝");
        Reject<InvalidDataException>(() => Catalog(grades: [Integer(99), Integer(99), Integer(80), Integer(60), Integer(50), Integer(30), Integer(20), Integer(10)]), "重复阈值拒绝");
        Reject<InvalidDataException>(() => Catalog(grades: [Integer(256), Integer(90), Integer(80), Integer(60), Integer(50), Integer(30), Integer(20), Integer(10)]), "表外评分阈值拒绝");
        Reject<InvalidDataException>(() => DungeonClearExperienceCatalog.Parse([], Server(), Grades(), Resolve), "缺失声明拒绝");
        Reject<InvalidDataException>(() => DungeonClearExperienceCatalog.Parse(
            [new(3, 0), Integer(1), Integer(-1), new(3, 0), Integer(2), Integer(-1)], Server(), Grades(), Resolve), "重复声明拒绝");
        Console.WriteLine($"PASS {Count - checks} 通关经验资源、评分与边界断言");

    }

    private static DungeonClearExperienceCatalog Catalog(PvfToken[]? quest = null, PvfToken[]? difficulty = null,
        PvfToken[]? rank = null, PvfToken[]? grades = null) => DungeonClearExperienceCatalog.Parse(
        [new(3, 0), .. quest ?? [Integer(100), Integer(-1), Integer(101), Integer(5000), Integer(1000), Integer(-1)]],
        Server(difficulty, rank), grades is null ? Grades() : [new(3, 3), .. grades], Resolve);

    private static PvfToken[] Server(PvfToken[]? difficulty = null, PvfToken[]? rank = null) =>
    [
        new(3, 1), .. difficulty ?? [Floating(1.3f), Integer(2), Floating(2.5f), Integer(3), Integer(4)],
        new(3, 2), .. rank ?? [Floating(0.05f), Floating(0.1f), Floating(0.12f), Floating(0.15f), Floating(0.2f)],
        // 该全局倍率不属于参考公式，不能因名字包含clear就自动相乘。
        new(3, 4), Integer(99)
    ];

    private static PvfToken[] Grades() =>
        [new(3, 3), Integer(99), Integer(90), Integer(80), Integer(60), Integer(50), Integer(30), Integer(20), Integer(10)];

    private static string Resolve(int value) => value switch
    {
        0 => "[exp reward table]", 1 => "[dungeon difficulty exp bonusrate]",
        2 => "[clear rank exp bonusrate]", 3 => "[rank grade]", 4 => "[clear exp bonusrate]",
        _ => throw new InvalidOperationException("合成标签不存在")
    };

    private static DungeonDefinition Dungeon(int? level = 1, PvfToken? weight = null) =>
        new(3, "dungeon/test.dgn", 1, level, [], weight is PvfToken token ?
            [new("experience increasing point", [token])] : []);

    private static PvfToken Integer(int value) => new(0, value);
    private static PvfToken Floating(float value) => new(2, BitConverter.SingleToInt32Bits(value));
}
