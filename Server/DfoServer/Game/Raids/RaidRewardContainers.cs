using System.Collections.Immutable;
using System.Security.Cryptography;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 原legacy单池在开战前解析；两种团本共用同一箱内抽样合同。
internal sealed class RaidRewardContainers(RaidScript script, ItemCatalog items, BoosterCatalog? boosters)
{
    readonly ImmutableDictionary<int, ImmutableArray<BoosterReward>> legacyPools = LoadLegacyPools(script, items, boosters);

    static ImmutableDictionary<int, ImmutableArray<BoosterReward>> LoadLegacyPools(RaidScript script, ItemCatalog items, BoosterCatalog? boosters)
    {
        var result = ImmutableDictionary.CreateBuilder<int, ImmutableArray<BoosterReward>>();
        foreach (int id in script.Phases.SelectMany(phase => phase.Rewards).Select(row => row.ItemId).Distinct())
        {
            var definition = (boosters ?? throw new InvalidDataException("攻坚奖励缺少奖池目录")).Get(id);
            if (!definition.Lottery) continue;
            if (!definition.Available || definition.Pools is not [{ Draws: 1, JobTag: null } pool] ||
                definition.LotteryUseNeedItem is not null || definition.DailyUseLimit is not null ||
                pool.Rewards.IsEmpty || pool.Rewards.Any(row => row.Weight < 0 || row.Count <= 0 ||
                    !items.TryGet(row.ItemId, out var item) || item.StackLimit < 1) ||
                pool.Rewards.Sum(row => row.Weight) is <= 0 or > int.MaxValue)
                throw new InvalidDataException("攻坚单池奖励声明无效：" + id);
            result.Add(id, pool.Rewards);
        }
        return result.ToImmutable();
    }

    internal InventoryReward Resolve(int container, Func<int, int>? draw)
    {
        if (!legacyPools.TryGetValue(container, out var pool)) return new(container, 1);
        int weight = checked((int)pool.Sum(row => row.Weight));
        int roll = (draw ?? RandomNumberGenerator.GetInt32)(weight);
        if (roll < 0 || roll >= weight) throw new InvalidDataException("攻坚箱内随机值越界");
        foreach (var row in pool)
        {
            if (roll < row.Weight) return new(row.ItemId, row.Count);
            roll -= checked((int)row.Weight);
        }
        throw new InvalidDataException("攻坚箱内奖池未命中");
    }

}
