using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.OnlineEvents;

internal sealed record DailyAttendanceReward(int Index, int RequiredDays, int ItemId, int Count);

internal sealed class DailyAttendanceCatalog
{
    public const string ResourcePath = "event/chn_event/chn_dailyattendanceanytimeevent.evt";
    public const int ClearTarget = 2;
    public ImmutableArray<DailyAttendanceReward> Daily { get; }
    public ImmutableArray<DailyAttendanceReward> Accumulated { get; }

    public DailyAttendanceCatalog(ImmutableArray<DailyAttendanceReward> daily,
        ImmutableArray<DailyAttendanceReward> accumulated)
    {
        if (daily.IsDefaultOrEmpty || daily.Length > 28 || accumulated.Length != 3 ||
            daily.Where((row, index) => row.Index != index || row.RequiredDays != index + 1).Any() ||
            accumulated.Where((row, index) => row.Index != index || row.RequiredDays <= 0 || row.RequiredDays > daily.Length).Any() ||
            accumulated.Select(row => row.RequiredDays).Distinct().Count() != 3 ||
            !accumulated.Select(row => row.RequiredDays).SequenceEqual(accumulated.Select(row => row.RequiredDays).Order()) ||
            daily.Concat(accumulated).Any(row => row.ItemId <= 0 || row.Count <= 0))
            throw new InvalidDataException("日签到奖励档位、数量或累计天数无效");
        Daily = daily;
        Accumulated = accumulated;
    }

    public static DailyAttendanceCatalog Load(PvfArchive pvf, ItemCatalog items)
    {
        var document = new PvfTokenDocument(pvf.ReadTokens(ResourcePath), pvf.ResolveString);
        if (!document.Diagnostics.IsEmpty || document.Sections.Length != 2)
            throw new InvalidDataException("日签到活动声明未正确闭合");
        var catalog = new DailyAttendanceCatalog(Read("daily attendance reward", false),
            Read("accumulate attendance reward", true));
        foreach (int id in catalog.Daily.Concat(catalog.Accumulated).Select(row => row.ItemId).Distinct())
            _ = items.Get(id);
        return catalog;

        ImmutableArray<DailyAttendanceReward> Read(string name, bool accumulated)
        {
            var fields = document.Sections.Where(row => row.Name == name).ToArray();
            if (fields is not [var field] || !field.IsBlock || !field.Children.IsEmpty ||
                field.Values.Length % 3 != 0 || field.Values.Any(value => value.Type != 0))
                throw new InvalidDataException("日签到奖励字段无效: " + name);
            var rows = ImmutableArray.CreateBuilder<DailyAttendanceReward>();
            for (int at = 0; at < field.Values.Length; at += 3)
            {
                int key = field.Values[at].Value;
                rows.Add(new(accumulated ? at / 3 : key, accumulated ? key : key + 1,
                    field.Values[at + 1].Value, field.Values[at + 2].Value));
            }
            return rows.ToImmutable();
        }
    }
}
