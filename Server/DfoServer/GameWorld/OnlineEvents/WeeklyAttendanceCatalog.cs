using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.OnlineEvents;

internal sealed record WeeklyAttendanceReward(int Threshold, int ItemId, int Count);

internal sealed class WeeklyAttendanceCatalog(int weeks, int requiredClears, int catchupClears, int catchupLimit,
    ImmutableArray<WeeklyAttendanceReward> rewards, ImmutableArray<WeeklyAttendanceReward> bonus)
{
    public const string ResourcePath = "event/chn_event/chn_totalattendance.evt";
    public int Weeks { get; } = weeks;
    public int RequiredClears { get; } = requiredClears;
    public int CatchupClears { get; } = catchupClears;
    public int CatchupLimit { get; } = catchupLimit;
    public int MaximumClears => checked(RequiredClears + CatchupClears * CatchupLimit);
    public ImmutableArray<WeeklyAttendanceReward> Rewards { get; } = rewards;
    public ImmutableArray<WeeklyAttendanceReward> Bonus { get; } = bonus;

    public void Validate()
    {
        if (Weeks is < 1 or > 20 || RequiredClears is < 1 or > 10000 || CatchupClears is < 1 or > 10000 ||
            CatchupLimit < 0 || CatchupLimit > Weeks || Rewards.Length != Weeks || Bonus.Length > 3 ||
            !Rewards.Select(row => row.Threshold).SequenceEqual(Enumerable.Range(1, Weeks)) ||
            Bonus.Any(row => row.Threshold < 1 || row.Threshold > Weeks) ||
            !Bonus.Select(row => row.Threshold).SequenceEqual(Bonus.Select(row => row.Threshold).Distinct().Order()) ||
            Rewards.Concat(Bonus).Any(row => row.ItemId <= 0 || row.Count <= 0))
            throw new InvalidDataException("周签到周期、通关条件或奖励无效");
    }

    public static WeeklyAttendanceCatalog Load(PvfArchive pvf, ItemCatalog items)
    {
        var document = new PvfTokenDocument(pvf.ReadTokens(ResourcePath), pvf.ResolveString);
        var fields = document.Sections;
        PvfTokenSection Field(string name) => fields.Single(row => row.Name == name);
        int[] Numbers(string name)
        {
            var row = Field(name);
            if (row.Values.Any(token => token.Type != 0) || !row.Children.IsEmpty)
                throw new InvalidDataException("周签到整数段无效: " + name);
            return row.Values.Select(token => token.Value).ToArray();
        }
        int Scalar(string name) => Numbers(name) is [int value] ? value :
            throw new InvalidDataException("周签到标量无效: " + name);
        ImmutableArray<WeeklyAttendanceReward> RewardsFor(string name)
        {
            int[] values = Numbers(name);
            if (values.Length % 3 != 0) throw new InvalidDataException("周签到奖励行宽无效");
            return Enumerable.Range(0, values.Length / 3)
                .Select(i => new WeeklyAttendanceReward(values[i * 3], values[i * 3 + 1], values[i * 3 + 2])).ToImmutableArray();
        }
        if (Scalar("reward by the week") != 0 || Numbers("attendance day").Length != 0 ||
            Numbers("total attendance day").Length != 0 || Numbers("total attendance week 2nd").Length != 0)
            throw new InvalidDataException("周签到包含未支持的发奖模式");
        int[] conditions = Numbers("attendance condition");
        if (conditions.Length != 2) throw new InvalidDataException("周签到通关条件行宽无效");
        var catalog = new WeeklyAttendanceCatalog(Scalar("event duration"), conditions[0], conditions[1],
            Scalar("bonus attendance count"), RewardsFor("attendance week"), RewardsFor("total attendance week"));
        catalog.Validate();
        var ui = Field("xui path").Values;
        if (ui.Length != 1 || ui[0].Type != 6 || !pvf.Contains(PvfArchive.NormalizePath(pvf.ResolveString(ui[0].Value))))
            throw new InvalidDataException("周签到界面路径无效或缺失");
        foreach (var reward in catalog.Rewards.Concat(catalog.Bonus)) _ = items.Get(reward.ItemId);
        return catalog;
    }
}
