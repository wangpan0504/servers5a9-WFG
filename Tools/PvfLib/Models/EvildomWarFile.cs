using System.Collections.Immutable;

namespace PvfLib;

// 保留魔界大战原始整数行；不在格式层解释队伍、费用或战役状态。
public sealed record EvildomWarFile(ImmutableArray<int> Dungeons, ImmutableArray<int> PartyMembers,
    ImmutableArray<int> WaitingArea, int LimitTime, int IncountDungeon, int DailyLimit, int WeeklyLimit, int NeedFatigue)
{
    public ImmutableArray<int> HiddenDungeons { get; init; } = [];
    public ImmutableArray<int> RecruitingArea { get; init; } = [];
    public ImmutableArray<int> Rewards { get; init; } = [];
    public ImmutableArray<int> HardModeRewards { get; init; } = [];
    public ImmutableArray<int> GuideModeRewards { get; init; } = [];
    public ImmutableArray<int> AntiEvilLimits { get; init; } = [];
    public int MovieTime { get; init; }
    public int DisconnectWaitTime { get; init; }
    public int DisconnectRetryItem { get; init; }
    public int SwordBuffRate { get; init; }
    public int ShieldBuffRate { get; init; }

    public static EvildomWarFile Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("魔界大战资源块未闭合");
        ImmutableArray<int> Values(string name, bool requireBlock = false)
        {
            var rows = document.Sections.Where(row => row.Name == name).ToArray();
            if (rows is not [var row] || requireBlock && !row.IsBlock || !row.Children.IsEmpty || row.Values.Any(token => token.Type != 0))
                throw new InvalidDataException("魔界大战字段缺失、重复或包含非整数: " + name);
            return row.Values.Select(token => token.Value).ToImmutableArray();
        }
        int Scalar(string name) => Values(name) is [var value] ? value :
            throw new InvalidDataException("魔界大战单值字段宽度无效: " + name);
        ImmutableArray<int> Rows(string name, int width)
        {
            var values = Values(name, requireBlock: name != "recruiting area");
            return !values.IsEmpty && values.Length % width == 0 ? values :
                throw new InvalidDataException("魔界大战整数行宽度无效: " + name);
        }
        return new(Rows("dungeon list", 3), Rows("party member count", 2), Values("waiting area"),
            Scalar("limit time"), Scalar("incount dungeon index"), Scalar("limit daily enter count"),
            Scalar("limit weekly enter count"), Scalar("need fatigue"))
        {
            HiddenDungeons = Rows("hidden dungeon", 3), RecruitingArea = Rows("recruiting area", 4),
            Rewards = Rows("reward list", 5), HardModeRewards = Rows("hard mode reward list", 5),
            GuideModeRewards = Rows("guide mode reward list", 5), AntiEvilLimits = Rows("limit anti evil", 2),
            MovieTime = Scalar("movie time"), DisconnectWaitTime = Scalar("disconnect wait time"),
            DisconnectRetryItem = Scalar("disconnect retry item"), SwordBuffRate = Scalar("sword dungeon buff rate"),
            ShieldBuffRate = Scalar("shield dungeon buff rate")
        };
    }
}
