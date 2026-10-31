using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Quests;

internal readonly record struct QuestExperienceParameter(int Base, int RawAdditionalPercent)
{
    // 04333E80..E8A只采用正值，原-1仍保留以便核对资源。
    public int NativeAdditionalPercent => Math.Max(0, RawAdditionalPercent);
}

internal readonly record struct QuestLevelPenalty(int MinimumDifference, int MaximumDifference, int Percent);

// 保留本版原始字符串键、直接表索引和完整惩罚声明，资源层不计算奖励。
internal sealed class QuestRewardParameterCatalog
{
    public const string ResourcePath = "n_quest/questparameter.etc";
    public ImmutableDictionary<string, int> Difficulty { get; }
    public ImmutableArray<QuestExperienceParameter> Experience { get; }
    public ImmutableArray<int> Gold { get; private init; } = [];
    public ImmutableArray<QuestField> Fields { get; private init; } = [];
    QuestRewardParameterCatalog(ImmutableDictionary<string, int> difficulty, ImmutableArray<QuestExperienceParameter> experience)
        => (Difficulty, Experience) = (difficulty, experience);

    public static QuestRewardParameterCatalog Load(PvfArchive archive) => Parse(archive.ReadTokens(ResourcePath), archive.ResolveString);

    internal static QuestRewardParameterCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var sections = new Dictionary<string, PvfToken[]>(StringComparer.Ordinal);
        string[] supported = ["[difficulty]", "[exp reward table]"];
        for (int at = 0; at < tokens.Count; at++)
        {
            if (tokens[at].Type != 3)
                continue;
            string tag = resolve(tokens[at].Value);
            if (!supported.Contains(tag))
                continue;
            int start = ++at;
            while (at < tokens.Count && tokens[at].Type != 3)
                at++;
            if (!sections.TryAdd(tag, tokens.Skip(start).Take(at - start).ToArray()))
                throw new InvalidDataException("重复任务奖励参数字段: " + tag);
            if (tag == "[difficulty]" && (at >= tokens.Count || resolve(tokens[at].Value) != "[/difficulty]"))
                throw new InvalidDataException("任务difficulty块未闭合");
            at--;
        }
        // Full03 041997BA将ASCII小写转为大写，两端查询同样匹配；保留资源原键，不给未知键补倍率。
        var difficulty = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.OrdinalIgnoreCase);
        var difficultyTokens = Required("[difficulty]");
        if (difficultyTokens.Length == 0 || difficultyTokens.Length % 2 != 0)
            throw new InvalidDataException("任务difficulty必须是完整字符串键与整数值");
        for (int at = 0; at < difficultyTokens.Length; at += 2)
        {
            if (difficultyTokens[at].Type != 6 || difficultyTokens[at + 1].Type != 0)
                throw new InvalidDataException("任务difficulty字段类型错误");
            string key = resolve(difficultyTokens[at].Value);
            if (key.Length == 0 || !difficulty.TryAdd(key, difficultyTokens[at + 1].Value))
                throw new InvalidDataException("任务difficulty键为空或重复");
        }
        int[] experienceValues = Integers("[exp reward table]");
        if (experienceValues.Length == 0 || experienceValues.Length % 2 != 0 || experienceValues.Length > 400)
            throw new InvalidDataException("任务exp表必须为最多200个完整整数对");
        var experience = ImmutableArray.CreateBuilder<QuestExperienceParameter>();
        for (int at = 0; at < experienceValues.Length; at += 2)
            experience.Add(new(experienceValues[at], experienceValues[at + 1]));
        var document = new PvfTokenDocument(tokens, resolve);
        return new(difficulty.ToImmutable(), experience.ToImmutable())
        {
            Fields = document.Sections.Select(Convert).ToImmutableArray(),
            Gold = document.Sections.FirstOrDefault(section => section.Name == "gold reward table")?.Values
                .Select(token => token.Type == 0 ? token.Value : throw new InvalidDataException("Gold表含非整数")).ToImmutableArray() ?? []
        };

        QuestField Convert(PvfTokenSection section) => new(section.Name,
            section.Values.Select(token => new QuestRewardToken(token.Type, token.Value, token.Type == 6 ? resolve(token.Value) : null)).ToImmutableArray(),
            section.Children.Select(Convert).ToImmutableArray());

        PvfToken[] Required(string tag) => sections.TryGetValue(tag, out var value) ? value :
            throw new InvalidDataException("缺少任务奖励参数字段: " + tag);
        int[] Integers(string tag)
        {
            var values = Required(tag);
            if (values.Any(token => token.Type != 0))
                throw new InvalidDataException("任务奖励参数包含非整数: " + tag);
            return values.Select(token => token.Value).ToArray();
        }
    }

    // 未声明难度使用缺省100；显式未知键不等同于未声明。
    public bool TryGetDifficultyWeight(string? key, out int weight)
    {
        if (key is null)
        {
            weight = 100;
            return true;
        }
        return Difficulty.TryGetValue(key, out weight) && weight >= 0;
    }

    // 04333680/6B0/E0真实getter直接使用index，不把资源第0项自动改名Lv1。
    public QuestExperienceParameter GetExperienceAtNativeIndex(int index) => index >= 0 && index < Experience.Length
        ? Experience[index] : throw new ArgumentOutOfRangeException(nameof(index), "任务EXP参数原生索引越界");

    public int BasicPenalty => Integers("basic level penalty", 1)[0];
    public QuestLevelPenalty Penalty(bool epic, bool grey)
    {
        var values = Integers((epic ? "epic " : "") + (grey ? "grey" : "green") + " level penalty", 3);
        if (values[0] < 0 || values[1] < values[0] || values[2] < 0)
            throw new InvalidDataException("任务等级惩罚三元组无效");
        return new(values[0], values[1], values[2]);
    }
    int[] Integers(string name, int count)
    {
        var values = Fields.SingleOrDefault(field => field.Name == name)?.Values ?? [];
        if (values.Length != count || values.Any(value => value.Type != 0))
            throw new NotSupportedException("任务参数缺少完整声明: " + name);
        return values.Select(value => value.Value).ToArray();
    }
}
