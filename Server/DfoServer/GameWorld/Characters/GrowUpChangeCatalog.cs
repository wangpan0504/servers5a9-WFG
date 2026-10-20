using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

// Character/GrowUp.etc：技能窗50级前职业变更（CMD785）的等级区间与按已变更次数索引的金币表。
// 匹配客户端 028A9333 加载同一脚本；012A17E5..180D 以已变更次数索引金币表，超出表长取末项。
internal sealed class GrowUpChangeCatalog
{
    public const string Path = "character/growup.etc";

    GrowUpChangeCatalog(int minimumLevel, int maximumLevel, ImmutableArray<int> fees)
    {
        MinimumLevel = minimumLevel;
        MaximumLevel = maximumLevel;
        Fees = fees;
    }

    public int MinimumLevel { get; }
    public int MaximumLevel { get; }
    public ImmutableArray<int> Fees { get; }

    public int Fee(int changeCount)
    {
        if (changeCount < 0) throw new ArgumentOutOfRangeException(nameof(changeCount));
        return changeCount < Fees.Length ? Fees[changeCount] : Fees[^1];
    }

    public static GrowUpChangeCatalog Load(PvfArchive archive) => Parse(archive.ReadTokens(Path), archive.ResolveString);

    public static GrowUpChangeCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        int[]? levels = null, fees = null;
        int at = 0;
        while (at < tokens.Count)
        {
            string tag = Tag(tokens, resolve, at);
            switch (tag)
            {
                case "[grow up change lv]":
                    if (levels is not null) throw new InvalidDataException("职业变更等级区间重复");
                    levels = Ints(tokens, resolve, ref at, tag, "[/grow up change lv]");
                    break;
                case "[grow up change gold]":
                    if (fees is not null) throw new InvalidDataException("职业变更金币表重复");
                    fees = Ints(tokens, resolve, ref at, tag, "[/grow up change gold]");
                    break;
                default:
                    throw new InvalidDataException("职业变更脚本出现未识别字段 " + tag);
            }
        }
        if (levels is not [var min, var max] || min < 1 || max < min)
            throw new InvalidDataException("职业变更等级区间必须是两个递增正整数");
        if (fees is null || fees.Length == 0 || fees.Any(fee => fee < 0))
            throw new InvalidDataException("职业变更金币表缺失或含负数");
        return new(min, max, fees.ToImmutableArray());
    }

    static int[] Ints(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, ref int at, string open, string close)
    {
        at++;
        var values = new List<int>();
        while (at < tokens.Count && tokens[at].Type != 3)
        {
            if (tokens[at].Type != 0) throw new InvalidDataException("职业变更字段含非整数 " + open);
            values.Add(tokens[at].Value);
            at++;
        }
        if (at >= tokens.Count || Tag(tokens, resolve, at) != close)
            throw new InvalidDataException("职业变更字段未闭合 " + open);
        at++;
        return values.ToArray();
    }

    static string Tag(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, int at)
    {
        if (at >= tokens.Count || tokens[at].Type != 3) throw new InvalidDataException("职业变更字段不是标签");
        return resolve(tokens[at].Value).Trim().ToLowerInvariant();
    }
}
