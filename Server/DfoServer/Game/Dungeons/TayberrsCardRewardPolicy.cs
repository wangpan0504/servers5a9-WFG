using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 泰波尔斯专表中的盒子是两组材料的容器；通关时冻结产物，展示与领取不再发放容器。
internal sealed class TayberrsCardRewardPolicy(DungeonCardPriceCatalog prices, BoosterCatalog boosters, ItemCatalog items)
{
    public const string Rule = "pvf-tayberrs-material-card-v1";
    readonly ImmutableDictionary<int, ImmutableArray<BoosterPool>> pools = Load(prices, boosters, items);

    static ImmutableDictionary<int, ImmutableArray<BoosterPool>> Load(DungeonCardPriceCatalog prices,
        BoosterCatalog boosters, ItemCatalog items)
    {
        var result = ImmutableDictionary.CreateBuilder<int, ImmutableArray<BoosterPool>>();
        foreach (int dungeon in new[] { 291100320, 291100322, 5349 })
        {
            if (!prices.SpecificRewards.TryGetValue(dungeon, out var rows)) continue;
            if (rows is not [{ Weight: 10000, Count: 1 } row])
                throw new InvalidDataException("泰波尔斯材料奖励需要唯一容器");
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
            result.Add(dungeon, definition.Pools);
        }
        return result.ToImmutable();
    }

    public DungeonCardReward Prepare(DungeonDefinition dungeon, int gold, Func<int, int> next, DateTimeOffset now)
    {
        if (!pools.TryGetValue(dungeon.Id, out var selected))
            throw new NotSupportedException("泰波尔斯缺少已解释的材料奖励容器");
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
        var reward = new DungeonCardReward(gold, Draw(selected[0]), Rule,
            DungeonCardPriceCatalog.ResourcePath + ";" + dungeon.Path, now.ToUnixTimeSeconds())
            { TayberrsWish = Draw(selected[1]) };
        reward.Validate();
        return reward;
    }
}
