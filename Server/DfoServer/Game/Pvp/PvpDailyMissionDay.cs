using System.Collections.Immutable;
using DfoServer.GameWorld.Pvp;
using DfoServer.GameWorld.Time;

namespace DfoServer.Game.Pvp;

internal sealed record PvpMissionPeriod(int Day, long Start, long End)
{
    internal static PvpMissionPeriod At(DateTimeOffset now, TimeZoneInfo zone)
    {
        var date = ServiceDay.Date(now, zone);
        return new(date.DayNumber, ServiceDay.At(date.ToDateTime(new TimeOnly(6, 0)), zone).ToUnixTimeSeconds(),
            ServiceDay.At(date.AddDays(1).ToDateTime(new TimeOnly(6, 0)), zone).ToUnixTimeSeconds());
    }
}

internal sealed record PvpDailyMissionEntry(PvpMissionDefinition Mission, bool Claimed);
internal readonly record struct PvpDailyMissionRow(byte Grade, ushort Id, int Current, int Target, int RewardKey, int RewardCount);

// 本服每天每种模式按资源组0/1/2各分配一项；分配和奖励快照一经提交，当天不再重抽。
internal sealed record PvpDailyMissionDay(int Day, ImmutableArray<PvpDailyMissionEntry> Entries)
{
    internal static PvpDailyMissionDay Draw(int day, ImmutableArray<PvpMissionDefinition> definitions, int rank, Func<int, int>? draw = null)
    {
        var entries = ImmutableArray.CreateBuilder<PvpDailyMissionEntry>();
        foreach (byte grade in new byte[] { 2, 4 })
            foreach (int group in new[] { 0, 1, 2 })
            {
                var candidates = definitions.Where(row => row.Grade == grade && row.Group == group &&
                    rank >= row.MinimumRank && rank <= row.MaximumRank).ToArray();
                if (candidates.Length == 0) throw new InvalidDataException("决斗日常缺少适用候选");
                entries.Add(new(candidates[(draw ?? Random.Shared.Next)(candidates.Length)], false));
            }
        return new(day, entries.ToImmutable());
    }

    internal void Validate(int day)
    {
        if (Day != day || Entries.IsDefault || Entries.Length != 6 || Entries.Any(row => row is null || row.Mission is null) ||
            Entries.Select(row => row.Mission.Id).Distinct().Count() != 6 ||
            Entries.Any(row => row.Mission is not { Grade: 2 or 4, Group: >= 0 and <= 2, Target: > 0 } ||
                row.Mission.Id is 0 or > 32767 || row.Mission.TotalOnly != (row.Mission.Grade == 4) ||
                row.Mission.Condition is not ("hereafter join" or "hereafter winning count" or "winning job" or "winning streak" or "kill count") ||
                row.Mission.Rewards.IsDefaultOrEmpty || row.Mission.Rewards.Any(reward => reward.ItemId <= 0 || reward.Count <= 0)) ||
            Entries.GroupBy(row => (row.Mission.Grade, row.Mission.Group)).Any(group => group.Count() != 1))
            throw new InvalidDataException("决斗日常已保存分配无效");
    }

    internal static bool Same(PvpDailyMissionDay? left, PvpDailyMissionDay? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left.Day == right.Day && left.Entries.Length == right.Entries.Length && left.Entries.Zip(right.Entries).All(pair =>
            pair.First.Claimed == pair.Second.Claimed && DefinitionSame(pair.First.Mission, pair.Second.Mission));
    }

    static bool DefinitionSame(PvpMissionDefinition left, PvpMissionDefinition right) =>
        left with { Rewards = [], Jobs = [] } == right with { Rewards = [], Jobs = [] } &&
        left.Rewards.SequenceEqual(right.Rewards) && left.Jobs.SequenceEqual(right.Jobs);

    internal ImmutableArray<PvpDailyMissionRow> Rows(ImmutableArray<PvpArenaResultRecord> matches, ushort character) =>
        Entries.Select(row =>
        {
            // 本服当前候选均为一个证明奖励；不要把其它奖励悄悄丢进未定义的579扩展。
            if (row.Mission.Rewards is not [{ ItemId: 10092640 } reward]) throw new InvalidDataException("日常奖励投影不支持此快照");
            return new PvpDailyMissionRow(row.Mission.Grade, row.Mission.Id,
                row.Claimed ? row.Mission.Target : Math.Min(row.Mission.Target, Current(row.Mission, matches, character)),
                row.Mission.Target, 2, reward.Count);
        }).ToImmutableArray();

    internal static int Current(PvpMissionDefinition mission, ImmutableArray<PvpArenaResultRecord> matches, ushort character)
    {
        int count = 0, streak = 0;
        foreach (var match in matches)
        {
            if (mission.TotalOnly && match.RatingMode != PvpRatingMode.Total) continue;
            var own = match.Participants.SingleOrDefault(row => row.CharacterId == character || row.TotalTeam?.Characters.Contains(character) == true);
            if (own is null || own.Outcome == PvpArenaOutcome.Cancelled) continue;
            bool win = own.Outcome == PvpArenaOutcome.Win;
            count = mission.Condition switch
            {
                "hereafter join" => checked(count + 1),
                "hereafter winning count" => checked(count + (win ? 1 : 0)),
                "winning job" => checked(count + (win && match.Participants.Any(row => row.CharacterId != own.CharacterId &&
                    row.Outcome == PvpArenaOutcome.Lose && row.Job is { } job && mission.Jobs.Contains(job)) ? 1 : 0)),
                "kill count" => (int)Math.Min(int.MaxValue, (long)count + own.Kills),
                "winning streak" => count,
                _ => throw new InvalidDataException("日常目标没有权威进度实现")
            };
            if (mission.Condition == "winning streak")
            {
                if (mission.Parameter <= 0) throw new InvalidDataException("日常连胜长度无效");
                streak = win ? streak + 1 : 0;
                // 本服按不重叠的完整连胜段累计次数，平局及失败均中断当前段。
                if (streak == mission.Parameter) { count++; streak = 0; }
            }
            if (count >= mission.Target) return mission.Target;
        }
        return count;
    }
}
