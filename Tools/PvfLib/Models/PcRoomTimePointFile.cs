using System.Collections.Immutable;

namespace PvfLib;

public sealed record PcRoomTimePointDailyRewardFile(int Kind, int ItemId, int Count,
    int DurationMilliseconds, bool FirstFlag, bool SecondFlag);
public sealed record PcRoomTimePointPeriodRewardFile(int StageIndex, int ItemId, int Count,
    int RequiredCount, bool Flag);

// 原始时长、次数和布尔列原样保留；累计阈值与领奖政策由活动领域解释。
public sealed record PcRoomTimePointFile(bool DailyRewardAutoGet, bool ExitAlarm, int DailyRewardLoop,
    int PeriodRewardLoop, ImmutableArray<PcRoomTimePointDailyRewardFile> DailyRewards,
    ImmutableArray<PcRoomTimePointPeriodRewardFile> PeriodRewards, string UiFile,
    ImmutableArray<PvfTokenSection> Fields)
{
    public static PcRoomTimePointFile Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty)
            throw new InvalidDataException("累计在线活动声明未正确闭合");
        var dailyValues = Field("daily reward items", true).Values;
        var periodValues = Field("period reward item", true).Values;
        if (dailyValues.Length % 6 != 0 || periodValues.Length % 5 != 0)
            throw new InvalidDataException("累计在线奖励行宽度无效");
        var daily = ImmutableArray.CreateBuilder<PcRoomTimePointDailyRewardFile>();
        for (int at = 0; at < dailyValues.Length; at += 6)
            daily.Add(new(Integer(dailyValues[at]), Integer(dailyValues[at + 1]), Integer(dailyValues[at + 2]),
                Integer(dailyValues[at + 3]), Boolean(dailyValues[at + 4]), Boolean(dailyValues[at + 5])));
        var period = ImmutableArray.CreateBuilder<PcRoomTimePointPeriodRewardFile>();
        for (int at = 0; at < periodValues.Length; at += 5)
            period.Add(new(Integer(periodValues[at]), Integer(periodValues[at + 1]), Integer(periodValues[at + 2]),
                Integer(periodValues[at + 3]), Boolean(periodValues[at + 4])));
        return new(Boolean(Scalar("daily reward autoget")), Boolean(Scalar("exit alarm")),
            Integer(Scalar("daily reward loop")), Integer(Scalar("period reward loop")), daily.ToImmutable(),
            period.ToImmutable(), String(Scalar("ui file")), document.Sections);

        PvfTokenSection Field(string name, bool block)
        {
            var rows = document.Sections.Where(row => row.Name == name).ToArray();
            if (rows is not [var row] || row.IsBlock != block || !row.Children.IsEmpty)
                throw new InvalidDataException("累计在线字段缺失、重复或结构无效: " + name);
            return row;
        }
        PvfToken Scalar(string name)
        {
            var values = Field(name, false).Values;
            return values is [var value] ? value : throw new InvalidDataException("累计在线单值字段宽度无效: " + name);
        }
        static int Integer(PvfToken token) => token.Type == 0 ? token.Value :
            throw new InvalidDataException("累计在线整数列类型无效");
        string String(PvfToken token) => token.Type == 6 ? resolve(token.Value) :
            throw new InvalidDataException("累计在线字符串列类型无效");
        bool Boolean(PvfToken token) => String(token) switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidDataException("累计在线布尔列必须为true或false")
        };
    }
}