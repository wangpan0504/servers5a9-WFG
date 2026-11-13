using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 获取条件来自当前物品说明；概率和数量是显式本服规则，不冒充客户端资源中的官服数值。
internal sealed class CarbonCrystalDropPolicy
{
    public const string Rule = "carbon-crystal-boss-local-v1";
    public const int ItemId = 10163135;
    // 用户确认按同期攻略“约三把一个”取千分比近似；不是官方精确掉率，也没有保底计数。
    public const int DefaultPermille = 333;
    public const int DefaultCount = 1;
    readonly DungeonDropTrial trial;

    public CarbonCrystalDropPolicy(Func<int, ItemTemplate> resolveItem, int permille = DefaultPermille,
        int minimum = DefaultCount, int maximum = DefaultCount)
    {
        if (permille is < 0 or > 1000 || minimum < 1 || maximum < minimum || maximum > 500)
            throw new InvalidDataException("碳结晶体规则超出千分比或地面对象范围");
        var item = resolveItem(ItemId);
        if (item.Id != ItemId || item.Kind != ItemKind.Material || item.StackLimit < 1 || !item.Expiration.SupportsCreation)
            throw new InvalidDataException("碳结晶体模板必须是可创建的材料");
        trial = new(permille, new DungeonItemDropPool([new DungeonWeightedDropItem(item, 1)]), minimum, maximum,
            ProbabilityDenominator: 1000, Category: DungeonDropCategory.Materials);
    }

    public static bool CanReward(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        !entry.DisableItemDrops && entry.UsesFatigue && entry.BasisLevel is >= 55 &&
        !TayberrsDropPolicy.Applies(entry) && monster.SourceMap is not null && monster.Source is not null &&
        monster.IsBoss && monster.CanReportDeath && !monster.IsFriendly && !monster.IsSpecialChild &&
        !monster.IsScriptedRetirement && !monster.IsScriptedBoss && !monster.IsDummyMapBoss &&
        monster.AiCharacter is null && monster.AltarActor is null && monster.HellWave is null &&
        monster.SpawnType <= 3 && monster.ObjectKey != entry.CharacterId;

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan, DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (trial.Threshold == 0 || !CanReward(entry, monster)) return plan;
        return new(plan.ResourceSource + ";" + Rule, plan.Trials.Add(trial), plan.ReferenceQuality, plan.Amplification, plan.Sarpoza)
        { OrdinaryLukeDailySource = plan.OrdinaryLukeDailySource };
    }
}
