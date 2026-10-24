using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 分支概率由用户明确制定；PVF仅提供各表内部的候选及相对权重。
internal sealed record HarlemHellEpicRules
{
    public int NormalWeaponPermille { get; init; } = 10;
    public int LegendRareEpicPermille { get; init; } = 0;
    public int CosmoTayberrsPermille { get; init; } = 100;
    public int CosmoSuperPermille { get; init; } = 100;

    public static HarlemHellEpicRules Parse(string json)
    {
        var rules = JsonSerializer.Deserialize<HarlemHellEpicRules>(json, new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("哈林史诗分支规则不能为空");
        rules.Validate();
        return rules;
    }

    public void Validate()
    {
        if (NormalWeaponPermille is < 0 or > 1000 || LegendRareEpicPermille is < 0 or > 1000 ||
            CosmoTayberrsPermille is < 0 or > 1000 || CosmoSuperPermille is < 0 or > 1000 ||
            CosmoTayberrsPermille + CosmoSuperPermille > 1000)
            throw new InvalidDataException("哈林史诗分支概率须为0至1000的千分比，超星空两分支之和不得超过1000");
    }
}

internal sealed class HarlemHellEpicDropPolicy
{
    public const string Rule = "harlem-hell-epic-local-v4";
    public const string RareRule = "harlem-starry-rare-extra-local-v1";
    public const int Area = 54;
    public const int NormalList = 1;
    public const int CosmoList = 2;
    public const int WeaponList = 3;
    public const int TayberrsList = 4;
    public const int SuperList = 5;
    readonly ImmutableHashSet<uint> hellDungeons;
    public DungeonItemDropPool Normal { get; }
    public DungeonItemDropPool? Weapons { get; }
    public DungeonItemDropPool? Tayberrs { get; }
    public DungeonItemDropPool? Cosmo { get; }
    public DungeonItemDropPool? Super { get; }
    public HarlemHellEpicRules Rules { get; }
    readonly DungeonCosmoRewardCatalog? cosmo;

    public HarlemHellEpicDropPolicy(IEnumerable<uint> hellDungeons, DungeonItemDropPool normal,
        DungeonItemDropPool? weapons = null, DungeonItemDropPool? tayberrs = null,
        DungeonItemDropPool? cosmoPool = null, HarlemHellEpicRules? rules = null, DungeonCosmoRewardCatalog? cosmo = null,
        DungeonItemDropPool? superPool = null)
    {
        this.hellDungeons = hellDungeons.ToImmutableHashSet();
        Validate(normal, false);
        if (weapons is not null) Validate(weapons, true);
        if (tayberrs is not null) Validate(tayberrs, true);
        if (cosmoPool is not null) Validate(cosmoPool, true);
        if (superPool is not null) Validate(superPool, true);
        Normal = normal;
        Weapons = weapons;
        Tayberrs = tayberrs;
        Cosmo = cosmoPool;
        Super = superPool;
        Rules = rules ?? new();
        Rules.Validate();
        if (Cosmo is not null && ((Rules.CosmoSuperPermille > 0 && Super is null) ||
            (Rules.CosmoTayberrsPermille > 0 && Tayberrs is null)))
            throw new InvalidDataException("启用超星空条件分支须完整装配对应候选池");
        this.cosmo = cosmo;
    }

    public DungeonItemDropPool? Pool(DungeonEntryPlan entry) => entry.IsOrdinary && !entry.IsPractice &&
        entry.HellParty is not null && hellDungeons.Contains(entry.DungeonId) ? Normal : null;

    // 只认已绑定的星空波次稀有怪；同模板的普通地图怪或超星空变体不能借此领奖。
    public bool IsLegendRare(DungeonEntryPlan entry, DungeonPlannedMonster monster) => entry.IsHellLegend && Pool(entry) is not null &&
        monster.CanReportDeath && !monster.IsFriendly && !monster.IsDummyMapBoss && monster.AiCharacter is null &&
        monster.HellWave is { } wave && monster.MonsterId == wave.MonsterId && monster.ObjectKey == wave.ObjectKey &&
        monster.SpawnType == wave.Type && monster.Source is { PlacementKind: "[fixed]", RankKind: "[normal]" } source &&
        source.MonsterId == wave.MonsterId && source.Path == wave.Path && DungeonHellPartyCatalog.IsHarlemLegendRare(wave);

    public DungeonDropGenerationPlan ExtendLegendRare(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        // 与末杀装备骰独立：稀有怪先死或最后死都使用自己的死亡事务。默认关闭，不改写PVF零概率。
        if (Rules.LegendRareEpicPermille == 0 || Tayberrs is null || !IsLegendRare(entry, monster)) return plan;
        return new(plan.ResourceSource + ":" + RareRule,
            plan.Trials.Append(new(Rules.LegendRareEpicPermille, Tayberrs, 1, 1, ProbabilityDenominator: 1000)),
            plan.ReferenceQuality, plan.Amplification);
    }

    public bool IsCosmoEntry(DungeonEntryPlan entry) => entry.DungeonId == DungeonCosmoRewardCatalog.DungeonId &&
        entry.IsOrdinary && !entry.IsPractice && cosmo is not null && entry.Difficulty == cosmo.Difficulty &&
        cosmo.BossRooms.ContainsKey(entry.BossMap.Id);

    public bool IsCosmoBoss(DungeonEntryPlan entry, DungeonPlannedMonster monster) => IsCosmoEntry(entry) &&
        monster.MonsterId == monster.Source?.MonsterId && cosmo!.Matches(entry.DungeonId, entry.BossMap, monster.Source) &&
        monster.IsBoss && !monster.IsFriendly && !monster.IsScriptedBoss && monster.HellWave is null;

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster,
        DungeonHellRewardPolicy? hellRewards)
    {
        bool ultra = IsCosmoBoss(entry, monster);
        if (ultra && Cosmo is not null && hellRewards is not null)
            plan = hellRewards.Extend(plan, entry.BasisLevel ?? monster.CandidateLevel, 1, cosmo!.RewardRolls,
                entry.Difficulty, suppressEpicFragments: true, epicEquipment: Cosmo);
        else if (Pool(entry) is null || monster.HellRewardRolls <= 0) return plan;

        var alternate = ultra ? Tayberrs : Weapons;
        int permille = ultra ? Rules.CosmoTayberrsPermille : Rules.NormalWeaponPermille;
        if (alternate is null && (!ultra || Super is null)) return plan;
        // 仅修改末波/最终领主追加的最后一组装备骰；普通怪及PVF独立奖励不借此增加触发率。
        int last = plan.Trials.Length - 1;
        var expected = ultra ? Cosmo : Pool(entry);
        while (last >= 0 && (plan.Trials[last].RarityPools.IsDefaultOrEmpty || plan.Trials[last].RarityPools.Length <= 4 ||
            !ReferenceEquals(plan.Trials[last].RarityPools[4].Items, expected))) last--;
        if (last < 0) return plan;
        return new(plan.ResourceSource, plan.Trials.Select((trial, index) => index != last || trial.RarityPools.IsDefaultOrEmpty ? trial : trial with
        {
            RarityPools = trial.RarityPools.Select((row, rarity) => rarity == 4 ?
                row with { AlternateItems = alternate, AlternatePermille = alternate is null ? 0 : permille,
                    SecondaryItems = ultra ? Super : null, SecondaryPermille = ultra ? Rules.CosmoSuperPermille : 0 } : row).ToImmutableArray()
        }), plan.ReferenceQuality, plan.Amplification);
    }

    static void Validate(DungeonItemDropPool pool, bool legend)
    {
        if (pool.TotalWeight <= 0 || pool.Entries.Any(row => row.Template.Kind != GameWorld.Items.ItemKind.Equipment ||
            row.Template.Rarity != 4 || (legend ? row.Template.MinimumLevel != 95 : row.Template.MinimumLevel is not (90 or 95))))
            throw new InvalidDataException("哈林普通/星空基础池须为90/95级史诗，苍穹/泰波尔斯及超星空基础池须为95级史诗");
    }
}
