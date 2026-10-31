using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 资源仅声明材料来源与兑换，未提供生成概率；沿本服显式材料兼容口径，每只真实敌人10%掉落1个。
internal sealed class AssaultMaterialDropPolicy
{
    public const string Rule = "assault-material-local-v1";
    public const int MaterialId = 10305788;
    readonly HashSet<uint> missions;
    readonly DungeonDropTrial reward;

    public AssaultMaterialDropPolicy(AssaultModeCatalog catalog, Func<int, ItemTemplate> resolve)
    {
        missions = catalog.Missions.Values.SelectMany(rows => rows).Select(id => checked((uint)id)).ToHashSet();
        var material = resolve(MaterialId);
        if (material is not { Kind: ItemKind.Material, Grade: 1, Rarity: 2, CreationRate: 0 })
            throw new InvalidDataException("强袭气息缺少已核对的材料模板");
        reward = new(DungeonExplicitDropPolicy.TriggerThreshold,
            new DungeonItemDropPool([new DungeonWeightedDropItem(material, 1)]), 1, 1,
            ProbabilityDenominator: DungeonExplicitDropPolicy.TriggerDenominator);
    }

    public bool Matches(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        missions.Contains(entry.DungeonId) && entry.IsOrdinary && !entry.IsPractice && entry.HellParty is null &&
        !entry.IsInfernoParty && !entry.IsCosmo && monster.CanReportDeath && !monster.IsFriendly && !monster.IsScriptedBoss &&
        !monster.IsDummyMapBoss && monster.AiCharacter is null && monster.AltarActor is null &&
        monster.HellWave is null && monster.SpawnType <= 3 && monster.SourceMap is not null &&
        monster.Source is not null && monster.Source.MonsterId == monster.MonsterId && monster.Team >= 100 &&
        monster.ObjectKey != entry.CharacterId;

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        Matches(entry, monster)
            ? new(plan.ResourceSource + ";" + Rule, plan.Trials.Add(reward), plan.ReferenceQuality, plan.Amplification)
            : plan;
}
