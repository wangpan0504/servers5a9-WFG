using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 本服补全概率，物品身份沿PVF；不从开盒、兑换或其它副本的参数反推深渊掉率。
internal sealed record HarlemAbyssBonusRules
{
    public bool Enabled { get; init; } = true;
    public int InvitationNumerator { get; init; } = 2;
    public int InvitationDenominator { get; init; } = 3;
    public int InvitationMaximumCount { get; init; } = 2;
    public int SoulPermille { get; init; } = 10;
    public int CrystalPermille { get; init; } = 1;
    public int SkyFragmentPermille { get; init; } = 100;
    public int StoneBagPermille { get; init; } = 5;
    public ImmutableArray<int> SkyFragments { get; init; } = [2, 3, 4, 4, 5];
    public int CosmoSkyFragmentMultiplier { get; init; } = 2;

    public static HarlemAbyssBonusRules Parse(string json)
    {
        var rules = JsonSerializer.Deserialize<HarlemAbyssBonusRules>(json, new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("深渊补全奖励规则不能为空");
        rules.Validate();
        return rules;
    }

    public void Validate()
    {
        if (InvitationDenominator is < 1 or > 1000000 || InvitationNumerator < 0 || InvitationNumerator > InvitationDenominator ||
            InvitationMaximumCount is < 1 or > 500 || SoulPermille is < 0 or > 1000 || CrystalPermille is < 0 or > 1000 ||
            SkyFragmentPermille is < 0 or > 1000 || StoneBagPermille is < 0 or > 1000 ||
            SkyFragments.IsDefault || SkyFragments.Length != 5 || SkyFragments.Any(count => count is < 1 or > 500) ||
            CosmoSkyFragmentMultiplier is < 1 or > 10 || (long)SkyFragments.Max() * CosmoSkyFragmentMultiplier > 500)
            throw new InvalidDataException("深渊补全奖励的概率、五档数量或完整地面容量参数越界");
    }
}

internal sealed class HarlemAbyssBonusDropPolicy
{
    public const string Rule = "harlem-abyss-bonus-local-v1";
    public const int InvitationId = 3330;
    public const int SoulId = 10158124;
    public const int CrystalId = 10305940;
    public const int SkyFragmentId = 10305616;
    public const int StoneBagId = 10307527;
    readonly HarlemAbyssBonusRules rules;
    readonly HarlemHellEpicDropPolicy epics;
    readonly ImmutableArray<DungeonDropTrial> common;
    readonly DungeonItemDropPool skyFragments;
    readonly DungeonItemDropPool stoneBag;

    public HarlemAbyssBonusDropPolicy(HarlemAbyssBonusRules rules, HarlemHellEpicDropPolicy epics, Func<int, ItemTemplate> resolveItem)
    {
        rules.Validate();
        this.rules = rules;
        this.epics = epics;
        var trials = ImmutableArray.CreateBuilder<DungeonDropTrial>();
        var invitation = Pool(InvitationId);
        var soul = Pool(SoulId);
        var crystal = Pool(CrystalId);
        skyFragments = Pool(SkyFragmentId);
        stoneBag = Pool(StoneBagId);
        if (rules.InvitationNumerator > 0)
            trials.Add(new(rules.InvitationNumerator, invitation, 1, rules.InvitationMaximumCount,
                ProbabilityDenominator: rules.InvitationDenominator, IndividualObjects: true));
        if (rules.SoulPermille > 0) trials.Add(new(rules.SoulPermille, soul, 1, 1, ProbabilityDenominator: 1000));
        if (rules.CrystalPermille > 0) trials.Add(new(rules.CrystalPermille, crystal, 1, 1, ProbabilityDenominator: 1000));
        common = trials.ToImmutable();

        DungeonItemDropPool Pool(int id)
        {
            var item = resolveItem(id);
            if (item.Id != id || item.Kind is not (ItemKind.Material or ItemKind.Consumable or ItemKind.Profession) ||
                item.StackLimit <= 0 || !item.Expiration.SupportsCreation)
                throw new NotSupportedException("当前PVF不支持深渊补全奖励新建: " + id);
            return new(new DungeonWeightedDropItem[] { new(item, 1) });
        }
    }

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (!rules.Enabled || !entry.IsOrdinary || entry.IsPractice || !monster.CanReportDeath || monster.IsFriendly || monster.IsScriptedBoss)
            return plan;
        bool cosmo = epics.IsCosmoBoss(entry, monster);
        if (!cosmo && (epics.Pool(entry) is null || monster.HellWave is null || monster.HellRewardRolls <= 0)) return plan;
        var trials = common.ToBuilder();
        if (cosmo || entry.IsHellLegend)
        {
            if (entry.Difficulty >= rules.SkyFragments.Length) throw new InvalidDataException("苍穹碎片奖励难度越界");
            int count = checked(rules.SkyFragments[entry.Difficulty] * (cosmo ? rules.CosmoSkyFragmentMultiplier : 1));
            // 一次命中生成完整份数，数量逐个落地；不能改成每个碎片各自判定概率。
            if (rules.SkyFragmentPermille > 0)
                trials.Add(new(rules.SkyFragmentPermille, skyFragments, count, count, ProbabilityDenominator: 1000, IndividualObjects: true));
            if (rules.StoneBagPermille > 0)
                trials.Add(new(rules.StoneBagPermille, stoneBag, 1, 1, ProbabilityDenominator: 1000));
        }
        return trials.Count == 0 ? plan : new(plan.ResourceSource + ":" + Rule, plan.Trials.Concat(trials), plan.ReferenceQuality, plan.Amplification);
    }
}
