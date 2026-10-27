using System.Collections.Immutable;

namespace PvfLib;

// 保留魔界大战原始整数行；不在格式层解释队伍、费用或战役状态。
public sealed record EvildomWarFile(ImmutableArray<int> Dungeons, ImmutableArray<int> PartyMembers,
    ImmutableArray<int> WaitingArea, int LimitTime, int IncountDungeon, int DailyLimit, int WeeklyLimit, int NeedFatigue)
{
    public static EvildomWarFile Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("魔界大战资源块未闭合");
        ImmutableArray<int> Values(string name)
        {
            var rows = document.Sections.Where(row => row.Name == name).ToArray();
            if (rows is not [var row] || !row.Children.IsEmpty || row.Values.Any(token => token.Type != 0))
                throw new InvalidDataException("魔界大战字段缺失、重复或包含非整数: " + name);
            return row.Values.Select(token => token.Value).ToImmutableArray();
        }
        int Scalar(string name) => Values(name) is [var value] ? value :
            throw new InvalidDataException("魔界大战单值字段宽度无效: " + name);
        return new(Values("dungeon list"), Values("party member count"), Values("waiting area"),
            Scalar("limit time"), Scalar("incount dungeon index"), Scalar("limit daily enter count"),
            Scalar("limit weekly enter count"), Scalar("need fatigue"));
    }
}
