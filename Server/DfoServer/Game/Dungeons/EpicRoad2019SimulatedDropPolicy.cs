using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

// 哈林追加是本服模拟，不代表官方选表。隐藏领主只落实PVF名单和活动说明里写明的固定1件超界。
internal sealed class EpicRoad2019SimulatedDropPolicy
{
    public const string Rule = "epic-road-2019-simulated-v2";
    public const string HiddenRule = "epic-road-2019-hidden-super-v1";
    public const int HiddenBossMonsterId = 407000601;
    public const int HiddenBossMapId = 400000465;
    readonly DungeonItemDropPool equipment;
    readonly DungeonItemDropPool? hiddenSuper;
    readonly ImmutableHashSet<int> hiddenEpicIds;
    readonly bool enabled;

    public EpicRoad2019SimulatedDropPolicy(DungeonItemDropPool equipment, bool enabled, DungeonItemDropPool? hiddenSuper = null)
    {
        Validate(equipment, "星河裂缝模拟奖励需要完整可新建的95级史诗装备池");
        if (hiddenSuper is not null)
            Validate(hiddenSuper, "超星河隐藏领主奖励需要完整可新建的95级超界史诗池");
        this.equipment = equipment;
        this.hiddenSuper = hiddenSuper;
        hiddenEpicIds = hiddenSuper is null ? ImmutableHashSet<int>.Empty :
            hiddenSuper.Entries.Select(row => row.Template.Id).ToImmutableHashSet();
        this.enabled = enabled;
    }

    static void Validate(DungeonItemDropPool pool, string message)
    {
        if (pool.TotalWeight <= 0 || pool.Entries.Any(row => row.Template is not
            { Kind: ItemKind.Equipment, MinimumLevel: 95, Rarity: 4, StackLimit: 1, Expiration.SupportsCreation: true }))
            throw new InvalidDataException(message);
    }

    public bool Matches(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        Eligible(entry, monster) && (enabled || IsHiddenBoss(entry, monster));

    // 02_hard 仅1条迷宫指向400000465，该图唯一领主是红腿爱克托。怪物脚本没有装备件数。
    static bool IsHiddenBoss(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        entry.DungeonId == 400000098 && monster.MonsterId == HiddenBossMonsterId &&
        monster.SourceMap?.Id == HiddenBossMapId;

    static bool Eligible(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        entry.DungeonId is 400000097 or 400000098 && entry.IsOrdinary && !entry.IsPractice &&
        !entry.DisableItemDrops && entry.HellParty is null && !entry.IsInfernoParty && !entry.IsHellLegend && !entry.IsCosmo &&
        monster.CanReportDeath && monster.IsBoss && !monster.IsFriendly && !monster.IsScriptedBoss &&
        !monster.IsDummyMapBoss && monster.SpawnType <= 3 && monster.HellWave is null &&
        monster.SourceMap is { } map && entry.IsTerminalMap(map);

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (!Matches(entry, monster)) return plan;
        bool hidden = hiddenSuper is not null && IsHiddenBoss(entry, monster);
        var trials = enabled ? plan.Trials.Select(AdjustMaterial) : plan.Trials.AsEnumerable();
        if (hidden)
            trials = trials.Select(trial => trial with { AllowedEpicEquipmentIds = hiddenEpicIds });
        if (hidden || enabled)
        {
            var reward = hidden ? hiddenSuper! : equipment;
            trials = trials.Append(DungeonDropControlSnapshot.ApplyTrial(new(1, reward, 1, 1,
                ProbabilityDenominator: !hidden && entry.DungeonId == 400000097 ? 5 : 1,
                Category: DungeonDropCategory.Equipment, EpicRoad2019Simulated: true)
            { AllowedEpicEquipmentIds = hidden ? hiddenEpicIds : null }, plan.DropMultipliers));
        }
        string source = plan.ResourceSource + ";" + (hidden && enabled ? Rule + ";" + HiddenRule : hidden ? HiddenRule : Rule);
        return new(source, trials, plan.ReferenceQuality, plan.Amplification)
        {
            OrdinaryLukeDailySource = plan.OrdinaryLukeDailySource,
            ExcludeLowEquipment = plan.ExcludeLowEquipment,
            ExcludeLowMaterials = plan.ExcludeLowMaterials,
            DropMultipliers = plan.DropMultipliers
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
