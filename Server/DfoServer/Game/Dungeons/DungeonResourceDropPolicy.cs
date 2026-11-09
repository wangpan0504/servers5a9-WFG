using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 当前PVF提供候选、权重与概率，本服采用90参考业务算法；不声称是原版抽样。
internal sealed class DungeonResourceDropPolicy(DungeonDropRuleCatalog rules, DungeonItemDropCatalog candidates,
    Func<int, DungeonMonsterDropDeclaration> resolveMonster, Func<int, ItemTemplate> resolveItem, DungeonSpecialDropCatalog? special = null,
    EquipmentAmplificationDropPolicy? amplification = null, HarlemMaterialDropPolicy? materials = null,
    HarlemHellEpicDropPolicy? harlemEpics = null, HarlemAbyssBonusDropPolicy? abyssBonuses = null, SleepyHollowMaterialDropPolicy? sleepyHollow = null,
    OperationHopeRewardPolicy? hopeRewards = null, DimensionRewardPolicy? dimensionRewards = null,
    DungeonDropControlSettings? dropControl = null, LegacyHellDropPolicy? legacyHell = null,
    DungeonRiftEpicDropPolicy? riftEpics = null, PandemoniumRegionalDropPolicy? regional = null, AssaultMaterialDropPolicy? assaultMaterials = null,
    EpicRoad2019SimulatedDropPolicy? epicRoadSimulated = null, TayberrsDropPolicy? tayberrs = null,
    CarbonCrystalDropPolicy? carbonCrystal = null)
{
    public const string Rule = "pvf-monster-reference-v2";
    public static ImmutableArray<string> RetainedFields { get; } = ["drop prob[4]", "rarity row[3]",
        "first boss/named mob hunting", "condition rate", "gold quantity", "gold volume", "item drop rarity control",
        "common champion drop item", "monster repeated item blocks after first"];
    static readonly decimal[] DifficultyRates = [1m, 1.2m, 1.4m, 1.6m, 1.8m];

    // 只在一次入图准备中复用不可变规则；对象编号、位置和实际随机结果仍归各只怪物。
    public Func<DungeonPlannedMonster, DungeonDropGenerationPlan> PrepareEntry(DungeonEntryPlan entry, DungeonHellRewardPolicy? hellRewards = null)
    {
        if (TayberrsDropPolicy.Applies(entry))
        {
            var tayberrsPolicy = tayberrs ?? new TayberrsDropPolicy(new(), resolveItem);
            var tayberrsMultipliers = dropControl?.Current.Resolve(entry);
            return monster => DungeonDropControlSnapshot.Apply(tayberrsPolicy.Prepare(entry, monster), tayberrsMultipliers)
                .WithDisjointRestrictions(entry.DisjointForbiddenDropRarities);
        }
        bool ordinaryLuke = entry.IsOrdinaryLuke && entry.DungeonId is >= 5101 and <= 5106 && entry.IsOrdinary && !entry.IsPractice;
        if (ordinaryLuke && special?.OrdinaryLukeCards.Count != 24)
            throw new NotSupportedException("普通卢克缺少已批准的24张专属卡片资源合同");
        var plans = new Dictionary<(int MonsterId, int Level, byte Type, byte SpawnType, int HellRolls, bool Boss, bool Wave, bool CanReward, bool CosmoBoss, bool LegendRare, bool SleepyHollowBoss, bool DimensionTerminal, bool RiftRewards, bool RegionalReward, bool AssaultMaterial, bool EpicRoadSimulated, bool CarbonCrystal), DungeonDropGenerationPlan>();
        var epicEquipment = harlemEpics?.Pool(entry);
        var dropMultipliers = dropControl?.Current.Resolve(entry);
        bool regionalDrops = regional?.Applies(entry) == true;
        return monster =>
        {
            var key = (monster.MonsterId, monster.CandidateLevel, monster.RewardType, monster.SpawnType, monster.HellRewardRolls,
                monster.IsBoss, monster.HellWave is not null, monster.CanReportDeath && !monster.IsFriendly && !monster.IsScriptedBoss,
                harlemEpics?.IsCosmoBoss(entry, monster) == true, harlemEpics?.IsLegendRare(entry, monster) == true,
                sleepyHollow?.IsRewardBoss(entry, monster) == true,
                entry.Dimension is not null && monster.SourceMap is { } source && entry.IsTerminalMap(source),
                riftEpics?.Matches(entry, monster) == true,
                regionalDrops && PandemoniumRegionalDropPolicy.CanReward(monster), assaultMaterials?.Matches(entry, monster) == true,
                epicRoadSimulated?.Matches(entry, monster) == true,
                carbonCrystal is not null && CarbonCrystalDropPolicy.CanReward(entry, monster));
            if (plans.TryGetValue(key, out var existing)) return existing;
            // APC 不套普通怪概率；深渊奖励与不带奖励的普通计划分别保存。
            var plan = monster.SpawnType > 3
                ? new DungeonDropGenerationPlan("hell-party-apc", [], amplification: amplification)
                : Prepare(monster.MonsterId, monster.CandidateLevel, monster.RewardType, entry.Difficulty, entry.BasisLevel, entry.PartyMemberCount);
            if (riftEpics is not null) plan = riftEpics.Extend(plan, entry, monster);
            if (monster.HellRewardRolls > 0 && hellRewards is not null)
                plan = hellRewards.Extend(plan, entry.BasisLevel ?? monster.CandidateLevel, entry.HellPartyMode,
                    monster.HellRewardRolls, entry.Difficulty, entry.IsHellLegend || epicEquipment is not null || materials?.UsesHarlemFragments(entry) == true,
                    epicEquipment, entry.IsInfernoParty ? entry.Maze.IsInfernoSpecial ? 13 : 11 : null,
                    legacyHell?.EquipmentRange(entry));
            if (legacyHell is not null) plan = legacyHell.Extend(plan, entry, monster);
            if (harlemEpics is not null) plan = harlemEpics.Extend(plan, entry, monster, hellRewards);
            if (harlemEpics is not null) plan = harlemEpics.ExtendLegendRare(plan, entry, monster);
            bool cosmo = harlemEpics?.IsCosmoEntry(entry) == true;
            if (epicEquipment is not null || entry.IsHellLegend || cosmo)
                plan = RestrictHellEquipment(plan, allow90: epicEquipment is not null && !cosmo,
                    epicOnly: epicEquipment is not null || cosmo);
            if (materials is not null) plan = materials.Extend(plan, entry, monster);
            if (abyssBonuses is not null) plan = abyssBonuses.Extend(plan, entry, monster);
            if (sleepyHollow is not null) plan = sleepyHollow.Extend(plan, entry, monster);
            if (hopeRewards is not null) plan = hopeRewards.Extend(plan,entry,monster);
            if (dimensionRewards is not null) plan = dimensionRewards.Extend(plan,entry,monster);
            if (regionalDrops) plan = regional!.Extend(plan, entry, monster);
            if (assaultMaterials is not null) plan = assaultMaterials.Extend(plan, entry, monster);
            if (ordinaryLuke && monster.CanReportDeath && !monster.IsFriendly && !monster.IsScriptedBoss &&
                monster.SpawnType <= 3 && special?.OrdinaryLukeCards.TryGetValue(monster.MonsterId, out var card) == true)
            {
                var template = resolveItem(card.ItemId);
                if (!template.IsMonsterCard || template.Kind != ItemKind.Profession || !template.StackableType.StartsWith("[material expert job]", StringComparison.Ordinal) ||
                    template.CreationRate != 0 || template.Grade != 1)
                    throw new InvalidDataException("普通卢克专属卡片模板不是已核对的副职业卡片");
                // 用户批准的本服兼容：保留原概率，百万分母，每次合法死亡一次，不叠加成长契约。
                var trial = new DungeonDropTrial(card.Probabilities[entry.Difficulty],
                    new DungeonItemDropPool([new DungeonWeightedDropItem(template, 1)]), 1, 1,
                    ProbabilityDenominator: 1000000);
                plan = new(plan.ResourceSource + ";luke-normal-card-compat-v1;" + DungeonSpecialDropCatalog.CardPath, plan.Trials.Add(trial),
                    plan.ReferenceQuality, plan.Amplification) { OrdinaryLukeDailySource = monster.MonsterId };
            }
            if (carbonCrystal is not null) plan = carbonCrystal.Extend(plan, entry, monster);
            plan = DungeonDropControlSnapshot.Apply(plan, dropMultipliers);
            if (epicRoadSimulated is not null) plan = epicRoadSimulated.Extend(plan, entry, monster);
            plan = plan.WithDisjointRestrictions(entry.DisjointForbiddenDropRarities);
            plans.Add(key, plan);
            return plan;
        };
    }

    // 三种哈林深渊只从通用装备池保留模式允许的史诗；材料与其它非装备掉落沿用原规则。
    static DungeonDropGenerationPlan RestrictHellEquipment(DungeonDropGenerationPlan plan, bool allow90, bool epicOnly)
    {
        DungeonItemDropPool Restrict(DungeonItemDropPool pool, bool rarityEpic = false)
        {
            bool Level(ItemTemplate item) => item.MinimumLevel == 95 || allow90 && item.MinimumLevel == 90;
            bool Allowed(ItemTemplate item) => item.Kind != ItemKind.Equipment ||
                (!(epicOnly || rarityEpic) && item.Rarity != 4 || item.Rarity == 4 && Level(item));
            if (pool.Entries.All(entry => Allowed(entry.Template))) return pool;
            int previous = 0;
            var kept = new List<DungeonWeightedDropItem>();
            foreach (var entry in pool.Entries)
            {
                int weight = entry.CumulativeWeight - previous;
                previous = entry.CumulativeWeight;
                if (Allowed(entry.Template)) kept.Add(new(entry.Template, weight));
            }
            return new(kept);
        }
        return new(plan.ResourceSource, plan.Trials.Select(trial =>
        {
            var items = trial.Items is { } direct ? Restrict(direct) : null;
            return trial with
            {
                Items = items,
                ItemCounts = trial.ItemCounts is { } counts && items is not null
                    ? items.Entries.ToImmutableDictionary(row => row.Template.Id, row => counts[row.Template.Id]) : trial.ItemCounts,
                RarityPools = trial.RarityPools.IsDefault ? default : trial.RarityPools.Select((row, rarity) =>
                    row with { Items = Restrict(row.Items, rarity == 4),
                        AlternateItems = row.AlternateItems is { } alternate ? Restrict(alternate, rarity == 4) : null,
                        SecondaryItems = row.SecondaryItems is { } secondary ? Restrict(secondary, rarity == 4) : null }).ToImmutableArray()
            };
        }), plan.ReferenceQuality, plan.Amplification);
    }

    public DungeonDropGenerationPlan Prepare(int monsterId, int level, byte monsterType, byte difficulty, int? dungeonLevel = null,
        byte partyMembers = 1)
    {
        // 概率段取副本等级，候选及金币引用取怪物等级；两种等级不能互相替代。
        var rates = DungeonDropRatePlan.Prepare(rules, level, monsterType, difficulty, partyMembers, dungeonLevel);
        var reference = rates.ItemReference;
        var gold = rates.GoldReference;
        if (reference.Value0 < 0 || reference.Value1 < 0)
            throw new InvalidDataException("当前等级的物品或金币引用不完整: " + level);
        // 金币数量暂保留原有本服倍率，不将其套用到物品掉落概率。
        decimal multiplier = DifficultyRates[difficulty];
        int Rate(int channel) => rates.Thresholds[channel];
        int minimum = level - reference.Value0, maximum = level + reference.Value1 - 1;
        ImmutableArray<DungeonDropRarityPool> Pools(int channel) => Enumerable.Range(0, 7).Select(rarity =>
            new DungeonDropRarityPool(rules.RarityDecision[(channel - 1) * 7 + rarity],
                candidates.ReferencePool(channel == 2, minimum, maximum, rarity))).ToImmutableArray();
        var trials = new List<DungeonDropTrial>
        {
            new(Rate(0), null, 1, 1, gold, multiplier, ReferenceGold: true, Category: DungeonDropCategory.Gold),
            new(Rate(1), null, 1, 1, RarityPools: Pools(1), Category: DungeonDropCategory.Materials),
            new(Rate(2), null, 1, 1, RarityPools: Pools(2), Category: DungeonDropCategory.Equipment)
                { RarityBonusGroup = DungeonRarityBonusGroup.OrdinaryEquipment },
            new(Rate(3), null, 1, 1, RarityPools: Pools(3), Category: DungeonDropCategory.Materials)
        };
        var declaration = resolveMonster(monsterId);
        if (declaration.Items is { Count: > 0 } rows)
        {
            var explicitItems = rows.Where(row => row.Parameter > 0).Select(row => new DungeonWeightedDropItem(resolveItem(row.ItemId), row.Parameter));
            var pool = new DungeonItemDropPool(explicitItems);
            if (pool.TotalWeight > 0)
                trials.Add(new(DungeonExplicitDropPolicy.TriggerThreshold, pool, 1, 1, Category: DungeonDropCategory.MonsterItems)
                    { RarityBonusGroup = DungeonRarityBonusGroup.MonsterSpecific });
        }
        if (special?.IndependentByMonster.TryGetValue(monsterId, out var independent) == true)
        foreach (var row in independent)
        {
            int basis = dungeonLevel ?? level;
            if (row.EnemyType != 0 || row.ListFlag == 2 || row.Difficulty >= 0 && row.Difficulty != difficulty ||
                row.MinimumLevel > 0 && row.MaximumLevel > 0 && (basis < row.MinimumLevel || basis > row.MaximumLevel))
                continue;
            int attempts = row.Attempts[difficulty], threshold = Math.Min(row.Probabilities[difficulty], 1000000);
            if (attempts == 0 || threshold == 0) continue;
            var source = row.ListFlag == 1 ? row.Items : row.ItemId > 0 ? ImmutableArray.Create(new DungeonSpecialDropItem(row.ItemId, 1)) : [];
            var pool = SpecialPool(source);
            if (pool.TotalWeight > 0)
                trials.Add(new(threshold, pool, 1, 1, ProbabilityDenominator: 1000000, Attempts: attempts,
                    GrowthContractEligible: row.ListFlag == 1 && monsterType == 3)
                    { RarityBonusGroup = row.ListFlag == 1 ? DungeonRarityBonusGroup.IndependentLottery : DungeonRarityBonusGroup.None });
        }
        if (special?.WorldByLevel.TryGetValue(level, out var world) == true)
        {
            var pool = SpecialPool(world.Items);
            if (pool.TotalWeight > 0)
                trials.Add(new(Math.Min(pool.TotalWeight, 100000), pool, 1, 1, ProbabilityDenominator: 100000)
                    { RarityBonusGroup = DungeonRarityBonusGroup.WorldLottery });
        }
        return new(Rule, trials, referenceQuality: true, amplification);

        DungeonItemDropPool SpecialPool(IEnumerable<DungeonSpecialDropItem> source) => new(source
            .Where(row => row.ItemId > 0 && row.Weight > 0).Select(row => new DungeonWeightedDropItem(resolveItem(row.ItemId), row.Weight)));
    }

    public DungeonDropGenerationPlan PrepareTayberrsPreview(DungeonDefinition definition, DungeonPlannedMonster monster)
    {
        if (definition.IsTayberrsStory)
            return new(TayberrsDropPolicy.StoryRule, []);
        if (!definition.IsTayberrs || !monster.IsBoss ||
            monster.MonsterId is not (TayberrsDropPolicy.PartyBossMonsterId or TayberrsDropPolicy.SingleBossMonsterId))
            return new(TayberrsDropPolicy.Rule, []);
        return (tayberrs ?? new TayberrsDropPolicy(new(), resolveItem)).PrepareBossPreview();
    }
}
