using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

internal enum DungeonDropCategory { Special, Gold, Materials, Equipment, MonsterItems }
internal enum DungeonDropScope { All, Ordinary, Story, Raid }

internal sealed record DungeonDropMultipliers(decimal Gold = 1, decimal Materials = 1, decimal Equipment = 1,
    decimal MonsterItems = 1, decimal Special = 1)
{
    public decimal For(DungeonDropCategory category) => category switch
    {
        DungeonDropCategory.Gold => Gold,
        DungeonDropCategory.Materials => Materials,
        DungeonDropCategory.Equipment => Equipment,
        DungeonDropCategory.MonsterItems => MonsterItems,
        _ => Special
    };
    public void Validate()
    {
        foreach (var value in new[] { Gold, Materials, Equipment, MonsterItems, Special })
            if (value is < 0 or > 100) throw new InvalidDataException("掉落倍率必须在0至100之间");
    }
}

internal sealed record DungeonDropControlRule(DungeonDropScope Scope,
    ImmutableArray<int> DungeonIds, DungeonDropMultipliers Multipliers);

// 配置快照只在新入场准备计划时读取一次，不改变已生成物件及已冻结的随机结果。
internal sealed class DungeonDropControlSnapshot
{
    public bool Enabled { get; }
    public ImmutableArray<DungeonDropControlRule> Rules { get; }
    public DungeonDropControlSnapshot(IEnumerable<DungeonDropControlRule> rules, bool enabled)
    {
        Rules = rules.ToImmutableArray();
        if (Rules.Length > 4 || Rules.Any(rule => rule is null) || Rules.Select(rule => rule.Scope).Distinct().Count() != Rules.Length)
            throw new InvalidDataException("每个掉落范围只能配置一条规则，最多四条");
        foreach (var rule in Rules)
        {
            if (!Enum.IsDefined(rule.Scope) || rule.Multipliers is null || rule.DungeonIds.IsDefault ||
                rule.DungeonIds.Length > 4096 || rule.DungeonIds.Any(id => id <= 0) ||
                rule.DungeonIds.Distinct().Count() != rule.DungeonIds.Length)
                throw new InvalidDataException("掉落规则范围、副本集合或倍率不合法");
            rule.Multipliers.Validate();
        }
        Enabled = enabled;
    }

    public DungeonDropMultipliers? Resolve(DungeonEntryPlan entry)
    {
        if (!Enabled) return null;
        DungeonDropScope? scope = entry.RaidEntry is not null ? DungeonDropScope.Raid :
            IsStory(entry) ? DungeonDropScope.Story : IsOrdinary(entry) ? DungeonDropScope.Ordinary : null;
        bool Matches(DungeonDropControlRule rule) => rule.DungeonIds.IsEmpty ||
            entry.DungeonId <= int.MaxValue && rule.DungeonIds.Contains((int)entry.DungeonId);
        return Rules.FirstOrDefault(rule => rule.Scope == scope && Matches(rule))?.Multipliers ??
            Rules.FirstOrDefault(rule => rule.Scope == DungeonDropScope.All && Matches(rule))?.Multipliers;
    }

    static bool IsStory(DungeonEntryPlan entry) => entry.RaidEntry is null && !entry.IsTutorial &&
        !entry.IsPractice && !entry.IsTrainingRoom && !entry.IsClassExperience &&
        (entry.RouteQuestId != 0 || entry.QuestId != 0 && !entry.IsOrdinaryHunt);

    static bool IsOrdinary(DungeonEntryPlan entry) => entry.IsOrdinary && entry.RaidEntry is null &&
        !entry.IsTutorial && !entry.IsPractice && !entry.IsTrainingRoom && !entry.IsClassExperience &&
        !entry.IsTournament && !entry.IsAltar && !entry.IsDespair && !entry.Maze.IsGriefTower &&
        !entry.IsBossRush && !entry.BeastMode && entry.LostLand is null && !entry.IsTagSystem &&
        entry.Hope is null && entry.Dimension is null && entry.Marriage is null;

    public static DungeonDropGenerationPlan Apply(DungeonDropGenerationPlan plan, DungeonDropMultipliers? rates)
    {
        if (rates is null) return plan;
        rates.Validate();
        var trials = plan.Trials.Select(trial => trial with
        {
            Threshold = (int)decimal.Min(trial.ProbabilityDenominator,
                decimal.Floor(trial.Threshold * rates.For(trial.Category)))
        }).ToImmutableArray();
        return new(plan.ResourceSource + ";drop-control-v1", trials, plan.ReferenceQuality, plan.Amplification)
            { OrdinaryLukeDailySource = plan.OrdinaryLukeDailySource };
    }
}

internal sealed class DungeonDropControlSettings
{
    DungeonDropControlSnapshot current = new([], false);
    public DungeonDropControlSnapshot Current => Volatile.Read(ref current);
    public void Replace(IEnumerable<DungeonDropControlRule> rules, bool enabled) =>
        Volatile.Write(ref current, new(rules, enabled));
}
