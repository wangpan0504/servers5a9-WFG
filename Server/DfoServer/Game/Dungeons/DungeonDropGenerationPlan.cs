using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonDropRarityPool(int Threshold, DungeonItemDropPool Items);
internal sealed record DungeonDropTrial(int Threshold, DungeonItemDropPool? Items,
    int MinimumCount, int MaximumCount, DungeonDropGoldReference? Gold = null, decimal GoldMultiplier = 1m,
    int ProbabilityDenominator = DungeonDropRatePlan.ProbabilityDenominator,
    ImmutableArray<DungeonDropRarityPool> RarityPools = default, bool ReferenceGold = false, int Attempts = 1,
    bool GrowthContractEligible = false, ImmutableDictionary<int, int>? ItemCounts = null);

// 上游必须提供已解释的概率、数量与池；这里只按完整声明执行，不为未知资源列补类别。
// 准备计划是不可变规则输入，不持有在线角色；原Run负责冻结实际随机结果。
internal sealed class DungeonDropGenerationPlan
{
    public string ResourceSource { get; }
    public ImmutableArray<DungeonDropTrial> Trials { get; }
    public int MaximumObjects { get; }
    public bool ReferenceQuality { get; }
    public EquipmentAmplificationDropPolicy? Amplification { get; }
    public DungeonDropGenerationPlan WithGrowthContract(int percent)
    {
        if (percent < 0) throw new InvalidDataException("成长契约掉落加成不能为负");
        if (percent == 0 || !Trials.Any(trial => trial.GrowthContractEligible)) return this;
        return new(ResourceSource, Trials.Select(trial => !trial.GrowthContractEligible ? trial : trial with
        { Threshold = (int)Math.Min(trial.ProbabilityDenominator, (long)trial.Threshold * (100L + percent) / 100) }), ReferenceQuality, Amplification);
    }

    public DungeonDropGenerationPlan(string resourceSource, IEnumerable<DungeonDropTrial> trials, bool referenceQuality = false,
        EquipmentAmplificationDropPolicy? amplification = null)
    {
        if (string.IsNullOrWhiteSpace(resourceSource))
            throw new InvalidDataException("掉落生成计划必须有明确来源");
        ResourceSource = resourceSource;
        ReferenceQuality = referenceQuality;
        Amplification = amplification;
        Trials = trials.ToImmutableArray();
        int maximum = 0;
        foreach (var trial in Trials)
        {
            int sources = (trial.Items is not null ? 1 : 0) + (trial.Gold is not null ? 1 : 0) + (!trial.RarityPools.IsDefaultOrEmpty ? 1 : 0);
            if (trial.ProbabilityDenominator <= 0 || trial.Threshold < 0 || trial.Threshold > trial.ProbabilityDenominator ||
                trial.MinimumCount <= 0 || trial.MaximumCount < trial.MinimumCount ||
                sources != 1 || trial.GoldMultiplier <= 0 || trial.ReferenceGold && trial.Gold is null || trial.Attempts is < 1 or > ushort.MaxValue)
                throw new InvalidDataException("掉落生成计划的概率、数量或物品/金币来源不完整");
            var pools = trial.Items is { } direct ? new[] { direct } : trial.RarityPools.IsDefaultOrEmpty ?
                Array.Empty<DungeonItemDropPool>() : trial.RarityPools.Select(row => row.Items).ToArray();
            // 同一次互斥抽样可以为不同物品声明不同数量，不拆成两个独立概率导致同时掉落。
            if (trial.ItemCounts is { } counts && (trial.Items is null || trial.MinimumCount != 1 || trial.MaximumCount != 1 ||
                counts.Values.Any(count => count <= 0) ||
                !counts.Keys.Order().SequenceEqual(trial.Items.Entries.Select(entry => entry.Template.Id).Distinct().Order())))
                throw new InvalidDataException("物品分支数量须完整匹配直接候选池，并使用固定单次选择");
            // 任一候选未支持新建时拒绝整份计划；不删候选、更改概率或延后至死亡中断。
            if (pools.Any(pool => pool.Entries.Any(entry => !entry.Template.Expiration.SupportsCreation)))
                throw new NotSupportedException("掉落候选包含尚未支持的新实例期限声明");
            if (pools.Any(pool => pool.TotalWeight < 0 || pool.Entries.Any(entry =>
                !DungeonDropCreation.Supports(entry.Template.Kind) || entry.Template.StackLimit <= 0)))
                throw new InvalidDataException("掉落生成计划须有完整普通资源候选池");
            if (!trial.RarityPools.IsDefaultOrEmpty && (trial.RarityPools.Any(row => row.Threshold < 0) ||
                !trial.RarityPools.Select(row => row.Threshold).SequenceEqual(trial.RarityPools.Select(row => row.Threshold).Order()) ||
                trial.RarityPools[^1].Threshold < DungeonDropRatePlan.RarityDenominator))
                throw new InvalidDataException("稀有度选择须完整覆盖原抽样并保留阈值顺序");
            if (trial.Gold is { } gold && (gold.BaseAmount <= 0 || gold.VariancePercent is < 0 or > 100))
                throw new InvalidDataException("金币生成计划的基值或波动范围无效");
            long objects = trial.Gold is not null ? trial.MaximumCount : pools.SelectMany(pool => pool.Entries).Select(entry =>
            {
                int count = trial.ItemCounts?.GetValueOrDefault(entry.Template.Id) ?? trial.MaximumCount;
                return entry.Template.Kind == ItemKind.Equipment ? count :
                    ((long)count + entry.Template.StackLimit - 1) / entry.Template.StackLimit;
            }).DefaultIfEmpty(0).Max();
            objects *= trial.Attempts;
            if (objects > 500 - maximum)
                throw new InvalidDataException("完整掉落生成计划超过本版通知容量，不能截断来源");
            maximum += checked((int)objects);
        }
        if (maximum > 500)
            throw new InvalidDataException("完整掉落生成计划超过本版通知容量，不能截断来源");
        MaximumObjects = maximum;
    }

    public ImmutableArray<DungeonGroundContent> Generate(Func<int, int> draw, DateTimeOffset now)
    {
        var result = ImmutableArray.CreateBuilder<DungeonGroundContent>();
        foreach (var trial in Trials)
        for (int attempt = 0; attempt < trial.Attempts; attempt++)
        {
            // 参考服先计算金币波动，再判定金币概率；即使未命中也保留这一抽样次序。
            int? preparedGold = trial.ReferenceGold ? GoldAmount(trial) : null;
            if (Sample(trial.ProbabilityDenominator) >= trial.Threshold)
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
                pool = trial.RarityPools.First(row => rarityRoll <= row.Threshold).Items;
            }
            // 明确空候选池不会制造另一类别；参考回退已在资源准备阶段处理。
            if (pool!.TotalWeight == 0)
                continue;
            var template = pool.Select(Sample(pool.TotalWeight));
            if (trial.ItemCounts is { } counts) count = counts[template.Id];
            if (template.Kind == ItemKind.Equipment)
            {
                for (int index = 0; index < count; index++)
                    result.Add(new(DungeonDropCreation.Create(template, 1, Sample, now, ReferenceQuality, Amplification)));
            }
            else
            {
                int remaining = count;
                while (remaining > 0)
                {
                    int quantity = Math.Min(remaining, template.StackLimit);
                    result.Add(new(DungeonDropCreation.Create(template, quantity, Sample, now)));
                    remaining -= quantity;
                }
            }
        }
        return result.ToImmutable();

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
