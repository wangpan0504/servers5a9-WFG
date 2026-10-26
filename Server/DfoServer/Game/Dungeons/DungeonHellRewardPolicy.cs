using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 深渊派对最后一波末位怪的 hellparty 装备奖励。前面的恶魔波次不带这组骰。按 [difficulty] 档位的 RewardRollCount
// （A=8/B=6/C=4/D=0）投骰，每骰命中概率 (rate+1)/1001，稀有度按档位行、装备等级区间按 item drop ref table。
// 这里把它作为额外的掉落试验（Trial）追加到该怪的普通掉落计划上，经现有掉落/地面流水线发放。
// 碎片按配表关联装备的等级筛选；数量沿用本服难度规则，哈林材料保持独立入口。
internal sealed class DungeonHellRewardPolicy(DungeonHellDropRules rules, DungeonItemDropCatalog candidates,
    EpicPieceDropCatalog? epicFragments = null, int epicFragmentPermille = 1000)
{
    public const int Denominator = 1001;


    public static int EpicFragmentRolls(byte mode, byte dungeonDifficulty) => (mode == 1 ? 1 : 0) + new[] { 5, 6, 8, 9, 10 }[Math.Min((int)dungeonDifficulty, 4)];

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, int basisLevel, byte mode, int rolls,
        byte dungeonDifficulty = 0, bool suppressEpicFragments = false, DungeonItemDropPool? epicEquipment = null)
    {
        if (rolls <= 0 || rules.Rate(basisLevel, mode) is not { } rate || rules.Rarity(mode) is not { } rarity ||
            rules.GradeRange(basisLevel) is not { } range) return plan;
        var pools = Enumerable.Range(0, 7).Select(index => new DungeonDropRarityPool(rarity[index],
            index == 4 && epicEquipment is not null ? epicEquipment :
                candidates.ReferencePool(true, range.Minimum, range.Maximum, index))).ToImmutableArray();
        var trial = new DungeonDropTrial(Math.Clamp(rate + 1, 0, Denominator), null, 1, 1, ProbabilityDenominator: Denominator,
            RarityPools: pools, Attempts: rolls);
        var trials = plan.Trials.Append(trial);
        // PVF没有逐骰概率；本服默认每骰产一片，保留显式0关闭及千分比调试，不把creation rate当掉率。
        // 候选权重来自碎片模板，等级区间来自本次深渊资源引用；数量不冒充原服服务端公式。
        if (!suppressEpicFragments && epicFragments is not null && epicFragmentPermille > 0)
        {
            var fragments = epicFragments.Pool(range.Minimum, range.Maximum);
            if (fragments.TotalWeight > 0)
                trials = trials.Append(new DungeonDropTrial(epicFragmentPermille, fragments, 1, 1, ProbabilityDenominator: 1000,
                    Attempts: EpicFragmentRolls(mode, dungeonDifficulty)));
        }
        return new(epicEquipment is null ? plan.ResourceSource : plan.ResourceSource + ";" + HarlemHellEpicDropPolicy.Rule,
            trials, plan.ReferenceQuality, plan.Amplification);
    }
}
