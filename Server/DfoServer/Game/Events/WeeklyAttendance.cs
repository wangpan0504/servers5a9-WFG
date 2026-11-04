using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.OnlineEvents;

namespace DfoServer.Game.Events;

internal sealed record WeeklyAttendanceSeason(long StartsAt, long EndsAt, long ActivatedAt, int Weeks)
{
    public static WeeklyAttendanceSeason Create(DateTimeOffset now, int weeks)
    {
        var local = now.ToOffset(TimeSpan.FromHours(8)).AddHours(-6);
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, local.Offset)
            .AddDays(-(((int)local.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7));
        return new(start.ToUnixTimeSeconds(), start.AddDays(7 * weeks).ToUnixTimeSeconds(), now.ToUnixTimeSeconds(), weeks);
    }
    public int Week(DateTimeOffset now) => now.ToUnixTimeSeconds() < ActivatedAt || now.ToUnixTimeSeconds() >= EndsAt
        ? -1 : checked((int)((now.ToUnixTimeSeconds() - StartsAt) / 604800));
}

internal sealed record WeeklyAttendanceSnapshot(int Week, int Total, int Clears, int Catchups, uint BonusMask,
    bool SignedThisWeek, bool CanSign, bool CanCatchup, bool CanBonus, byte[] SignedWeeks);
internal sealed record WeeklyAttendanceCommit(WeeklyAttendanceSnapshot Snapshot, ImmutableArray<uint> MailIds);
internal sealed class WeeklyAttendanceRejectedException(string message) : InvalidOperationException(message);

internal interface IWeeklyAttendanceStore
{
    WeeklyAttendanceSeason? ReadWeeklyAttendanceSeason();
    WeeklyAttendanceSeason ActivateWeeklyAttendance(AccountOwners.Lease owner, Character expected, DateTimeOffset now, WeeklyAttendanceCatalog catalog);
    WeeklyAttendanceState ReadWeeklyAttendance(AccountOwners.Lease owner, WeeklyAttendanceSeason season, WeeklyAttendanceCatalog catalog);
    WeeklyAttendanceCommit CommitWeeklyAttendance(AccountOwners.Lease owner, Character expected, WeeklyAttendanceSeason season,
        DateTimeOffset now, WeeklyAttendanceCatalog catalog, bool claim, bool bonus,
        IReadOnlyList<ImmutableArray<InventoryItem>> attachments);
}

internal sealed class WeeklyAttendanceState
{
    public long LastObserved { get; set; }
    public int[] Clears { get; set; } = [];
    public uint Signed { get; set; }
    public int Catchups { get; set; }
    public uint Bonus { get; set; }
    public static WeeklyAttendanceState Parse(string? json, WeeklyAttendanceCatalog catalog)
    {
        WeeklyAttendanceState value;
        try { value = json is null ? new() { Clears = new int[catalog.Weeks] } :
            JsonSerializer.Deserialize<WeeklyAttendanceState>(json) ?? throw new InvalidDataException("周签到账本为空"); }
        catch (JsonException error) { throw new InvalidDataException("周签到账本损坏", error); }
        uint bonusBits = catalog.Bonus.Aggregate(0u, (mask, row) => mask | 1u << row.Threshold);
        if (value.Clears is null || value.Clears.Length != catalog.Weeks || value.LastObserved < 0 ||
            value.Clears.Any(count => count < 0 || count > catalog.MaximumClears) || value.Signed >> catalog.Weeks != 0 ||
            value.Catchups < 0 || value.Catchups > catalog.CatchupLimit || value.Catchups > BitOperations.PopCount(value.Signed) ||
            (value.Bonus & ~bonusBits) != 0 || catalog.Bonus.Any(row => (value.Bonus & (1u << row.Threshold)) != 0 &&
                BitOperations.PopCount(value.Signed) < row.Threshold))
            throw new InvalidDataException("周签到账本超出目录边界");
        return value;
    }
    public WeeklyAttendanceSnapshot Snapshot(WeeklyAttendanceCatalog catalog, int week)
    {
        int total = BitOperations.PopCount(Signed);
        bool signed = week >= 0 && (Signed & (1u << week)) != 0;
        bool catchup = signed && total < week + 1 && Catchups < catalog.CatchupLimit;
        int clears = week < 0 ? 0 : Clears[week];
        return new(week < 0 ? catalog.Weeks + 1 : week + 1, total, clears, Catchups, Bonus, signed,
            week >= 0 && (!signed && clears >= catalog.RequiredClears || catchup && clears >= catalog.CatchupClears),
            catchup, week >= 0 && catalog.Bonus.Any(row => total >= row.Threshold && (Bonus & (1u << row.Threshold)) == 0),
            Enumerable.Range(0, catalog.Weeks).Where(i => (Signed & (1u << i)) != 0).Select(i => (byte)(i + 1)).ToArray());
    }
    public ImmutableArray<WeeklyAttendanceReward> Claim(WeeklyAttendanceCatalog catalog, WeeklyAttendanceSeason season,
        DateTimeOffset now, bool claim, bool bonus)
    {
        if (now.ToUnixTimeSeconds() < LastObserved) throw new WeeklyAttendanceRejectedException("周签到时钟倒退");
        int week = season.Week(now);
        var before = Snapshot(catalog, week);
        var rewards = ImmutableArray.CreateBuilder<WeeklyAttendanceReward>();
        if (claim && before.CanSign)
        {
            int target = week;
            if (before.SignedThisWeek)
            {
                target = Enumerable.Range(0, week).First(i => (Signed & (1u << i)) == 0);
                Clears[week] -= catalog.CatchupClears;
                Catchups++;
            }
            else Clears[week] -= catalog.RequiredClears;
            Signed |= 1u << target;
            rewards.Add(catalog.Rewards[before.Total]);
        }
        if (bonus && week >= 0)
            foreach (var reward in catalog.Bonus)
                if (BitOperations.PopCount(Signed) >= reward.Threshold && (Bonus & (1u << reward.Threshold)) == 0)
                {
                    Bonus |= 1u << reward.Threshold;
                    rewards.Add(reward);
                }
        if (rewards.Count != 0) LastObserved = now.ToUnixTimeSeconds();
        return rewards.ToImmutable();
    }
    public string Serialize() => JsonSerializer.Serialize(this);
}
