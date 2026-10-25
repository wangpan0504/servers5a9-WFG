using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 用户明确选择的本服补全：杂物固定9，芽在10至14之间等概率；不是PVF原版掉率。
internal sealed class SleepyHollowMaterialDropPolicy
{
    public const string Rule = "sleepy-hollow-material-local-v1";
    public const int UsefulJunkId = 10305787;
    public const int BirthSeedId = 10161610;
    readonly SleepyHollowRewardCatalog sources;
    readonly DungeonDropTrial[] rewards;

    public SleepyHollowMaterialDropPolicy(SleepyHollowRewardCatalog sources, Func<int, ItemTemplate> resolve)
    {
        this.sources = sources;
        rewards = [new(1, Pool(UsefulJunkId, ItemKind.Quest), 9, 9, ProbabilityDenominator: 1),
            new(1, Pool(BirthSeedId, ItemKind.Material), 10, 14, ProbabilityDenominator: 1)];
        DungeonItemDropPool Pool(int id, ItemKind kind)
        {
            var item = resolve(id);
            if (item.Id != id || item.Kind != kind || item.StackLimit <= 0 || !item.Expiration.SupportsCreation)
                throw new InvalidDataException("断头谷材料模板身份或创建合同不匹配: " + id);
            return new(new[] { new DungeonWeightedDropItem(item, 1) });
        }
    }

    public bool IsRewardBoss(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        entry.DungeonId == SleepyHollowRewardCatalog.DungeonId && entry.Difficulty == 0 && entry.IsOrdinary &&
        !entry.IsPractice && !entry.IsQuestReplay && entry.HellParty is null && !entry.IsCosmo &&
        monster.IsBoss && monster.CanReportDeath && !monster.IsFriendly && !monster.IsScriptedBoss &&
        monster.AiCharacter is null && monster.AltarActor is null && monster.HellWave is null && monster.SpawnType == 3 &&
        monster.SourceMap == entry.BossMap && monster.Team >= 100 && monster.ObjectKey != entry.CharacterId && monster.MonsterId == monster.Source?.MonsterId &&
        sources.Matches(entry.Maze, entry.BossMap, monster.SourceOrdinal, monster.Source);

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        !IsRewardBoss(entry, monster) ? plan : new(plan.ResourceSource + ";" + Rule, plan.Trials.Concat(rewards),
            plan.ReferenceQuality, plan.Amplification);
}
