using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 正式生产者显式登记适用策略；材料、固定指定物及活动保证奖励默认不接装备掉率。
internal enum DungeonRarityBonusGroup { None, OrdinaryEquipment, HellEquipment, MonsterSpecific, IndependentLottery, WorldLottery, EquipmentLottery }

internal sealed record DungeonDropRarityPool(int Threshold, DungeonItemDropPool Items,
    DungeonItemDropPool? AlternateItems = null, int AlternatePermille = 0,
    DungeonItemDropPool? SecondaryItems = null, int SecondaryPermille = 0)
{
    public IEnumerable<DungeonItemDropPool> CandidatePools => new[] { Items, AlternateItems, SecondaryItems }.OfType<DungeonItemDropPool>();
}
internal sealed record DungeonDropTrial(int Threshold, DungeonItemDropPool? Items,
    int MinimumCount, int MaximumCount, DungeonDropGoldReference? Gold = null, decimal GoldMultiplier = 1m,
    int ProbabilityDenominator = DungeonDropRatePlan.ProbabilityDenominator,
    ImmutableArray<DungeonDropRarityPool> RarityPools = default, bool ReferenceGold = false, int Attempts = 1,
    bool GrowthContractEligible = false, ImmutableDictionary<int, int>? ItemCounts = null, bool IndividualObjects = false,
    DungeonItemDropPool? EpicReplacementItems = null, DungeonDropCategory Category = DungeonDropCategory.Special,
    ImmutableDictionary<int, int>? ItemStackSizes = null,
    ImmutableHashSet<int>? DisjointForbiddenRarities = null, bool EpicRoad2019Simulated = false,
    DungeonEpicDropAdjustment? EpicAdjustment = null)
{
    public DungeonRarityBonusGroup RarityBonusGroup { get; init; }
    public DungeonEquipmentRarityAdjustment? EquipmentRarityAdjustment { get; init; }
    public bool IndependentDrop { get; init; }
    public DungeonBossDropAdjustment? BossDropAdjustment { get; init; }
    // 只登记旧地区普通密门深渊的额外装备骰，不扩大到同一怪物的其它掉落。
    public bool ExcludeLegacyHellLowEquipment { get; init; }
    // 仅正式深渊碎片骰登记。入场预留上界，死亡时才把本人冻结增量变为实际次数。
    public int EpicPieceBonusCapacity { get; init; }
    public DungeonDropTrial WithChanceBonus(long numerator, long denominator) => this with
    {
        Threshold = (int)Math.Min(ProbabilityDenominator, (long)Threshold * numerator / denominator),
        EpicAdjustment = EpicAdjustment is { } adjustment
            ? adjustment with { EpicThreshold = adjustment.EpicThreshold * numerator / denominator } : null
    };
}

// 上游必须提供已解释的概率、数量与池；这里只按完整声明执行，不为未知资源列补类别。
// 准备计划是不可变规则输入，不持有在线角色；原Run负责冻结实际随机结果。
internal sealed class DungeonDropGenerationPlan
{
    public string ResourceSource { get; }
    public ImmutableArray<DungeonDropTrial> Trials { get; }
    public int MaximumObjects { get; }
    public bool ReferenceQuality { get; }
    public int? OrdinaryLukeDailySource { get; init; }
    public bool ExcludeLowEquipment { get; init; }
    public bool ExcludeLowMaterials { get; init; }
    public DungeonDropMultipliers? DropMultipliers { get; init; }
    public EquipmentAmplificationDropPolicy? Amplification { get; }
    public EvildomWarSarpozaDropPlan? Sarpoza { get; }
    public bool AcceptsEpicPieceBonus => Trials.Any(trial => trial.EpicPieceBonusCapacity > 0);

    public DungeonDropGenerationPlan WithEpicPieceBonus(int count)
    {
        if (count < 0) throw new InvalidDataException("额外史诗碎片数量不能为负数");
        if (count == 0 || !AcceptsEpicPieceBonus) return this;
        return new(ResourceSource, Trials.Select(trial =>
        {
            if (trial.EpicPieceBonusCapacity == 0) return trial;
            if (count > trial.EpicPieceBonusCapacity) throw new InvalidDataException("额外史诗碎片超过本次入场预留");
            return trial with { Attempts = checked(trial.Attempts + count), EpicPieceBonusCapacity = 0 };
        }), ReferenceQuality, Amplification, Sarpoza)
        {
            OrdinaryLukeDailySource = OrdinaryLukeDailySource, ExcludeLowEquipment = ExcludeLowEquipment,
            ExcludeLowMaterials = ExcludeLowMaterials, DropMultipliers = DropMultipliers
        };
    }
    public DungeonDropGenerationPlan WithEquipmentRarityBonus(EquipmentRarityBonus bonus)
    {
        if (bonus.IsEmpty) return this;
        if (bonus.RarePercent < 0 || bonus.UniquePercent < 0) throw new InvalidDataException("装备稀有度加成不能为负");
        bool changed = false;
        var trials = Trials.Select(trial =>
        {
            if (trial.RarityBonusGroup is DungeonRarityBonusGroup.OrdinaryEquipment or DungeonRarityBonusGroup.HellEquipment)
            {
                var pools = trial.RarityPools;
                if (pools.IsDefaultOrEmpty || pools.Length != 7 || pools[^1].Threshold < DungeonDropRatePlan.RarityDenominator) return trial;
                bool Available(int rarity) => pools[rarity].CandidatePools.Any(pool => pool.TotalWeight > 0) &&
                    pools[rarity].CandidatePools.SelectMany(pool => pool.Entries).All(row => row.Template.Kind == ItemKind.Equipment && row.Template.Rarity == rarity);
                var previous = pools.Select(pool => pool.Threshold).ToImmutableArray();
                var thresholds = EquipmentRarityDistribution.Adjust(previous, bonus, Available(2), Available(3));
                if (previous.SequenceEqual(thresholds)) return trial;
                changed = true;
                return trial with { RarityPools = pools.Select((pool, i) => pool with { Threshold = thresholds[i] }).ToImmutableArray() };
            }
            if (trial.RarityBonusGroup is not (DungeonRarityBonusGroup.MonsterSpecific or DungeonRarityBonusGroup.IndependentLottery or
                DungeonRarityBonusGroup.WorldLottery or DungeonRarityBonusGroup.EquipmentLottery) || trial.Threshold == 0 ||
                trial.Items is not { TotalWeight: > 0 } direct || !direct.Entries.Any(row => row.Template.Kind == ItemKind.Equipment &&
                    (row.Template.Rarity == 2 && bonus.RarePercent > 0 || row.Template.Rarity == 3 && bonus.UniquePercent > 0))) return trial;
            changed = true;
            return trial with { EquipmentRarityAdjustment = new(bonus) };
        }).ToImmutableArray();
        return changed ? new(ResourceSource, trials, ReferenceQuality, Amplification, Sarpoza)
        {
            OrdinaryLukeDailySource = OrdinaryLukeDailySource, ExcludeLowEquipment = ExcludeLowEquipment,
            ExcludeLowMaterials = ExcludeLowMaterials, DropMultipliers = DropMultipliers
        } : this;
    }
    public DungeonDropGenerationPlan WithDisjointRestrictions(ImmutableHashSet<int> rarities)
    {
        if (rarities.IsEmpty) return this;
        return new(ResourceSource, Trials.Select(trial => trial with
        {
            DisjointForbiddenRarities = (trial.DisjointForbiddenRarities ?? ImmutableHashSet<int>.Empty).Union(rarities)
        }), ReferenceQuality, Amplification, Sarpoza) { OrdinaryLukeDailySource = OrdinaryLukeDailySource,
            ExcludeLowEquipment = ExcludeLowEquipment, ExcludeLowMaterials = ExcludeLowMaterials, DropMultipliers = DropMultipliers };
    }
    // 本服固定史诗药剂策略：只扩展完整稀有度骰的史诗区间，不增加装备触发或抽样次数。
    // 掉落Buff统一使用百分比；scale只供其他明确比例调用，小灯笼限定正式深渊结算装备来源。
    public DungeonDropGenerationPlan WithEpicDropBonus(int added, int scale = 100, bool hellRewardOnly = false, bool includeDirectEpic = false)
    {
        if (added < 0) throw new InvalidDataException("史诗掉落加成不能为负");
        if (scale <= 0) throw new InvalidDataException("史诗掉落加成分母必须为正");
        if (added == 0) return this;
        bool changed = false;
        var trials = Trials.Select(trial =>
        {
            if (hellRewardOnly && trial.RarityBonusGroup != DungeonRarityBonusGroup.HellEquipment) return trial;
            if (includeDirectEpic && trial.Category == DungeonDropCategory.Equipment && trial.Items is { TotalWeight: > 0 } direct &&
                direct.Entries.Any(row => DungeonEpicDropAdjustment.IsEpic(row.Template)))
            {
                if (trial.EpicAdjustment is null && direct.Entries.All(row => DungeonEpicDropAdjustment.IsEpic(row.Template)))
                {
                    var directAdjustment = trial.WithChanceBonus((long)scale + added, scale);
                    if (directAdjustment.Threshold != trial.Threshold) changed = true;
                    return directAdjustment;
                }
                decimal basis = trial.EpicAdjustment?.EpicThreshold ?? trial.Threshold;
                decimal enhancedDirect = basis * ((decimal)scale + added) / scale;
                if (enhancedDirect != basis) changed = true;
                return trial with { EpicAdjustment = new(enhancedDirect) };
            }
            var pools = trial.RarityPools;
            if (pools.IsDefaultOrEmpty || pools.Length != 7 || pools[^1].Threshold != DungeonDropRatePlan.RarityDenominator ||
                !pools[4].CandidatePools.Any(pool => pool.TotalWeight > 0) ||
                pools[4].CandidatePools.SelectMany(pool => pool.Entries).Any(row =>
                    row.Template is not { Kind: ItemKind.Equipment, Rarity: 4 })) return trial;
            int denominator = DungeonDropRatePlan.RarityDenominator;
            int original = pools[4].Threshold - pools[3].Threshold;
            int enhanced = (int)Math.Min(denominator, (long)original * ((long)scale + added) / scale);
            if (enhanced == original) return trial;
            var adjusted = ImmutableArray.CreateBuilder<DungeonDropRarityPool>(7);
            long nonEpic = 0;
            int previous = 0;
            for (int index = 0; index < pools.Length; index++)
            {
                int width = pools[index].Threshold - previous;
                previous = pools[index].Threshold;
                if (index != 4) nonEpic += width;
                int cumulative = checked((int)(nonEpic * (denominator - enhanced) / (denominator - original)));
                if (index >= 4) cumulative += enhanced;
                adjusted.Add(pools[index] with { Threshold = cumulative });
            }
            changed = true;
            return trial with { RarityPools = adjusted.ToImmutable() };
        }).ToImmutableArray();
        return changed ? new(ResourceSource, trials, ReferenceQuality, Amplification, Sarpoza) { OrdinaryLukeDailySource = OrdinaryLukeDailySource, ExcludeLowEquipment = ExcludeLowEquipment, ExcludeLowMaterials = ExcludeLowMaterials, DropMultipliers = DropMultipliers } : this;
    }

    public DungeonDropGenerationPlan WithGrowthContract(int percent)
    {
        if (percent < 0) throw new InvalidDataException("成长契约掉落加成不能为负");
        if (percent == 0 || !Trials.Any(trial => trial.GrowthContractEligible)) return this;
        return new(ResourceSource, Trials.Select(trial => !trial.GrowthContractEligible ? trial :
            trial.WithChanceBonus(100L + percent, 100)), ReferenceQuality, Amplification, Sarpoza) { OrdinaryLukeDailySource = OrdinaryLukeDailySource, ExcludeLowEquipment = ExcludeLowEquipment, ExcludeLowMaterials = ExcludeLowMaterials, DropMultipliers = DropMultipliers };
    }

    public DungeonDropGenerationPlan WithBossDropBonus(int percent)
    {
        if (percent < 0) throw new InvalidDataException("领主掉落加成不能为负");
        if (percent == 0) return this;
        bool changed = false;
        var trials = Trials.Select(trial =>
        {
            if (trial.IndependentDrop)
            {
                // 独立混池仍只选一项；只有装备可以占用空抽空间，材料等维持原绝对概率。
                if (trial.Items is not { TotalWeight: > 0 } pool || !pool.Entries.Any(row => row.Template.Kind == ItemKind.Equipment))
                    return trial;
                var adjustment = new DungeonBossDropAdjustment(percent);
                if (adjustment.Widths(trial).SequenceEqual(new DungeonBossDropAdjustment(0).Widths(trial))) return trial;
                changed = true;
                return trial with { BossDropAdjustment = adjustment };
            }
            var adjusted = trial.WithChanceBonus(100L + percent, 100);
            if (adjusted.Threshold == trial.Threshold && adjusted.EpicAdjustment == trial.EpicAdjustment) return trial;
            changed = true;
            return adjusted;
        }).ToImmutableArray();
        return changed ? new(ResourceSource, trials, ReferenceQuality, Amplification, Sarpoza)
        {
            OrdinaryLukeDailySource = OrdinaryLukeDailySource, ExcludeLowEquipment = ExcludeLowEquipment,
            ExcludeLowMaterials = ExcludeLowMaterials, DropMultipliers = DropMultipliers
        } : this;
    }

    public DungeonDropGenerationPlan WithAntoniumDropBonus(int basisPoints)
    {
        if (basisPoints < 0) throw new InvalidDataException("安徒恩魔能石掉落加成不能为负");
        if (basisPoints == 0 || !Trials.Any(trial => trial.Items?.Entries.Any(
            row => row.Template.Id == Adventure.AdventureBenefitPolicy.AntoniumItemId) == true))
            return this;
        return new(ResourceSource, Trials.Select(trial =>
            trial.Items?.Entries.Any(row => row.Template.Id == Adventure.AdventureBenefitPolicy.AntoniumItemId) != true
                ? trial
                : trial.WithChanceBonus(10000L + basisPoints, 10000)), ReferenceQuality, Amplification, Sarpoza)
        {
            OrdinaryLukeDailySource = OrdinaryLukeDailySource,
            ExcludeLowEquipment = ExcludeLowEquipment,
            ExcludeLowMaterials = ExcludeLowMaterials,
            DropMultipliers = DropMultipliers
        };
    }

    public DungeonDropGenerationPlan(string resourceSource, IEnumerable<DungeonDropTrial> trials, bool referenceQuality = false,
        EquipmentAmplificationDropPolicy? amplification = null, EvildomWarSarpozaDropPlan? sarpoza = null)
    {
        if (string.IsNullOrWhiteSpace(resourceSource))
            throw new InvalidDataException("掉落生成计划必须有明确来源");
        ResourceSource = resourceSource;
        ReferenceQuality = referenceQuality;
        Amplification = amplification;
        Sarpoza = sarpoza;
        Trials = trials.ToImmutableArray();
        int maximum = sarpoza?.MaximumObjects ?? 0;
        foreach (var trial in Trials)
        {
            int sources = (trial.Items is not null ? 1 : 0) + (trial.Gold is not null ? 1 : 0) + (!trial.RarityPools.IsDefaultOrEmpty ? 1 : 0);
            if (trial.ProbabilityDenominator <= 0 || trial.Threshold < 0 || trial.Threshold > trial.ProbabilityDenominator ||
                trial.MinimumCount <= 0 || trial.MaximumCount < trial.MinimumCount ||
                trial.DisjointForbiddenRarities?.Any(rarity => rarity is < 0 or > 6) == true ||
                sources != 1 || trial.GoldMultiplier <= 0 || trial.ReferenceGold && trial.Gold is null || trial.Attempts is < 1 or > ushort.MaxValue)
                throw new InvalidDataException("掉落生成计划的概率、数量或物品/金币来源不完整");
            if (trial.EpicAdjustment is { } adjustment && (trial.Items is null || trial.Items.TotalWeight <= 0 ||
                adjustment.EpicThreshold < 0 || !trial.Items.Entries.Any(row => DungeonEpicDropAdjustment.IsEpic(row.Template))))
                throw new InvalidDataException("史诗混池倍率须绑定原直接候选及非负概率");
            if (trial.EpicPieceBonusCapacity < 0 || trial.EpicPieceBonusCapacity > 0 &&
                (trial.Items is not { TotalWeight: > 0 } || trial.MinimumCount != 1 || trial.MaximumCount != 1 ||
                 trial.ItemCounts is not null || trial.Items.Entries.Any(row => row.Template.Kind != ItemKind.Material)))
                throw new InvalidDataException("额外史诗碎片须绑定单片材料候选及非负预留");
            if (trial.EquipmentRarityAdjustment is { } rarityAdjustment && (trial.Items is not { TotalWeight: > 0 } ||
                rarityAdjustment.Bonus.RarePercent < 0 || rarityAdjustment.Bonus.UniquePercent < 0 ||
                trial.RarityBonusGroup is not (DungeonRarityBonusGroup.MonsterSpecific or DungeonRarityBonusGroup.IndependentLottery or
                    DungeonRarityBonusGroup.WorldLottery or DungeonRarityBonusGroup.EquipmentLottery)))
                throw new InvalidDataException("装备混池加成须绑定明确的随机策略及非负加成");
            if (trial.BossDropAdjustment is { } bossAdjustment && (!trial.IndependentDrop ||
                trial.Items is not { TotalWeight: > 0 } bossItems || bossAdjustment.Percent < 0 ||
                !bossItems.Entries.Any(row => row.Template.Kind == ItemKind.Equipment)))
                throw new InvalidDataException("领主独立掉落加成须绑定包含装备的原独立池及非负百分比");
            var pools = trial.Items is { } direct ? new[] { direct } : trial.RarityPools.IsDefaultOrEmpty ?
                Array.Empty<DungeonItemDropPool>() : trial.RarityPools.SelectMany(row => row.CandidatePools).ToArray();
            if (!trial.RarityPools.IsDefaultOrEmpty && trial.RarityPools.Any(row => row.AlternatePermille is < 0 or > 1000 ||
                row.AlternateItems is null && row.AlternatePermille != 0 ||
                row.AlternateItems is { TotalWeight: <= 0 } || row.SecondaryPermille is < 0 or > 1000 ||
                row.SecondaryItems is null && row.SecondaryPermille != 0 || row.SecondaryItems is { TotalWeight: <= 0 } ||
                row.AlternatePermille + row.SecondaryPermille > 1000))
                throw new InvalidDataException("稀有度条件分支须提供非空候选及0至1000的明确概率");
            // 同一次互斥抽样可以为不同物品声明不同数量，不拆成两个独立概率导致同时掉落。
            if (trial.ItemCounts is { } counts && (trial.Items is null || trial.MinimumCount != 1 || trial.MaximumCount != 1 ||
                counts.Values.Any(count => count <= 0) ||
                !counts.Keys.Order().SequenceEqual(trial.Items.Entries.Select(entry => entry.Template.Id).Distinct().Order())))
                throw new InvalidDataException("物品分支数量须完整匹配直接候选池，并使用固定单次选择");
            var sourcePools = pools;
            if (trial.EpicReplacementItems is { } replacement)
            {
                if (replacement.TotalWeight <= 0 || replacement.Entries.Any(row =>
                    row.Template.Kind != GameWorld.Items.ItemKind.Equipment || row.Template.Rarity != 4))
                    throw new InvalidDataException("史诗替换池须为非空史诗装备候选");
                pools = pools.Append(replacement).ToArray();
            }
            // 任一候选未支持新建时拒绝整份计划；不删候选、更改概率或延后至死亡中断。
            if (pools.Any(pool => pool.Entries.Any(entry => !entry.Template.Expiration.SupportsCreation)))
                throw new NotSupportedException("掉落候选包含尚未支持的新实例期限声明");
            if (pools.Any(pool => pool.TotalWeight < 0 || pool.Entries.Any(entry =>
                !DungeonDropCreation.Supports(entry.Template.Kind) || entry.Template.StackLimit <= 0)))
                throw new InvalidDataException("掉落生成计划须有完整普通资源候选池: " + resourceSource + "; " +
                    string.Join(",", pools.SelectMany(pool => pool.Entries).Where(entry =>
                        !DungeonDropCreation.Supports(entry.Template.Kind) || entry.Template.StackLimit <= 0)
                        .Select(entry => $"item={entry.Template.Id},kind={entry.Template.Kind},stack={entry.Template.StackLimit}")));
            if (!trial.RarityPools.IsDefaultOrEmpty && (trial.RarityPools.Any(row => row.Threshold < 0) ||
                !trial.RarityPools.Select(row => row.Threshold).SequenceEqual(trial.RarityPools.Select(row => row.Threshold).Order()) ||
                trial.RarityPools[^1].Threshold < DungeonDropRatePlan.RarityDenominator))
                throw new InvalidDataException("稀有度选择须完整覆盖原抽样并保留阈值顺序");
            if (trial.Gold is { } gold && (gold.BaseAmount <= 0 || gold.VariancePercent is < 0 or > 100))
                throw new InvalidDataException("金币生成计划的基值或波动范围无效");
            long objects = trial.Gold is not null ? trial.MaximumCount : sourcePools.SelectMany(pool => pool.Entries).Select(entry =>
            {
                int count = trial.ItemCounts?.GetValueOrDefault(entry.Template.Id) ?? trial.MaximumCount;
                int stackSize = trial.ItemStackSizes is { } sizes &&
                    sizes.TryGetValue(entry.Template.Id, out int configured) && configured > 0
                    ? configured : entry.Template.StackLimit;
                if (stackSize <= 0) throw new InvalidDataException("掉落堆叠大小必须为正数");
                return entry.Template.Kind == ItemKind.Equipment || trial.IndividualObjects ? count :
                    ((long)count + stackSize - 1) / stackSize;
            }).DefaultIfEmpty(0).Max();
            if (trial.EpicReplacementItems is not null)
            {
                long replacementObjects = sourcePools.SelectMany(pool => pool.Entries)
                    .Where(row => row.Template is { Kind: GameWorld.Items.ItemKind.Equipment, Rarity: 4 })
                    .Select(row => (long)(trial.ItemCounts?.GetValueOrDefault(row.Template.Id) ?? trial.MaximumCount))
                    .DefaultIfEmpty(0).Max();
                objects = Math.Max(objects, replacementObjects);
            }
            objects *= (long)trial.Attempts + trial.EpicPieceBonusCapacity;
            if (objects > 500 - maximum)
                throw new InvalidDataException("完整掉落生成计划超过本版通知容量，不能截断来源");
            maximum += checked((int)objects);
        }
        if (maximum > 500)
            throw new InvalidDataException("完整掉落生成计划超过本版通知容量，不能截断来源");
        MaximumObjects = maximum;
    }

    public ImmutableArray<DungeonGroundContent> Generate(Func<int, int> draw, DateTimeOffset now, bool epicRoad2019Available = true)
    {
        var result = ImmutableArray.CreateBuilder<DungeonGroundContent>();
        foreach (var trial in Trials)
        for (int attempt = 0; attempt < trial.Attempts; attempt++)
        {
            if (!epicRoad2019Available && trial.EpicRoad2019Simulated) continue;
            // 参考服先计算金币波动，再判定金币概率；即使未命中也保留这一抽样次序。
            int? preparedGold = trial.ReferenceGold ? GoldAmount(trial) : null;
            ItemTemplate? adjustedTemplate = null;
            if (trial.BossDropAdjustment is { } bossAdjustment)
            {
                adjustedTemplate = bossAdjustment.Select(trial, Sample);
                if (adjustedTemplate is null) continue;
            }
            else if (trial.EquipmentRarityAdjustment is { } equipmentAdjustment)
            {
                adjustedTemplate = equipmentAdjustment.Select(trial, Sample);
                if (adjustedTemplate is null) continue;
            }
            else if (trial.EpicAdjustment is { } adjustment)
            {
                adjustedTemplate = adjustment.Select(trial, Sample);
                if (adjustedTemplate is null) continue;
            }
            else if (Sample(trial.ProbabilityDenominator) >= trial.Threshold)
                continue;
            int count = trial.MinimumCount == trial.MaximumCount ? trial.MinimumCount :
                trial.MinimumCount + Sample(trial.MaximumCount - trial.MinimumCount + 1);
            if (trial.Gold is { } gold)
            {
                int amount = preparedGold ?? GoldAmount(trial);
                if (amount <= 0)
                    throw new InvalidDataException("已命中金币计划产生非正金额");
                for (int index = 0; index < count; index++)
                    result.Add(new(null, amount));
                continue;
            }
            var pool = trial.Items;
            if (!trial.RarityPools.IsDefaultOrEmpty)
            {
                int rarityRoll = Sample(DungeonDropRatePlan.RarityDenominator) + 1;
                var rarity = trial.RarityPools.First(row => rarityRoll <= row.Threshold);
                // 三个互斥分支共用一次抽样，不新增装备骰，也不改变各池内部权重。
                int branch = rarity.AlternatePermille + rarity.SecondaryPermille > 0 ? Sample(1000) : 1000;
                pool = branch < rarity.AlternatePermille ? rarity.AlternateItems :
                    branch < rarity.AlternatePermille + rarity.SecondaryPermille ? rarity.SecondaryItems : rarity.Items;
            }
            // 明确空候选池不会制造另一类别；参考回退已在资源准备阶段处理。
            if (adjustedTemplate is null && pool!.TotalWeight == 0)
                continue;
            var template = adjustedTemplate ?? pool!.Select(Sample(pool.TotalWeight));
            if (trial.ExcludeLegacyHellLowEquipment && template.Kind == ItemKind.Equipment &&
                (template.Rarity is 0 or 1 || template.MagicSealedEquipment))
                continue;
            // 禁用的品质只冻结为空；不重抽、不把垃圾转为史诗，也不改变其它候选的中奖概率。
            if (ExcludeLowEquipment && template is { Kind: ItemKind.Equipment, Rarity: >= 0 and <= 3 } ||
                ExcludeLowMaterials && template is { Kind: ItemKind.Material, Rarity: 0 or 1 })
                continue;
            // 保留原池权重：过期活动物品命中只冻结为空，不重抽、不续期，也不阻断其它奖励及死亡提交。
            int expiresAt = template.Expiration.Resolve(now);
            if (expiresAt > 0 && expiresAt <= now.ToUnixTimeSeconds())
                continue;
            if (trial.ItemCounts is { } counts) count = counts[template.Id];
            // 保留原概率、品质、混合池分支及数量；只有未过期的史诗装备命中才进入专属替换池。
            if (template is { Kind: GameWorld.Items.ItemKind.Equipment, Rarity: 4 } && trial.EpicReplacementItems is { } replacement)
            {
                template = replacement.Select(Sample(replacement.TotalWeight));
                expiresAt = template.Expiration.Resolve(now);
                if (expiresAt > 0 && expiresAt <= now.ToUnixTimeSeconds())
                    continue;
            }
            if (template.Kind == ItemKind.Equipment)
            {
                for (int index = 0; index < count; index++)
                {
                    var core = DungeonDropCreation.Create(template, 1, Sample, now, ReferenceQuality, Amplification);
                    if (trial.DisjointForbiddenRarities?.Contains(template.Rarity) == true)
                        core = core with { DisjointForbidden = true };
                    result.Add(new(core));
                }
            }
            else
            {
                int remaining = count;
                while (remaining > 0)
                {
                    int stackSize = trial.ItemStackSizes is { } sizes &&
                        sizes.TryGetValue(template.Id, out int configured) && configured > 0
                        ? configured : template.StackLimit;
                    if (stackSize <= 0) throw new InvalidDataException("掉落堆叠大小必须为正数");
                    int quantity = Math.Min(remaining, trial.IndividualObjects ? 1 : stackSize);
                    result.Add(new(DungeonDropCreation.Create(template, quantity, Sample, now)));
                    remaining -= quantity;
                }
            }
        }
        if (Sarpoza is not null)
            result.AddRange(Sarpoza.Generate(Sample, now));
        return epicRoad2019Available ? result.ToImmutable() : result.Where(content =>
            content.Item is null || !EpicRoad2019ItemLifetime.Includes(content.Item.ItemId)).ToImmutableArray();

        int GoldAmount(DungeonDropTrial trial)
        {
            var gold = trial.Gold!.Value;
            int variation = gold.VariancePercent == 0 ? 0 : Sample(2 * gold.VariancePercent + 1) - gold.VariancePercent;
            long change = (long)variation * gold.BaseAmount / 100;
            return trial.ReferenceGold ? Math.Max(1, checked((int)Math.Floor((gold.BaseAmount + change) * (double)trial.GoldMultiplier))) :
                checked((int)decimal.Floor((gold.BaseAmount + change) * trial.GoldMultiplier));
        }

        int Sample(int bound)
        {
            int value = draw(bound);
            if (value < 0 || value >= bound)
                throw new InvalidDataException("掉落抽样超出声明区间，不能取模或自动重抽");
            return value;
        }
    }
}
