using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 泰波尔斯专表中的盒子是两组材料的容器；通关时冻结产物，展示与领取不再发放容器。
internal sealed class TayberrsCardRewardPolicy(DungeonCardPriceCatalog prices, BoosterCatalog boosters,
    ItemCatalog items, ClearRewardLimitCatalog? limits = null)
{
    public const string Rule = "pvf-tayberrs-material-card-v1";
    readonly ImmutableDictionary<int, (int ContainerItemId, ImmutableArray<BoosterPool> Pools)> pools = Load(prices, boosters, items, limits);
    public ClearRewardLimitCatalog? Limits => limits;
    public int? LimitedContainer(int dungeon) => pools.TryGetValue(dungeon, out var selected) &&
        limits?.FindItem(selected.ContainerItemId) is not null ? selected.ContainerItemId : null;

    static ImmutableDictionary<int, (int ContainerItemId, ImmutableArray<BoosterPool> Pools)> Load(DungeonCardPriceCatalog prices,
        BoosterCatalog boosters, ItemCatalog items, ClearRewardLimitCatalog? limits)
    {
        var result = ImmutableDictionary.CreateBuilder<int, (int ContainerItemId, ImmutableArray<BoosterPool> Pools)>();
        foreach (int dungeon in new[] { 291100320, 291100322, 5349 })
        {
            if (!prices.SpecificRewards.TryGetValue(dungeon, out var rows)) continue;
            if (rows is not [{ Weight: 10000, Count: 1 } row])
                throw new InvalidDataException("泰波尔斯材料奖励需要唯一容器");
            if (limits is not null)
            {
                if (limits.FindDungeon(dungeon) is not { GroupId: 1, DailyLimit: 3, WeeklyLimit: 6 } groupLimit ||
                    !groupLimit.Dungeons.SequenceEqual([291100320, 291100322, 5349]))
                    throw new InvalidDataException("泰波尔斯通关奖励上限组必须完整匹配三模式共享3/6限制");
                if (dungeon is 291100320 or 291100322 &&
                    limits.FindItem(row.ItemId) is not { DailyLimit: 3, WeeklyLimit: 6 })
                    throw new InvalidDataException("泰波尔斯奖励礼盒容器必须声明3/6道具限制");
            }
            var definition = boosters.Get(row.ItemId);
            if (!definition.Available || definition.Pools.Length != 2 || definition.Selection is not null ||
                !definition.PackageRewards.IsDefaultOrEmpty || definition.Lottery ||
                definition.Pools.Any(pool => pool.Draws != 1 || pool.JobTag is not null || pool.Rewards.IsEmpty ||
                    pool.Rewards.Sum(reward => reward.Weight) is <= 0 or > int.MaxValue ||
                    pool.Rewards.Any(reward => reward.Weight <= 0 || reward.Count <= 0 ||
                        items.Get(reward.ItemId) is not { Kind: ItemKind.Material or ItemKind.Consumable, StackLimit: > 0 })) ||
                definition.Pools[0].Rewards.Any(reward => reward.ItemId is not (10305616 or 10309040)) ||
                definition.Pools[1].Rewards.Any(reward => reward.ItemId != 10305617))
                throw new InvalidDataException("泰波尔斯容器必须完整声明苍穹碎片与天空之愿两组材料");
            result.Add(dungeon, (row.ItemId, definition.Pools));
        }
        return result.ToImmutable();
    }

    public DungeonCardReward Prepare(DungeonDefinition dungeon, int gold, Func<int, int> next, DateTimeOffset now,
        DungeonIncountUsage? clearRewardUsage = null, DungeonIncountUsage? itemRewardUsage = null)
    {
        if (!pools.TryGetValue(dungeon.Id, out var selected))
            throw new NotSupportedException("泰波尔斯缺少已解释的材料奖励容器");
        if (clearRewardUsage is { } usage &&
            !ClearRewardLimitPolicy.AllowsCardReward(limits, dungeon.Id, selected.ContainerItemId, usage, itemRewardUsage))
        {
            var exhausted = new DungeonCardReward(gold, null, Rule,
                DungeonCardPriceCatalog.ResourcePath + ";" + ClearRewardLimitCatalog.Path + ";" + dungeon.Path,
                now.ToUnixTimeSeconds());
            exhausted.Validate();
            return exhausted;
        }
        ItemCore Draw(BoosterPool pool)
        {
            int roll = next(checked((int)pool.Rewards.Sum(row => row.Weight)));
            foreach (var row in pool.Rewards)
            {
                if (roll < row.Weight) return DungeonDropCreation.Create(items.Get(row.ItemId), row.Count, next, now);
                roll -= checked((int)row.Weight);
            }
            throw new InvalidDataException("泰波尔斯材料权重未命中");
        }
        var reward = new DungeonCardReward(gold, Draw(selected.Pools[0]), Rule,
            DungeonCardPriceCatalog.ResourcePath + ";" + dungeon.Path, now.ToUnixTimeSeconds())
            { TayberrsWish = Draw(selected.Pools[1]) };
        reward.Validate();
        return reward;
    }
}
