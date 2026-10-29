using DfoServer.GameWorld.Guilds;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildNpcBenefitCatalogTests
{
    static readonly object[] Guardian = [1003, 400001060, 1, "common", "guild various buff", "none", 0, "none", 0, 0, 2, 1276, 120];
    static readonly object[] Upgrade = [3001, 400001062, 1, "common", "equip upgrade buff", "public funds", 30000, "none", 0, 180, 2, 1, 10];
    static readonly object[] Training = [5001, 400001064, 1, "individual", "guild trainer attack raid buff", "member coins", 200, "gold", 300000, 0, 3, 1281, 1, 25];

    public static void RunCore()
    {
        int before = Count;
        var catalog = Parse([Guardian, Upgrade, Training]);
        That(catalog.Definitions.Count == 3 && catalog.TrainingIntervalSeconds == 180 &&
            catalog.GuardianBuff(1) == 1276 && catalog.GuardianBuff(0) == 0 && catalog.GuardianBuff(2) == 0 &&
            catalog.Find(9999) is null,
            "福利目录使用精确索引和守护石等级，未知等级不借用其他档位");
        That(catalog.Find(1003) is { DurationSeconds: 0, Parameters: [1276, 120] } &&
            catalog.Find(3001) is { Common: true, FirstAmount: 30000, DurationSeconds: 10800, Parameters: [1, 10] } &&
            catalog.Find(5001) is { Common: false, FirstAmount: 200, SecondCurrency: "gold", SecondAmount: 300000,
                DurationSeconds: 0, Parameters: [1281, 1, 25] },
            "生产福利分钟转秒，永久属性值和零期限训练解锁参数原样保留");
        That(Parse([Changed(Upgrade, 4, "future benefit")]).Find(3001)?.Effect == "future benefit",
            "未知福利类型保留资源声明，不擅自转换为已有业务");
        Reject<InvalidDataException>(() => Parse([Guardian, Guardian]), "重复福利ID拒绝");
        Reject<InvalidDataException>(() => Parse([Guardian, Changed(Guardian, 0, 1008)]), "同等级守护石属性声明冲突拒绝");
        Reject<InvalidDataException>(() => Parse([Upgrade[..10]]), "不足十一列的福利行拒绝");
        Reject<InvalidDataException>(() => Parse([Upgrade[..^1]]), "变长参数截断拒绝");
        Reject<InvalidDataException>(() => Parse([Upgrade], close: false), "福利段缺少闭合拒绝");
        Reject<InvalidDataException>(() => Parse([], interval: 180), "空福利名册拒绝");
        Reject<InvalidDataException>(() => Parse([Upgrade], interval: 0), "训练发动间隔不能为零");
        foreach (var change in new (int Column, object Value)[]
        {
            (0, 0), (1, 0), (2, 6), (3, "shared"), (4, ""), (5, "guild crystal"),
            (6, -1), (8, 1), (9, -1), (9, int.MaxValue / 60 + 1), (10, -1), (10, 33), (11, "1")
        })
        {
            object[] row = Changed(Upgrade, change.Column, change.Value);
            Reject<InvalidDataException>(() => Parse([row]), "福利索引、类型、费用、期限及参数边界拒绝");
        }
        foreach (var change in new (int Column, object Value)[]
        {
            (1, 400001061), (3, "individual"), (5, "public funds"), (9, 1), (10, 1), (11, 1281), (12, 0)
        })
        {
            object[] row = Changed(Guardian, change.Column, change.Value);
            Reject<InvalidDataException>(() => Parse([row]), "守护石永久属性须匹配公有免费期限及真实效果声明");
        }
        Console.WriteLine($"PASS {Count - before} guild NPC benefit catalog assertions");
    }

    public static void RunResources(PvfArchive pvf)
    {
        var production = new GuildNpcBenefitCatalog(pvf).Definitions.Where(row => row.Effect == "crafting item").OrderBy(row => row.Id).ToArray();
        That(production.Length == 5 && production.All(row => !row.Common && row.NpcId == 400001065 &&
            row.DurationSeconds == 86400 && row.FirstCurrency == "member coins" && row.SecondCurrency == "gold" &&
            row.Parameters is [> 0, 30]) && production.Select(row => row.Parameters[0]).SequenceEqual(
                new[] { 490013242, 490013243, 490013244, 490013245, 490013246 }) &&
            production.Select(row => row.Level).SequenceEqual(new[] { 1, 1, 2, 3, 4 }),
            "正式五种炼金配方各30份、24小时、NPC等级与双币费用从资源读取");
        int before = Count;
        var catalog = new GuildNpcBenefitCatalog(pvf);
        That(catalog.Definitions.Count == 92 && catalog.TrainingIntervalSeconds == 180 &&
            Enumerable.Range(1, 5).All(level => catalog.GuardianBuff(level) == 1275u + (uint)level) &&
            catalog.Find(1023) is { DurationSeconds: 0, Parameters: [1280, 120] },
            "正式福利名册完整加载，五级守护石属性引用永久guild reform效果而非秒数");
        That(catalog.Find(3001) is { Common: true, Effect: "equip upgrade buff", FirstCurrency: "public funds",
                FirstAmount: 30000, DurationSeconds: 10800, Parameters: [1, 10] } &&
            catalog.Find(3002) is { Common: false, FirstCurrency: "member coins", FirstAmount: 200, DurationSeconds: 1800 } &&
            catalog.Find(4001) is { Effect: "equip upgrade separate buff", Parameters: [1, 5] } &&
            catalog.Find(6001) is { Effect: "emblem compound buff", FirstAmount: 60000, Parameters: [1, 5] } &&
            catalog.Find(8001) is { Effect: "equip amplify upgrade buff", Parameters: [2, 7] },
            "强化、锻造、徽章、增幅各保留公共个人费用、时间与原始概率参数");
        That(Enumerable.Range(5001, 16).All(id => catalog.Find((uint)id) is { Common: false, DurationSeconds: 0,
                FirstCurrency: "member coins", SecondCurrency: "gold", Parameters: [1281, > 0, > 0] }) &&
            catalog.Find(5004) is { Level: 4, FirstAmount: 1000, SecondAmount: 1500000, Parameters: [1281, 4, 50] } &&
            catalog.Find(5016) is { Level: 4, Parameters: [1281, 4, 80] },
            "训练官十六项能力保留独立解锁费用及属性参数，不把零期限当三分钟购买");
        Console.WriteLine($"PASS {Count - before} guild NPC benefit resource assertions");
    }

    static object[] Changed(object[] source, int index, object value)
    {
        object[] copy = source.ToArray(); copy[index] = value; return copy;
    }

    static GuildNpcBenefitCatalog Parse(object[][] rows, int interval = 180, bool close = true)
    {
        var strings = new List<string>(); var tokens = new List<PvfToken>();
        void Text(byte type, string text) { strings.Add(text); tokens.Add(new(type, strings.Count - 1)); }
        Text(3, "[guild trainer buff interval time]"); tokens.Add(new(0, interval));
        Text(3, "[npc benefit info]");
        foreach (object value in rows.SelectMany(row => row))
        {
            if (value is int number) tokens.Add(new(0, number));
            else Text(7, (string)value);
        }
        if (close) Text(3, "[/npc benefit info]");
        return new(tokens, index => strings[index], new HashSet<int> { 1276 });
    }
}
