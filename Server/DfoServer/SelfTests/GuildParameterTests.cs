using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Guilds;
using PvfLib;

namespace DfoServer;

/// <summary>
/// 公会参数目录：合成文本覆盖解析与边界拒绝，真实 PVF 覆盖 `etc/guild.etc` 与 `etc/guildreform.etc` 的实际数值。
/// </summary>
internal static class GuildParameterTests
{
    static readonly string[] Tiers = ["low class", "mid class", "high class", "guardian", "conqueror"];

    internal const string GuildText = """
        [guild create cost]
        300000
        [guild mail]
        50000
        [guild cargo size limit]
        56
        [/guild cargo size limit]
        [guild donate]
        1000
        [guild parameter end]
        """;

    public static void RunCore()
    {
        int before = Count;
        var catalog = new GuildParameterCatalog(GuildText, Curve(rows => rows));
        That(catalog.CreateCost == 300000 && catalog.CargoSizeLimit == 56 && catalog.TeleportCost == 7,
            "建会花费与公会仓库格数按 `etc/guild.etc` 段落读取");
        That(catalog.LevelExperience(0, 1) == 1001 && catalog.LevelExperience(4, 10) == 5010,
            "公会等级经验按档位与等级定位");
        That(catalog.LevelAtExperience(0, 1000) == 1 && catalog.LevelAtExperience(0, 1001) == 2 &&
            catalog.LevelAtExperience(0, 1009) == 10 && catalog.LevelAtExperience(0, long.MaxValue) == 10 &&
            catalog.LevelAtExperience(1, 1009) == 1,
            "累计经验在门槛精确升级，档内满级不越过守护石晋阶，新档使用独立累计门槛");
        Reject<ArgumentOutOfRangeException>(() => catalog.LevelExperience(5, 1), "档位越界拒绝而不是夹取");
        Reject<ArgumentOutOfRangeException>(() => catalog.LevelExperience(0, 11), "等级越界拒绝而不是夹取");

        Reject<InvalidDataException>(() => new GuildParameterCatalog("[guild create cost]\n0\n", Curve(rows => rows)),
            "建会花费缺失或非正数必须拒绝");
        foreach (string cost in new[] { "", "[guild teleport cost]\n0\n" })
            Reject<InvalidDataException>(() => new GuildParameterCatalog(GuildText,
                Curve(rows => rows).Replace("[guild teleport cost]\n7\n", cost)),
                "地图传送费用缺失或非正数不能退回免费传送");
        Reject<InvalidDataException>(() => new GuildParameterCatalog(GuildText, Curve(rows => rows.Skip(1).ToList())),
            "公会等级经验表缺行必须拒绝");
        Reject<InvalidDataException>(() => new GuildParameterCatalog(GuildText, Curve(rows => rows.Append(rows[0]).ToList())),
            "公会等级经验表重复行必须拒绝");
        Reject<InvalidDataException>(() => new GuildParameterCatalog(GuildText,
            "[guild level exp]\n`unknown` 1 1\n[/guild level exp]\n"), "未知档位名必须拒绝");
        Console.WriteLine($"PASS {Count - before} 公会参数解析与边界拒绝断言");
    }

    public static void Run(PvfArchive pvf)
    {
        int before = Count;
        var catalog = new GuildParameterCatalog(pvf);
        That(catalog.CreateCost == 300000, "真实资源建会花费为 30 万（etc/guild.etc）");
        That(catalog.CargoSizeLimit == 56, "真实资源公会仓库为 56 格（etc/guild.etc）");
        That(catalog.TeleportCost == 5, "真实公会地图传送费用为5枚贡献币，独立于公共传送次数");
        That(catalog.LevelExperience(0, 1) == 150000 && catalog.LevelExperience(4, 10) == 371740446,
            "真实资源公会等级经验曲线首尾与 etc/guildreform.etc 一致");
        Console.WriteLine($"PASS {Count - before} 公会参数真实资源断言");
    }

    /// <summary>合成参数：建会花费 300000、仓库 56、五档 ×10 级经验曲线（供其它测试复用，不依赖 PVF）。</summary>
    internal static GuildParameterCatalog Synthetic() => new(GuildText, Curve(rows => rows));

    static string Curve(Func<List<string>, List<string>> mutate)
    {
        var rows = new List<string>();
        for (int tier = 0; tier < Tiers.Length; tier++)
            for (int level = 1; level <= 10; level++)
                rows.Add($"`{Tiers[tier]}` {level} {1000 * (tier + 1) + level}");
        return "[guild teleport cost]\n7\n[guild level exp]\n" + string.Join(" ", mutate(rows)) + "\n[/guild level exp]\n";
    }
}
