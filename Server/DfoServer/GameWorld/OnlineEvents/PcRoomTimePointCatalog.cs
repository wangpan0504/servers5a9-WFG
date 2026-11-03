using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.OnlineEvents;

internal sealed record PcRoomTimePointDailyReward(int ItemId, int Count, long RequiredMilliseconds,
    int Kind, bool FirstFlag, bool SecondFlag)
{
    // 状态包以秒表示累计在线；不足整秒的资源阈值须等到下一秒，不提前发奖。
    public long RequiredSeconds => (RequiredMilliseconds + 999) / 1000;
}
internal sealed record PcRoomTimePointPeriodReward(int StageIndex, int ItemId, int Count,
    int RequiredDays, bool Flag);

internal sealed class PcRoomTimePointCatalog
{
    public const string ResourcePath = "etc/pcroomtimepoint.etc";
    public ImmutableArray<PcRoomTimePointDailyReward> Daily { get; }
    public ImmutableArray<PcRoomTimePointPeriodReward> Period { get; }
    public bool DailyRewardAutoGet { get; }
    public bool ExitAlarm { get; }
    public int DailyRewardLoop { get; }
    public int PeriodRewardLoop { get; }
    public string UiFile { get; }
    // 此活动的历史技术名不构成网吧身份条件；本资源没有pc room event资格字段。
    public bool PcRoomOnly => false;

    public PcRoomTimePointCatalog(PcRoomTimePointFile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string[] fields = ["daily reward autoget", "exit alarm", "daily reward loop", "period reward loop",
            "daily reward items", "period reward item", "ui file"];
        if (source.Fields.IsDefault || source.Fields.Any(field => !fields.Contains(field.Name)) ||
            source.DailyRewards.IsDefaultOrEmpty || source.DailyRewards.Length > 4 ||
            source.PeriodRewards.IsDefaultOrEmpty || source.PeriodRewards.Length > 4 ||
            source.DailyRewardLoop < 0 || source.PeriodRewardLoop < 0)
            throw new InvalidDataException("累计在线活动字段、档数或循环声明无效");
        DailyRewardAutoGet = source.DailyRewardAutoGet;
        ExitAlarm = source.ExitAlarm;
        DailyRewardLoop = source.DailyRewardLoop;
        PeriodRewardLoop = source.PeriodRewardLoop;
        UiFile = PvfArchive.NormalizePath(source.UiFile);
        if (!UiFile.StartsWith("ui/", StringComparison.Ordinal) || !UiFile.EndsWith(".xui", StringComparison.Ordinal))
            throw new InvalidDataException("累计在线界面路径无效");
        long milliseconds = 0;
        var daily = ImmutableArray.CreateBuilder<PcRoomTimePointDailyReward>();
        foreach (var row in source.DailyRewards)
        {
            if (row.Kind < 0 || row.ItemId <= 0 || row.Count <= 0 || row.DurationMilliseconds <= 0)
                throw new InvalidDataException("累计在线每日奖励或时长无效");
            milliseconds = checked(milliseconds + row.DurationMilliseconds);
            daily.Add(new(row.ItemId, row.Count, milliseconds, row.Kind, row.FirstFlag, row.SecondFlag));
        }
        int days = 0;
        var period = ImmutableArray.CreateBuilder<PcRoomTimePointPeriodReward>();
        var stageIds = new HashSet<int>();
        foreach (var row in source.PeriodRewards.OrderBy(row => row.StageIndex))
        {
            if (row.StageIndex is < 1 or > 4 || !stageIds.Add(row.StageIndex) ||
                row.ItemId <= 0 || row.Count <= 0 || row.RequiredCount <= 0)
                throw new InvalidDataException("累计在线累计奖励或天数无效");
            days = checked(days + row.RequiredCount);
            period.Add(new(row.StageIndex, row.ItemId, row.Count, days, row.Flag));
        }
        Daily = daily.ToImmutable();
        Period = period.ToImmutable();
    }

    public static PcRoomTimePointCatalog Load(PvfArchive pvf, ItemCatalog items)
    {
        var catalog = new PcRoomTimePointCatalog(PcRoomTimePointFile.Parse(pvf.ReadTokens(ResourcePath), pvf.ResolveString));
        if (!pvf.Contains(catalog.UiFile))
            throw new InvalidDataException("累计在线资源声明的界面不存在");
        foreach (int itemId in catalog.Daily.Select(row => row.ItemId).Concat(catalog.Period.Select(row => row.ItemId)).Distinct())
            _ = items.Get(itemId);
        return catalog;
    }
}