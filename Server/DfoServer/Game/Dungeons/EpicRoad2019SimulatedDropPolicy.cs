using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

// 本服重开活动奖励，不声明为原服选表、掉率或件数分布。
internal sealed class EpicRoad2019SimulatedDropPolicy
{
    public const string Rule = "epic-road-2019-simulated-v2";
    readonly DungeonItemDropPool equipment;
    readonly bool enabled;

    public EpicRoad2019SimulatedDropPolicy(DungeonItemDropPool equipment, bool enabled)
    {
        if (equipment.TotalWeight <= 0 || equipment.Entries.Any(row => row.Template is not
            { Kind: ItemKind.Equipment, MinimumLevel: 95, Rarity: 4, StackLimit: 1, Expiration.SupportsCreation: true }))
            throw new InvalidDataException("星河裂缝模拟奖励需要完整可新建的95级史诗装备池");
        this.equipment = equipment;
        this.enabled = enabled;
    }

    public bool Matches(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        enabled && entry.DungeonId is 400000097 or 400000098 && entry.IsOrdinary && !entry.IsPractice &&
        !entry.DisableItemDrops && entry.HellParty is null && !entry.IsInfernoParty && !entry.IsHellLegend && !entry.IsCosmo &&
        monster.CanReportDeath && monster.IsBoss && !monster.IsFriendly && !monster.IsScriptedBoss &&
        !monster.IsDummyMapBoss && monster.SpawnType <= 3 && monster.HellWave is null &&
        monster.SourceMap is { } map && entry.IsTerminalMap(map);

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (!Matches(entry, monster)) return plan;
        return new(plan.ResourceSource + ";" + Rule,
            plan.Trials.Select(AdjustMaterial).Append(new(1, equipment, 1, 1,
                ProbabilityDenominator: entry.DungeonId == 400000097 ? 5 : 1,
                Category: DungeonDropCategory.Equipment, EpicRoad2019Simulated: true)),
            plan.ReferenceQuality, plan.Amplification)
        {
            OrdinaryLukeDailySource = plan.OrdinaryLukeDailySource,
            ExcludeLowEquipment = plan.ExcludeLowEquipment,
            ExcludeLowMaterials = plan.ExcludeLowMaterials
        };

        DungeonDropTrial AdjustMaterial(DungeonDropTrial trial)
        {
            if (trial.Items is not { Entries.Length: > 0 } pool ||
                pool.Entries.Any(row => row.Template is not { Kind: ItemKind.Material, Id: 490013716 or 490013717 }))
                return trial;
            if (entry.DungeonId == 400000098)
                return trial with { Threshold = (int)Math.Min(trial.ProbabilityDenominator, (long)trial.Threshold * 3) };
            // 当前原资源为固定数量；不把随机数量上下界放大成原本不存在的中间数量。
            if (trial.ItemCounts is { } counts)
                return trial with { ItemCounts = counts.ToImmutableDictionary(row => row.Key, row => checked(row.Value * 5)) };
            return trial.MinimumCount == trial.MaximumCount
                ? trial with { MinimumCount = checked(trial.MinimumCount * 5), MaximumCount = checked(trial.MaximumCount * 5) }
                : trial;
        }
    }
}
