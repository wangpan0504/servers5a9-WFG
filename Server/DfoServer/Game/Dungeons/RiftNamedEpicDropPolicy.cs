using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 裂缝专属领主规则：只替换装备通道，金币、材料及怪物独立掉落保持原计划。
internal sealed class RiftNamedEpicDropPolicy
{
    public const string Rule = "pandemonium-rift-named-epic-local-v1";
    public const uint DaybreakDungeonId = 530;
    public const int DaybreakBossMapId = 41207;
    public const int DaybreakBossMonsterId = 71030;
    public const int BaseThreshold = 1000;
    public const int NamedKillThreshold = 500;
    public const int MaximumNamedKills = 2;
    public static ImmutableArray<int> DaybreakNamedMonsterIds { get; } = [107001269, 107002888];

    readonly DungeonItemDropPool daybreakEpics;

    public RiftNamedEpicDropPolicy(DungeonItemDropPool daybreakEpics)
    {
        if (daybreakEpics.TotalWeight <= 0 || daybreakEpics.Entries.Any(row => row.Template is not
            { Kind: ItemKind.Equipment, Rarity: 4, MinimumLevel: 95, StackLimit: 1, Expiration.SupportsCreation: true }))
            throw new InvalidDataException("黎明裂缝领主装备池只能包含可新建的95级史诗");
        this.daybreakEpics = daybreakEpics;
    }

    public bool Applies(DungeonEntryPlan entry, DungeonPlannedMonster monster) => Pool(entry, monster) is not null;

    // 530的两只绿名资源带命名标记；按已核对模板与实例死亡事实计数。
    public bool IsNamedKill(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        entry.DungeonId == DaybreakDungeonId && DaybreakNamedMonsterIds.Contains(monster.MonsterId) &&
        monster.Source?.IsNamed == true;

    DungeonItemDropPool? Pool(DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (!entry.IsOrdinary || entry.IsPractice || !monster.CanReportDeath || monster.IsFriendly ||
            monster.IsScriptedBoss || monster.IsDummyMapBoss || !monster.IsBoss || monster.SourceMap is not { } map ||
            !entry.IsTerminalMap(map)) return null;
        return (entry.DungeonId, map.Id, monster.MonsterId) switch
        {
            (DaybreakDungeonId, DaybreakBossMapId, DaybreakBossMonsterId) => daybreakEpics,
            _ => null
        };
    }

    public DungeonDropGenerationPlan Rewrite(DungeonDropGenerationPlan plan, DungeonEntryPlan entry,
        DungeonPlannedMonster monster, int namedKills)
    {
        if (Pool(entry, monster) is not { } pool) return plan;
        int confirmed = Math.Clamp(namedKills, 0, MaximumNamedKills);
        var equipment = new DungeonDropTrial(BaseThreshold + NamedKillThreshold * confirmed, pool, 1, 1,
            Category: DungeonDropCategory.Equipment);
        var trials = ImmutableArray.CreateBuilder<DungeonDropTrial>(plan.Trials.Length + 1);
        bool replaced = false;
        foreach (var trial in plan.Trials)
        {
            if (trial.Category != DungeonDropCategory.Equipment)
                trials.Add(trial);
            else if (!replaced)
            {
                trials.Add(equipment);
                replaced = true;
            }
        }
        if (!replaced) trials.Add(equipment);
        // 原计划可能已有装备通道，也可能完全没有；最后条目数不固定，不能按预分配容量 MoveToImmutable。
        return new(plan.ResourceSource + ";" + Rule + ";named=" + confirmed, trials.ToImmutable(),
            plan.ReferenceQuality, plan.Amplification) { OrdinaryLukeDailySource = plan.OrdinaryLukeDailySource };
    }
}