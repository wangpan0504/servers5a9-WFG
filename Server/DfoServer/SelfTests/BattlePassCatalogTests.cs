using DfoServer.GameWorld.BattlePass;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassCatalogTests
{
    static List<object> Source() =>
    [
        "[ui path]", "UI/Test/BattlePass.xui",
        "[mission]", 10, 2_000_000_000, 77, 45, "保留大目标及原条件参数", -1, "[/mission]",
        "[mission]", 3, 2, 88, 14, "测试第二个定义", 10158964, "[/mission]",
        "[daily mission]", 10, 3, 10, "[/daily mission]",
        "[weekly mission]", 3, 10, 3, "[/weekly mission]",
        "[season mission]", 10, 3, "[/season mission]",
        "[level reward]", 1, 1001, 1, 17, "[/level reward]",
        "[level reward]", 2, 2001, 2, 0, 2002, 3, 1, 2001, 4, 17, "[/level reward]",
        "[level reward]", 3, 3001, 1, 1, "[/level reward]",
        "[postal]", 7, 8, 9, 10, "[/postal]",
        "[exp table]", 1, 10, 2, 30, 3, 0, "[/exp table]",
        "[tier table]", 1, 2, 3, 3, "[/tier table]",
        "[certificate index]", 7001, "[/certificate index]",
        "[package index]", 7002, "[/package index]",
        "[buy restrict code]", 900, "[/buy restrict code]",
        "[golden mission exp ratio]", 3,
        "[cera consumption exp percentage]", 50
    ];

    static BattlePassCatalog Parse(List<object> source)
    {
        var strings = new List<string>();
        int Add(string value) { strings.Add(value); return strings.Count - 1; }
        PvfToken Token(object value) => value switch
        {
            int integer => new(0, integer),
            float number => new(2, BitConverter.SingleToInt32Bits(number)),
            string text => new(text.StartsWith('[') ? (byte)3 : (byte)6, Add(text)),
            _ => throw new InvalidDataException("unsupported synthetic token")
        };
        return BattlePassCatalog.Parse(source.Select(Token).ToArray(), index => strings[index]);
    }

    static List<object> Replace(string name, params object[] values)
    {
        var source = Source();
        int start = source.FindIndex(value => value is string text && text == "[" + name + "]");
        int end = source.FindIndex(start + 1, value => value is string text && text == "[/" + name + "]");
        if (end < 0) end = source.FindIndex(start + 1, value => value is string text && text.StartsWith('['));
        if (end < 0) end = source.Count;
        source.RemoveRange(start + 1, end - start - 1);
        source.InsertRange(start + 1, values);
        return source;
    }

    public static void RunCore()
    {
        int before = Count;
        var catalog = Parse(Source());
        That(catalog.Missions.Length == 2 && catalog.Missions[0].Id == 10 && catalog.Missions[1].Id == 3,
            "战令目录保留任务定义声明原序，不排序或以空目录代替资源");
        That(catalog.MissionsById[10] == catalog.Missions[0] && catalog.MissionsById[3] == catalog.Missions[1] &&
            catalog.Missions[0].RequiredCount == 2_000_000_000 && catalog.Missions[0].Experience == 77 &&
            catalog.Missions[0].RawTypeCode == 45 && catalog.Missions[0].RawParameter == -1 &&
            catalog.Missions[1].Text == "测试第二个定义" && catalog.Missions[1].RawParameter == 10158964,
            "战令任务保留大目标、原始条件码、文案及带符号参数");
        That(catalog.DailyMissionIds.SequenceEqual(new[] { 10, 3, 10 }) &&
            catalog.WeeklyMissionIds.SequenceEqual(new[] { 3, 10, 3 }) &&
            catalog.SeasonMissionIds.SequenceEqual(new[] { 10, 3 }),
            "战令调度保留声明原序及重复，不臆造随机任务或分组语义");
        That(catalog.UiPath == "UI/Test/BattlePass.xui" && catalog.CertificateIndex == 7001 && catalog.PackageIndex == 7002 &&
            catalog.BuyRestrictCode == 900 && catalog.RawGoldenMissionExperienceRatio == 3 &&
            catalog.RawCeraConsumptionExperiencePercentage == 50 && catalog.Postal.SequenceEqual(new[] { 7, 8, 9, 10 }),
            "战令路径、商品索引、限制码、原比例与邮寄字段从定义读，不硬写当前ID");
        That(catalog.LevelRewards.Select(row => row.Level).SequenceEqual(new[] { 1, 2, 3 }) &&
            catalog.RewardsByLevel[2].Rewards.SequenceEqual(new[]
            {
                new BattlePassReward(2001, 2, 0), new BattlePassReward(2002, 3, 1), new BattlePassReward(2001, 4, 17)
            }) && catalog.LevelRewards[0].Rewards[0].RawTrackCode == 17,
            "战令每级完整多奖励及原轨道码保留，未知轨道码不擅自映射或丢弃");
        That(catalog.MaximumLevel == 3 && catalog.ExperienceTable.Select(row => row.Level).SequenceEqual(new[] { 1, 2, 3 }) &&
            catalog.Requirement(1) == 10 && catalog.Requirement(2) == 30 && catalog.Requirement(3) == 0 &&
            catalog.TotalExperienceToReach(1) == 0 && catalog.TotalExperienceToReach(2) == 10 && catalog.TotalExperienceToReach(3) == 40,
            "战令升级需求取资源当前级到下一级，动态曲线不硬写80或1300");
        That(catalog.LevelAtTotalExperience(0) == 1 && catalog.LevelAtTotalExperience(9) == 1 &&
            catalog.LevelAtTotalExperience(10) == 2 && catalog.LevelAtTotalExperience(39) == 2 &&
            catalog.LevelAtTotalExperience(40) == 3 && catalog.LevelAtTotalExperience(ulong.MaxValue) == 3 &&
            catalog.Tiers.SequenceEqual(new[] { new BattlePassTier(1, 2), new BattlePassTier(3, 3) }),
            "战令纯累计等级查询覆盖临界与终级，不决定满级溢出发放策略");
        Reject<ArgumentOutOfRangeException>(() => catalog.Requirement(0), "战令需求拒绝0级");
        Reject<ArgumentOutOfRangeException>(() => catalog.Requirement(4), "战令需求拒绝超出目录等级");
        Reject<ArgumentOutOfRangeException>(() => catalog.TotalExperienceToReach(0), "战令累计阈值拒绝0级");
        Reject<ArgumentOutOfRangeException>(() => catalog.TotalExperienceToReach(4), "战令累计阈值拒绝越界等级");

        Reject<InvalidDataException>(() => Parse(Replace("mission", 10, 1, 2, 3, "short")), "任务行缺原参数必须拒绝");
        var duplicateMission = Source();
        duplicateMission.InsertRange(duplicateMission.IndexOf("[daily mission]"), new object[] { "[mission]", 10, 1, 1, 1, "duplicate", -1, "[/mission]" });
        Reject<InvalidDataException>(() => Parse(duplicateMission), "任务ID重复不能最后写入覆盖");
        Reject<InvalidDataException>(() => Parse(Replace("daily mission", 999)), "调度不能引用缺失任务");
        Reject<InvalidDataException>(() => Parse(Replace("weekly mission", 3, 999)), "周调度引用完整核验");
        Reject<InvalidDataException>(() => Parse(Replace("season mission", 999)), "赛季调度引用完整核验");
        Reject<InvalidDataException>(() => Parse(Replace("exp table", 1, 10, 3, 0)), "升级表等级不可缺口");
        Reject<InvalidDataException>(() => Parse(Replace("exp table", 1, 10, 2)), "升级表必须等级经验成对");
        Reject<InvalidDataException>(() => Parse(Replace("exp table", 1, 0, 2, 30, 3, 0)), "升级表不能中间零经验");
        Reject<InvalidDataException>(() => Parse(Replace("exp table", 1, 10, 2, 30, 3, 1)), "终级需求必须为零");
        Reject<InvalidDataException>(() => Parse(Replace("exp table", 1, -10, 2, 30, 3, 0)), "升级表不能负经验");
        Reject<InvalidDataException>(() => Parse(Replace("exp table", 1, 10f, 2, 30, 3, 0)), "整数字段不能吞浮点token");
        Reject<InvalidDataException>(() => Parse(Replace("level reward", 1, 1001, 1)), "奖励必须完整三元组");
        Reject<InvalidDataException>(() => Parse(Replace("level reward", 1, 1001, 0, 0)), "奖励数量必须正数");
        Reject<InvalidDataException>(() => Parse(Replace("tier table", 1, 2, 3)), "页阶表必须成对");
        Reject<InvalidDataException>(() => Parse(Replace("certificate index", 7001, 7002)), "唯一标量不可吞第二个值");
        var duplicateScalar = Source(); duplicateScalar.AddRange(new object[] { "[package index]", 7003, "[/package index]" });
        Reject<InvalidDataException>(() => Parse(duplicateScalar), "唯一标量重复必须拒绝");
        var missing = Source(); int start = missing.IndexOf("[postal]"); int end = missing.IndexOf("[/postal]");
        missing.RemoveRange(start, end - start + 1);
        Reject<InvalidDataException>(() => Parse(missing), "必需资源段缺失不能回落空定义");
        Console.WriteLine($"PASS BattlePassCatalogTests core ({Count - before} checks)");
    }

    // 完整资源自测借用本轮归档，通过正式入口读取真实战令定义。
    public static void Run(PvfArchive pvf) => Verify(BattlePassCatalog.Load(pvf));

    // 外置探针注入已保存的token与字符串，不在仓库自测中搬入资源副本。
    public static void VerifyCaptured(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve) =>
        Verify(BattlePassCatalog.Parse(tokens, resolve));

    static void Verify(BattlePassCatalog catalog)
    {
        int before = Count;
        That(catalog.UiPath == "UI/Event/chn_event_2019/191210_BattlePass/BattlePassWindow.xui" &&
            catalog.Missions.Length == 70 && catalog.MissionsById.Count == 70 &&
            catalog.Missions.Select(row => row.Id).SequenceEqual(Enumerable.Range(1, 70)), "捕获战令完整70任务与原生路径");
        That(catalog.MissionsById[52].RequiredCount == 2_000_000_000 && catalog.MissionsById[52].Experience == 1600 &&
            catalog.MissionsById[52].RawTypeCode == 14 && catalog.MissionsById[52].RawParameter == -1 &&
            catalog.MissionsById[5].RawParameter == 190 && catalog.MissionsById[31].RawParameter == 10158964,
            "捕获战令保留真实大目标和原条件参数");
        That(catalog.DailyMissionIds.Length == 400 && catalog.DailyMissionIds.Take(8).SequenceEqual(new[] { 10, 6, 18, 2, 16, 1, 20, 17 }) &&
            catalog.DailyMissionIds.TakeLast(8).SequenceEqual(new[] { 6, 5, 24, 4, 6, 1, 17, 21 }), "捕获每日调度400项首尾原序不重排");
        That(catalog.WeeklyMissionIds.Length == 60 && catalog.WeeklyMissionIds.Take(8).SequenceEqual(new[] { 33, 31, 35, 30, 42, 34, 49, 31 }) &&
            catalog.WeeklyMissionIds.TakeLast(8).SequenceEqual(new[] { 34, 43, 35, 45, 28, 44, 33, 36 }), "捕获每周调度60项首尾原序不重排");
        That(catalog.SeasonMissionIds.SequenceEqual(new[] { 51, 53, 54, 55, 56, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70 }) &&
            catalog.MissionsById.ContainsKey(57) && !catalog.SeasonMissionIds.Contains(52) && !catalog.SeasonMissionIds.Contains(57),
            "捕获赛季18任务保留实际调度，不能把全部高ID定义擅自加入赛季");
        That(catalog.LevelRewards.Length == 80 && catalog.RewardsByLevel.Count == 80 &&
            catalog.LevelRewards.Sum(row => row.Rewards.Length) == 162 &&
            catalog.LevelRewards.Select(row => row.Level).SequenceEqual(Enumerable.Range(1, 80)), "捕获80级完整奖励段");
        That(catalog.RewardsByLevel[4].Rewards.SequenceEqual(new[]
            { new BattlePassReward(15, 1, 0), new BattlePassReward(490705122, 1, 1), new BattlePassReward(490705101, 3, 1) }) &&
            catalog.RewardsByLevel[8].Rewards.SequenceEqual(new[]
            { new BattlePassReward(490705107, 2, 0), new BattlePassReward(490705112, 1, 0), new BattlePassReward(490705108, 2, 1), new BattlePassReward(490705117, 1, 1), new BattlePassReward(490705179, 1, 1) }),
            "捕获等级4三奖和等级8五奖逐项保留原轨道码");
        That(catalog.RewardsByLevel[80].Rewards.SequenceEqual(new[]
            { new BattlePassReward(490705130, 1, 0), new BattlePassReward(490705163, 1, 0), new BattlePassReward(490705127, 1, 0),
              new BattlePassReward(490705131, 1, 1), new BattlePassReward(490705132, 1, 1), new BattlePassReward(490705111, 1, 1), new BattlePassReward(490705134, 1, 1) }),
            "捕获80级七奖不能截成每轨单物品");
        That(catalog.MaximumLevel == 80 && catalog.ExperienceTable.Length == 80 &&
            Enumerable.Range(1, 79).All(level => catalog.Requirement(level) == 1300) && catalog.Requirement(80) == 0 &&
            catalog.TotalExperienceToReach(40) == 50700 && catalog.TotalExperienceToReach(80) == 102700,
            "捕获1到79需求1300及80终行0，Lv40与Lv80分别39/79次升级");
        That(catalog.LevelAtTotalExperience(1299) == 1 && catalog.LevelAtTotalExperience(1300) == 2 &&
            catalog.LevelAtTotalExperience(50699) == 39 && catalog.LevelAtTotalExperience(50700) == 40 &&
            catalog.LevelAtTotalExperience(102699) == 79 && catalog.LevelAtTotalExperience(102700) == 80,
            "捕获经验曲线边界不按80次升级计算");
        That(catalog.Tiers.SequenceEqual(new[] { new BattlePassTier(1, 8), new BattlePassTier(9, 16), new BattlePassTier(17, 24),
            new BattlePassTier(25, 32), new BattlePassTier(33, 40), new BattlePassTier(41, 48), new BattlePassTier(49, 56),
            new BattlePassTier(57, 64), new BattlePassTier(65, 72), new BattlePassTier(73, 80) }) &&
            catalog.CertificateIndex == 490705043 && catalog.PackageIndex == 490705044 && catalog.BuyRestrictCode == 70289 &&
            catalog.RawGoldenMissionExperienceRatio == 3 && catalog.RawCeraConsumptionExperiencePercentage == 50 &&
            catalog.Postal.SequenceEqual(new[] { 1823, 1824, 1825, 1826 }), "捕获十页区间、资格物品索引及原比例字段");
        Console.WriteLine($"PASS BattlePassCatalogTests resources ({Count - before} checks)");
    }
}
