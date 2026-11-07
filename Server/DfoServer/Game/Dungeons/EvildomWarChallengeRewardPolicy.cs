using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal static class EvildomWarChallengeRewardPolicy
{
    internal const string Rule = "pvf-evildom-war-challenge-card-v1";
    internal const int DecorationId = 490014021;

    internal static DungeonCardReward Prepare(DungeonDefinition dungeon, DungeonCardPriceCatalog? prices,
        ClearRewardLimitCatalog? limits, ItemCatalog items, DateTimeOffset now, DungeonIncountUsage usage)
    {
        if (!dungeon.IsEvildomWarChallenge || prices is null ||
            !prices.SpecificRewards.TryGetValue(dungeon.Id, out var rows) ||
            rows is not [{ ItemId: DecorationId, Weight: 10000, Count: 1 }] ||
            limits?.FindDungeon(dungeon.Id) is not { GroupId: 2, DailyLimit: 1, WeeklyLimit: 2 } limit ||
            !limit.Dungeons.SequenceEqual([EvildomWarChallengeEntryPolicy.DungeonId]))
            throw new InvalidDataException("魔界大战英雄奖励必须匹配独立1/2限次和唯一闪耀装饰专表");
        var template = items.Get(DecorationId);
        if (template.Kind != ItemKind.Material || template.StackLimit <= 0 || !template.Expiration.SupportsCreation)
            throw new InvalidDataException("魔界大战英雄装饰缺少可创建材料定义");
        var core = ClearRewardLimitPolicy.CanGrantDungeonReward(limit, usage)
            ? DungeonDropCreation.Create(template, 1, _ => 0, now) : null;
        var reward = new DungeonCardReward(0, core, Rule,
            DungeonCardPriceCatalog.ResourcePath + ";" + ClearRewardLimitCatalog.Path + ";" + dungeon.Path,
            now.ToUnixTimeSeconds());
        reward.Validate();
        return reward;
    }
}
