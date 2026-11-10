using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

internal enum DungeonDropCategory { Special, Gold, Materials, Equipment, MonsterItems }
internal enum DungeonDropScope { All, Ordinary, Story, Raid }

internal sealed record DungeonDropMultipliers(decimal Gold = 1, decimal Materials = 1, decimal Equipment = 1,
    decimal MonsterItems = 1, decimal Special = 1, bool ExcludeLowEquipment = false, bool ExcludeLowMaterials = false)
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

    // 脱离单张地图的奖励只使用未限定副本ID的范围规则，不能把某张图的设置扩散到整个攻坚队。
    public DungeonDropMultipliers? ResolveReward(DungeonDropScope scope) => !Enabled ? null :
        Rules.FirstOrDefault(rule => rule.Scope == scope && rule.DungeonIds.IsEmpty)?.Multipliers ??
        Rules.FirstOrDefault(rule => rule.Scope == DungeonDropScope.All && rule.DungeonIds.IsEmpty)?.Multipliers;

    static bool IsStory(DungeonEntryPlan entry) => entry.RaidEntry is null && !entry.IsTutorial &&
        !entry.IsPractice && !entry.IsTrainingRoom && !entry.IsClassExperience &&
        (entry.RouteQuestId != 0 || entry.QuestId != 0 && !entry.IsOrdinaryHunt);

    static bool IsOrdinary(DungeonEntryPlan entry) => entry.IsOrdinary && entry.RaidEntry is null &&
        !entry.IsTutorial && !entry.IsPractice && !entry.IsTrainingRoom && !entry.IsClassExperience &&
        !entry.IsTournament && !entry.IsAltar && !entry.IsDespair && !entry.Maze.IsGriefTower &&
        !entry.IsBossRush && entry.LostLand is null && !entry.IsTagSystem &&
        entry.Hope is null && entry.Dimension is null && entry.Marriage is null && entry.EvildomWarMode is null;

    public static DungeonDropGenerationPlan Apply(DungeonDropGenerationPlan plan, DungeonDropMultipliers? rates)
    {
        if (rates is null) return plan;
        rates.Validate();
        return new(plan.ResourceSource + ";drop-control-v2", plan.Trials.Select(trial => ApplyTrial(trial, rates)), plan.ReferenceQuality, plan.Amplification, plan.Sarpoza)
            { OrdinaryLukeDailySource = plan.OrdinaryLukeDailySource, DropMultipliers = rates,
              ExcludeLowEquipment = rates.ExcludeLowEquipment, ExcludeLowMaterials = rates.ExcludeLowMaterials };
    }

    public static DungeonDropTrial ApplyTrial(DungeonDropTrial trial, DungeonDropMultipliers? rates)
    {
        if (rates is null) return trial;
        decimal multiplier = rates.For(trial.Category);
        DungeonEpicDropAdjustment? epicAdjustment = null;
        if (trial.Category != DungeonDropCategory.Equipment && trial.Items is { Entries.Length: > 0 } direct &&
            direct.Entries.Any(row => DungeonEpicDropAdjustment.IsEpic(row.Template)))
        {
            if (direct.Entries.All(row => DungeonEpicDropAdjustment.IsEpic(row.Template))) multiplier = rates.Equipment;
            else if (multiplier != rates.Equipment)
                epicAdjustment = new(trial.Threshold * rates.Equipment);
        }
        int probabilityDenominator = trial.ProbabilityDenominator;
        decimal probability = decimal.Min(probabilityDenominator, trial.Threshold * multiplier);
        int threshold = (int)decimal.Floor(probability);
        // 活动原表用1/5和1/1表示概率；小数倍率须提高精度，不能把0.5倍误截为零。
        if (trial.EpicRoad2019Simulated && probability != threshold)
        {
            threshold = (int)decimal.Floor(probability * 1000000 / probabilityDenominator);
            probabilityDenominator = 1000000;
        }
        var pools = trial.RarityPools;
        // 外层装备概率封顶后，剩余史诗倍率进入稀有度骰，不能把封顶误报成史诗继续增加。
        if (trial.Category == DungeonDropCategory.Equipment && threshold > 0 && !pools.IsDefaultOrEmpty && pools.Length == 7 &&
            pools[^1].Threshold == DungeonDropRatePlan.RarityDenominator &&
            pools[4].CandidatePools.Any(pool => pool.TotalWeight > 0))
        {
            int original = pools[4].Threshold - pools[3].Threshold;
            int denominator = DungeonDropRatePlan.RarityDenominator;
            int enhanced = (int)decimal.Min(denominator, decimal.Floor((decimal)original * trial.Threshold * multiplier / threshold));
            if (enhanced != original && original < denominator)
            {
                long nonEpic = 0;
                int previous = 0;
                pools = pools.Select((pool, index) =>
                {
                    int width = pool.Threshold - previous;
                    previous = pool.Threshold;
                    if (index != 4) nonEpic += width;
                    int cumulative = checked((int)(nonEpic * (denominator - enhanced) / (denominator - original)));
                    return pool with { Threshold = cumulative + (index >= 4 ? enhanced : 0) };
                }).ToImmutableArray();
            }
        }
        return trial with { Threshold = threshold, ProbabilityDenominator = probabilityDenominator, RarityPools = pools, EpicAdjustment = epicAdjustment };
    }
}

internal sealed class DungeonDropControlSettings
{
    DungeonDropControlSnapshot current = new([], false);
    public DungeonDropControlSnapshot Current => Volatile.Read(ref current);
    public void Replace(IEnumerable<DungeonDropControlRule> rules, bool enabled) =>
        Volatile.Write(ref current, new(rules, enabled));
}
