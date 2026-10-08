using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Quests;

internal readonly record struct QuestExperienceParameter(int Base, int RawAdditionalPercent)
{
    // 04333E80..E8A只采用正值，原-1仍保留以便核对资源。
    public int NativeAdditionalPercent => Math.Max(0, RawAdditionalPercent);
}

// 读取3145本服经验规则所需的EXP/difficulty参数，不计算或授予通用金币奖励。
internal sealed class QuestRewardParameterCatalog
{
    public const string ResourcePath = "n_quest/questparameter.etc";
    public ImmutableDictionary<string, int> Difficulty { get; }
    public ImmutableArray<QuestExperienceParameter> Experience { get; }
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
        var difficulty = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
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
        return new(difficulty.ToImmutable(), experience.ToImmutable());

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

    // 04333680/6B0/E0真实getter直接使用index，不把资源第0项自动改名Lv1。
    public QuestExperienceParameter GetExperienceAtNativeIndex(int index) => index >= 0 && index < Experience.Length
        ? Experience[index] : throw new ArgumentOutOfRangeException(nameof(index), "任务EXP参数原生索引越界");
}
