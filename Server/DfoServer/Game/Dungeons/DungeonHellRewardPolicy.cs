using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 深渊派对最后一波末位怪的 hellparty 装备奖励。前面的恶魔波次不带这组骰。按 [difficulty] 档位的 RewardRollCount
// （A=8/B=6/C=4/D=0）投骰，每骰命中概率 (rate+1)/1001，稀有度按档位行、装备等级区间按 item drop ref table。
// 这里把它作为额外的掉落试验（Trial）追加到该怪的普通掉落计划上，经现有掉落/地面流水线发放。
// 旧史诗碎片仍为默认关闭的实验开关；哈林材料由独立本服规则准备，沿同一地面流水线提交。
internal sealed class DungeonHellRewardPolicy(DungeonHellDropRules rules, DungeonItemDropCatalog candidates, DungeonItemDropPool? epicFragments = null, int epicFragmentPermille = 0)
{
    public const int Denominator = 1001;


    public static int EpicFragmentRolls(byte mode, byte dungeonDifficulty) => (mode == 1 ? 1 : 0) + new[] { 5, 6, 8, 9, 10 }[Math.Min((int)dungeonDifficulty, 4)];

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, int basisLevel, byte mode, int rolls,
        byte dungeonDifficulty = 0, bool suppressEpicFragments = false)
    {
        if (rolls <= 0 || rules.Rate(basisLevel, mode) is not { } rate || rules.Rarity(mode) is not { } rarity ||
            rules.GradeRange(basisLevel) is not { } range) return plan;
        var pools = Enumerable.Range(0, 7).Select(index => new DungeonDropRarityPool(rarity[index],
            candidates.ReferencePool(true, range.Minimum, range.Maximum, index))).ToImmutableArray();
        var trial = new DungeonDropTrial(Math.Clamp(rate + 1, 0, Denominator), null, 1, 1, ProbabilityDenominator: Denominator,
            RarityPools: pools, Attempts: rolls);
        var trials = plan.Trials.Append(trial);
        // 实验性/默认关闭：史诗碎片。90 参考的概率来自环境变量（PVF 与 95 都没有概率表），这里用同样思路做成可配置（DFO_HELL_EPIC_FRAGMENT_PERMILLE，千分比/每骰，默认 0=关）。
        // 骰数 = (A 档 ? 1 : 0) + {0:5,1:6,2:8,3:9,4:10}[副本难度]（90 参考 EpicPieceDropService.ResolveRollCount）；候选 = etc/epicpieceinfo.etc [piece list] 的碎片 id。
        // 星空及启用哈林材料规则的普通深渊不叠加旧史诗碎片实验奖励。
        if (!suppressEpicFragments && epicFragments is { TotalWeight: > 0 } && epicFragmentPermille > 0)
            trials = trials.Append(new DungeonDropTrial(Math.Min(epicFragmentPermille, 1000), epicFragments, 1, 1, ProbabilityDenominator: 1000,
                Attempts: EpicFragmentRolls(mode, dungeonDifficulty)));
        return new(plan.ResourceSource, trials, plan.ReferenceQuality, plan.Amplification);
    }
}
