using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.GameWorld.BattlePass;

namespace DfoServer.Game.BattlePass;

internal readonly record struct BattlePassMissionKey(byte Category, long Period, int Id);
internal sealed record BattlePassMissionInstance(BattlePassMissionKey Key, int ViewSlot, BattlePassMission Definition,
    bool Premium, long Experience);
internal sealed record BattlePassMissionSlot(BattlePassMissionInstance Instance, long Count, bool Completed, bool Ready);
internal sealed record BattlePassMissionState(BattlePassSeasonState Season, ImmutableArray<BattlePassMissionSlot> Slots)
{
    public bool Active => Slots.Length == 26;
    public byte[] ToPayload()
    {
        if (!Active) throw new InvalidOperationException("非活动期不能构造虚构任务槽");
        byte[] body = new byte[156];
        for (int i = 0; i < Slots.Length; i++)
        {
            var row = Slots[i];
            if (row.Count < 0 || row.Instance.Key.Id is <= 0 or > ushort.MaxValue)
                throw new InvalidDataException("战令任务投影无效");
            var cell = body.AsSpan(6 * i, 6);
            BinaryPrimitives.WriteUInt16LittleEndian(cell, checked((ushort)row.Instance.Key.Id));
            BinaryPrimitives.WriteUInt16LittleEndian(cell[2..], checked((ushort)Math.Min(ushort.MaxValue,
                Math.Min(row.Count, row.Instance.Definition.RequiredCount))));
            cell[4] = row.Ready ? (byte)1 : (byte)0;
            cell[5] = row.Completed ? (byte)1 : (byte)0;
        }
        return body;
    }
}

// 周期、类别映射和高级倍率是显式本服策略，不冒充原服务器隐藏周期已复原。
internal sealed class BattlePassMissionPolicy
{
    public string SeasonKey { get; }
    public BattlePassCatalog Catalog { get; }
    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public int Days { get; }
    public string RulesJson { get; }

    public BattlePassMissionPolicy(string seasonKey, DateTimeOffset start, BattlePassCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seasonKey);
        var local = start.ToOffset(TimeSpan.FromHours(8));
        if (local.DayOfWeek != DayOfWeek.Thursday || local.TimeOfDay != TimeSpan.FromHours(6) ||
            start.ToUnixTimeSeconds() < 0 || catalog.DailyMissionIds.IsEmpty || catalog.WeeklyMissionIds.IsEmpty ||
            catalog.DailyMissionIds.Length % 4 != 0 || catalog.WeeklyMissionIds.Length % 4 != 0 ||
            catalog.SeasonMissionIds.Length != 18 || catalog.SeasonMissionIds.Distinct().Count() != 18 ||
            catalog.RawGoldenMissionExperienceRatio is <= 0 or > ushort.MaxValue)
            throw new InvalidDataException("战令任务需要显式周四06:00起点和完整有界目录");
        foreach (var group in catalog.DailyMissionIds.Chunk(4).Concat(catalog.WeeklyMissionIds.Chunk(4))
            .Append(catalog.SeasonMissionIds.ToArray()))
            if (group.Distinct().Count() != group.Length || group.Any(id => id is <= 0 or > ushort.MaxValue ||
                !catalog.MissionsById.ContainsKey(id))) throw new InvalidDataException("战令任务组键无效或重复");
        SeasonKey = seasonKey;
        Catalog = catalog;
        Start = start;
        Days = Math.Min(catalog.DailyMissionIds.Length / 4, checked(7 * (catalog.WeeklyMissionIds.Length / 4)));
        End = start.AddDays(Days);
        RulesJson = JsonSerializer.Serialize(new
        {
            Version = 1, Category0 = "daily", Category1 = "weekly", Category2 = "season",
            Premium = "daily-slot3-type1-source-counted-before-entitlement", Schedule = "ordered-exhaustion",
            Start = Start.ToUnixTimeSeconds(), End = End.ToUnixTimeSeconds(),
            Daily = catalog.DailyMissionIds, Weekly = catalog.WeeklyMissionIds, Season = catalog.SeasonMissionIds,
            Definitions = catalog.Missions, Experience = catalog.ExperienceTable,
            PremiumExperienceRatio = catalog.RawGoldenMissionExperienceRatio,
            PaidCeraExperiencePercentage = catalog.RawCeraConsumptionExperiencePercentage,
            PaidCeraRounding = "account-season-cumulative-numerator-remainder-100",
            PaidCeraAllocation = "cash-only-reserved-then-request-order-happy-token-cera-exclude-gold-product"
        });
    }

    public ImmutableArray<BattlePassMissionInstance> Active(DateTimeOffset now)
    {
        if (now < Start || now >= End) return [];
        int day = checked((int)((now - Start).Ticks / TimeSpan.TicksPerDay));
        int week = day / 7;
        var rows = ImmutableArray.CreateBuilder<BattlePassMissionInstance>(26);
        Add(0, Catalog.DailyMissionIds.Skip(4 * day).Take(4), Start.AddDays(day));
        Add(1, Catalog.WeeklyMissionIds.Skip(4 * week).Take(4), Start.AddDays(7 * week));
        Add(2, Catalog.SeasonMissionIds, Start);
        return rows.ToImmutable();

        void Add(byte category, IEnumerable<int> ids, DateTimeOffset period)
        {
            int slot = 0;
            foreach (int id in ids)
            {
                var definition = Catalog.MissionsById[id];
                bool premium = category == 0 && slot == 3;
                long experience = checked((long)definition.Experience * (premium ? Catalog.RawGoldenMissionExperienceRatio : 1));
                rows.Add(new(new(category, period.ToUnixTimeSeconds(), id), slot++, definition, premium, experience));
            }
        }
    }

    // 仅接当前已经核实的两项远征定义，不把未知条件或未来参数泛化为已实现。
    public static bool IsExpeditionStart(BattlePassMission definition) => definition.RawTypeCode == 12 &&
        definition.RawParameter == -1 && (definition.Id == 12 && definition.RequiredCount == 1 && definition.Experience == 100 ||
        definition.Id == 38 && definition.RequiredCount == 3 && definition.Experience == 1000);

    // 仅核实的每日登录定义；周目标3和赛季目标50保持资源原数量，不新增日槽。
    public static bool IsDailyLogin(BattlePassMission definition) => definition.RawTypeCode == 30 &&
        definition.RawParameter == -1 && (definition.Id == 43 && definition.RequiredCount == 3 && definition.Experience == 1000 ||
        definition.Id == 56 && definition.RequiredCount == 50 && definition.Experience == 1600);

}
