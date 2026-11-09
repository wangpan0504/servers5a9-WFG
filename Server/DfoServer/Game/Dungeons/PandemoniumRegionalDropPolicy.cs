using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 芽与次元光辉的地区生成表不在当前PVF内；沿本服既有显式掉落规则补全，不冒充原版概率。
internal sealed class PandemoniumRegionalDropPolicy(Func<int, DungeonDefinition> resolveDungeon,
    Func<int, ItemTemplate> resolveItem, DungeonSpecialDropCatalog special)
{
    public const string Rule = "pandemonium-regional-local-v1";
    public const int BirthSeedId = 10161610;
    public const int DimensionalRadianceId = 10163136;
    public const int DimensionalRadianceTriggerThreshold = 100;

    public bool Applies(DungeonEntryPlan entry)
    {
        if (entry.IsPractice || entry.IsQuestReplay) return false;
        var dungeon = resolveDungeon(checked((int)entry.DungeonId));
        return IsOrdinaryRegionalDungeon(dungeon) &&
            (IsMetroCenterPath(dungeon.Path) ||
             dungeon.Path.StartsWith("dungeon/harlem/", StringComparison.OrdinalIgnoreCase));
    }

    public bool AppliesDimensionalRadiance(DungeonEntryPlan entry)
    {
        if (entry.IsPractice || entry.IsQuestReplay) return false;
        var dungeon = resolveDungeon(checked((int)entry.DungeonId));
        return IsOrdinaryRegionalDungeon(dungeon) && IsMetroCenterPath(dungeon.Path);
    }

    static bool IsOrdinaryRegionalDungeon(DungeonDefinition dungeon) =>
        string.IsNullOrEmpty(dungeon.DungeonType) && !dungeon.IsRaid &&
        !dungeon.RawSections.Any(section => section.Tag == "disable drop item");

    static bool IsMetroCenterPath(string path) =>
        path.StartsWith("dungeon/metrocenter/", StringComparison.OrdinalIgnoreCase);

    public static bool CanReward(DungeonPlannedMonster monster) => monster.CanReportDeath && !monster.IsFriendly &&
        !monster.IsScriptedBoss && !monster.IsDummyMapBoss && !monster.IsScriptedRetirement && monster.SpawnType <= 3 &&
        monster.HellWave is null && monster.AiCharacter is null && monster.AltarActor is null;

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (!CanReward(monster) || !Applies(entry)) return plan;
        var seed = resolveItem(BirthSeedId);
        if (seed.Id != BirthSeedId || seed.Kind != ItemKind.Material || seed.StackLimit <= 0 || !seed.Expiration.SupportsCreation)
            throw new InvalidDataException("地区诞生之芽模板不支持材料创建");
        var trials = plan.Trials.ToBuilder();
        trials.Add(new(DungeonExplicitDropPolicy.TriggerThreshold,
            new DungeonItemDropPool([new DungeonWeightedDropItem(seed, 1)]), 1, 1,
            ProbabilityDenominator: DungeonExplicitDropPolicy.TriggerDenominator, Category: DungeonDropCategory.Materials));
        if (AppliesDimensionalRadiance(entry))
        {
            var radiance = resolveItem(DimensionalRadianceId);
            if (radiance.Id != DimensionalRadianceId || radiance.Kind != ItemKind.Material || radiance.Rarity != 6 ||
                radiance.StackLimit <= 0 || !radiance.Expiration.SupportsCreation)
                throw new InvalidDataException("地轨中心次元光辉模板不支持传说材料创建");
            trials.Add(new(DimensionalRadianceTriggerThreshold,
                new DungeonItemDropPool([new DungeonWeightedDropItem(radiance, 1)]), 1, 1,
                ProbabilityDenominator: DungeonExplicitDropPolicy.TriggerDenominator, Category: DungeonDropCategory.Materials));
        }
        if (special.CardsByMonster.TryGetValue(monster.MonsterId, out var cards))
        foreach (var card in cards)
        {
            int basis = entry.BasisLevel ?? monster.CandidateLevel;
            if (card.EnemyType != 0 || card.ListFlag != 3 || card.ItemId <= 0 || !card.ListValues.IsEmpty ||
                card.Difficulty >= 0 && card.Difficulty != entry.Difficulty ||
                card.MinimumLevel > 0 && basis < card.MinimumLevel || card.MaximumLevel > 0 && basis > card.MaximumLevel)
                continue;
            int probability = card.Probabilities[entry.Difficulty], attempts = card.Attempts[entry.Difficulty];
            if (probability == 0 || attempts == 0) continue;
            if (probability > 1000000) throw new InvalidDataException("地区卡片概率超过百万分母");
            var template = resolveItem(card.ItemId);
            if (!template.IsMonsterCard || template.Kind != ItemKind.Profession || !template.Expiration.SupportsCreation)
                throw new InvalidDataException("地区卡片掉落声明未指向可创建的怪物卡片");
            trials.Add(new(probability, new DungeonItemDropPool([new DungeonWeightedDropItem(template, 1)]), 1, 1,
                ProbabilityDenominator: 1000000, Attempts: attempts, Category: DungeonDropCategory.MonsterItems));
        }
        return new(plan.ResourceSource + ";" + Rule + ";" + DungeonSpecialDropCatalog.CardPath, trials,
            plan.ReferenceQuality, plan.Amplification) { OrdinaryLukeDailySource = plan.OrdinaryLukeDailySource };
    }
}
