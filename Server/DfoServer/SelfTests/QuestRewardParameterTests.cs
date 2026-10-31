using DfoServer.GameWorld.Quests;
using PvfLib;

namespace DfoServer;

internal static class QuestRewardParameterTests
{
    public static void Run()
    {
        int checks = 0;
        var fixture = new Fixture();
        var tokens = fixture.Tokens();
        var catalog = QuestRewardParameterCatalog.Parse(tokens, fixture.Resolve);
        Check(catalog.Difficulty["1"] == 1780 && catalog.Difficulty["10"] == 708 && catalog.Difficulty["G"] == 30,
            "difficulty完整字符串键保留");
        Check(catalog.Difficulty["g"] == 30 && catalog.Difficulty.Keys.Contains("G", StringComparer.Ordinal) &&
            !catalog.Difficulty.Keys.Contains("g", StringComparer.Ordinal) && !catalog.Difficulty.ContainsKey("unknown"),
            "字母匹配忽略大小写，原始键保持且未知键不补倍率");
        Check(catalog.TryGetDifficultyWeight(null, out int defaultWeight) && defaultWeight == 100 &&
            catalog.TryGetDifficultyWeight("g", out int configuredWeight) && configuredWeight == 30,
            "未声明难度映射缺省100，显式难度沿用资源及大小写匹配规则");
        Check(!catalog.TryGetDifficultyWeight("", out _) && !catalog.TryGetDifficultyWeight("unknown", out _),
            "显式空键及未知键不伪装成缺省难度");
        var negative = fixture.Tokens();
        negative[6] = new(0, -1);
        Check(!QuestRewardParameterCatalog.Parse(negative, fixture.Resolve).TryGetDifficultyWeight("G", out _),
            "显式负难度倍率仍拒绝");
        Check(catalog.Experience.SequenceEqual(new QuestExperienceParameter[] { new(100, -1), new(126, 10), new(187, 0) }) &&
            catalog.Experience[0].NativeAdditionalPercent == 0 && catalog.Experience[1].NativeAdditionalPercent == 10,
            "EXP原始负值保留，仅正值作为追加倍率");
        Check(catalog.GetExperienceAtNativeIndex(1).Base == 126, "EXP直接索引不擅自按level-1换算");
        tokens[^6] = new(0, 999);
        Check(catalog.Experience[0].Base == 100, "资源token修改不改变冻结表");
        Reject(() => catalog.GetExperienceAtNativeIndex(-1), "负索引拒绝");
        Reject(() => catalog.GetExperienceAtNativeIndex(3), "越界不夹取末项");
        Reject(() => QuestRewardParameterCatalog.Parse(fixture.Tokens(exp: []), fixture.Resolve), "空EXP表拒绝");
        Reject(() => QuestRewardParameterCatalog.Parse(fixture.Tokens(exp: [100]), fixture.Resolve), "残缺EXP对拒绝");
        Reject(() => QuestRewardParameterCatalog.Parse(fixture.Tokens(exp: Enumerable.Repeat(1, 402).ToArray()), fixture.Resolve),
            "超过200行表容量不截断");
        var missing = fixture.Tokens()[..8];
        Reject(() => QuestRewardParameterCatalog.Parse(missing, fixture.Resolve), "缺少EXP表拒绝");
        var duplicate = fixture.Tokens(duplicateKey: true);
        Reject(() => QuestRewardParameterCatalog.Parse(duplicate, fixture.Resolve), "重复difficulty键拒绝");
        var invalid = fixture.Tokens();
        invalid[^1] = new(2, 0);
        Reject(() => QuestRewardParameterCatalog.Parse(invalid, fixture.Resolve), "EXP非整数拒绝");
        var unclosed = fixture.Tokens().ToList();
        unclosed.RemoveAt(7);
        Reject(() => QuestRewardParameterCatalog.Parse(unclosed, fixture.Resolve), "difficulty未闭合拒绝");
        Console.WriteLine($"PASS {checks} 任务经验参数解析与索引断言");

        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL 任务经验参数: " + name);
        }
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or ArgumentOutOfRangeException)
            {
                Check(true, name);
                return;
            }
            throw new InvalidOperationException("FAIL 任务经验参数未拒绝: " + name);
        }
    }

    public static void RunResources(PvfArchive archive)
    {
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(archive);
        if (catalog.Difficulty["1"] != 1780 || catalog.Difficulty["10"] != 708 || catalog.Difficulty["G"] != 30 ||
            catalog.Experience.Length != 200 ||
            !catalog.Experience.Take(3).SequenceEqual(new QuestExperienceParameter[] { new(100, -1), new(100, -1), new(126, -1) }) ||
            catalog.GetExperienceAtNativeIndex(2).Base != 126)
            throw new InvalidOperationException("FAIL 匹配任务EXP/difficulty参数声明发生变化");
        Console.WriteLine("PASS 任务经验参数真实资源完整表与索引断言");
    }

    sealed class Fixture
    {
        readonly Dictionary<int, string> strings = [];
        public string Resolve(int id) => strings[id];
        PvfToken String(string value, byte type)
        {
            int id = strings.Count;
            strings.Add(id, value);
            return new(type, id);
        }
        public PvfToken[] Tokens(int[]? exp = null, bool duplicateKey = false)
        {
            var tokens = new List<PvfToken> { String("[difficulty]", 3) };
            foreach (var row in new[] { ("1", 1780), ("10", 708), (duplicateKey ? "1" : "G", 30) })
            {
                tokens.Add(String(row.Item1, 6));
                tokens.Add(new(0, row.Item2));
            }
            tokens.Add(String("[/difficulty]", 3));
            tokens.Add(String("[exp reward table]", 3));
            tokens.AddRange((exp ?? [100, -1, 126, 10, 187, 0]).Select(value => new PvfToken(0, value)));
            return tokens.ToArray();
        }
    }
}
