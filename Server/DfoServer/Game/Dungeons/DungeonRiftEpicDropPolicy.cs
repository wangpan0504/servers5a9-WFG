using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 裂缝领主沿原装备触发率和稀有度骰修正史诗候选；魔界材料独立追加，不套用其它深渊的装备次数。
internal sealed class DungeonRiftEpicDropPolicy
{
    public const string Rule = "rift-boss-epic-pools-v1";
    public const string MaterialRule = "pandemonium-material-local-v1";
    public const int RefinedTerraniumId = 10158964;
    readonly DungeonItemDropPool metro;
    readonly DungeonItemDropPool dawn;
    readonly ImmutableArray<DungeonDropTrial> metroMaterials;

    public DungeonRiftEpicDropPolicy(DungeonItemDropCatalog candidates, DungeonItemDropPool dawn,
        DungeonItemDropPool fragments, ItemTemplate refinedTerranium, PandemoniumRiftMaterialRules materials)
    {
        materials.Validate();
        metro = candidates.EquipmentLevelPool(85, 90, 4);
        this.dawn = dawn;
        foreach (var pool in new[] { metro, dawn })
            if (pool.TotalWeight <= 0 || pool.Entries.Any(row => row.Template is not
                { Kind: ItemKind.Equipment, Rarity: 4, StackLimit: 1, Expiration.SupportsCreation: true }))
                throw new InvalidDataException("裂缝领主缺少完整可新建史诗池");
        if (dawn.Entries.Any(row => row.Template.MinimumLevel != 95))
            throw new InvalidDataException("黎明裂缝史诗必须来自95级哈林专表");
        if (fragments.TotalWeight <= 0 || fragments.Entries.Any(row => row.Template is not
            { Kind: ItemKind.Material, Expiration.SupportsCreation: true } || row.Template.StackLimit <= 0) ||
            refinedTerranium is not { Id: RefinedTerraniumId, Kind: ItemKind.Material, Expiration.SupportsCreation: true } ||
            refinedTerranium.StackLimit <= 0)
            throw new InvalidDataException("魔界裂缝缺少可新建的85/90碎片或精炼时空石");
        var rewards = ImmutableArray.CreateBuilder<DungeonDropTrial>();
        if (materials.FragmentRolls > 0 && materials.FragmentPermille > 0)
            rewards.Add(new(materials.FragmentPermille, fragments, 1, 1, ProbabilityDenominator: 1000,
                Attempts: materials.FragmentRolls));
        if (materials.RefinedTerraniumPermille > 0)
            rewards.Add(new(materials.RefinedTerraniumPermille,
                new DungeonItemDropPool([new DungeonWeightedDropItem(refinedTerranium, 1)]), 1, 1, ProbabilityDenominator: 1000));
        metroMaterials = rewards.ToImmutable();
    }

    public bool Matches(DungeonEntryPlan entry, DungeonPlannedMonster monster) => Pool(entry, monster) is not null;

    DungeonItemDropPool? Pool(DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (!entry.IsOrdinary || entry.IsPractice || entry.HellParty is not null || entry.IsHellLegend || entry.IsInfernoParty ||
            !monster.CanReportDeath || monster.IsFriendly || monster.IsScriptedBoss || monster.IsDummyMapBoss ||
            monster.SourceMap is not { } map) return null;
        // 这两只绿名在本地地图使用normal源行；按实际地图与模板核对，不能仅凭RankKind判绿名。
        if (entry.DungeonId == 316 && (map.Id, monster.MonsterId) is (44153, 59538) or (44156, 59537)) return metro;
        if (!monster.IsBoss || !entry.IsTerminalMap(map)) return null;
        return (entry.DungeonId, map.Id, monster.MonsterId) switch
        {
            (316, 44158, 63804) or (316, 44159, 107001626) => metro,
            (530, 41207, 71030) => dawn,
            _ => null
        };
    }

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (Pool(entry, monster) is not { } pool) return plan;
        var trials = plan.Trials.Select(trial =>
            trial.Category == DungeonDropCategory.Equipment && !trial.RarityPools.IsDefaultOrEmpty && trial.RarityPools.Length == 7
                ? trial with { RarityPools = trial.RarityPools.SetItem(4, trial.RarityPools[4] with { Items = pool }) }
                : trial);
        bool materials = entry.DungeonId == 316 && monster.IsBoss && entry.IsTerminalMap(monster.SourceMap!) && !metroMaterials.IsEmpty;
        return new(plan.ResourceSource + ";" + Rule + (materials ? ";" + MaterialRule : ""),
            materials ? trials.Concat(metroMaterials) : trials, plan.ReferenceQuality, plan.Amplification);
    }
}
