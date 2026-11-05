using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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

    // 普雷沿实际配方展开一层；子盒仍是奖励物品，全部随机结果在开战事务前冻结。
    internal ImmutableArray<InventoryReward> ResolveAll(int container, Func<int, int>? draw)
    {
        var item = items.Get(container);
        // 模板保留完整声明（例如[booster] 0）；子型整数不是类型名的一部分。
        string family = Regex.Match(item.StackableType, @"\[([^\]]+)\]").Groups[1].Value.ToLowerInvariant();
        if (family is not ("booster" or "cera booster" or "booster random" or "usable cera package"))
            return [Resolve(container, draw)];
        var definition = (boosters ?? throw new InvalidDataException("攻坚奖励缺少奖池目录")).Get(container);
        if (!definition.Available || definition.Selection is not null || definition.RuneBox is not null ||
            definition.DailyRetainedLimit is not null || !definition.FirstGrowTypes.IsEmpty ||
            definition.UnsealsRandomOptions || definition.CustomAbilityGrade is not null ||
            definition.SetsDisjointInstanceType || definition.SetsEpicExchangeInstanceType ||
            definition.Pools.Any(pool => pool.JobTag is not null || pool.Draws <= 0 || pool.Rewards.IsEmpty ||
                pool.Rewards.Any(row => row.Weight < 0 || row.Count <= 0 ||
                    !row.EmptyOutcome && (!items.TryGet(row.ItemId, out var reward) || reward.StackLimit < 1 ||
                        pool.RequiredKind is { } kind && reward.Kind != kind))))
            throw new InvalidDataException("攻坚奖励容器声明尚不能完整展开：" + container);
        var totals = new Dictionary<int, int>();
        foreach (var pool in definition.Pools)
        {
            long total = pool.Rewards.Sum(row => row.Weight);
            if (total is <= 0 or > int.MaxValue) throw new InvalidDataException("攻坚箱内总权重无效：" + container);
            for (int index = 0; index < pool.Draws; index++)
            {
                int roll = (draw ?? RandomNumberGenerator.GetInt32)((int)total);
                if (roll < 0 || roll >= total) throw new InvalidDataException("攻坚箱内随机值越界");
                foreach (var row in pool.Rewards)
                {
                    if (roll >= row.Weight) { roll -= checked((int)row.Weight); continue; }
                    if (!row.EmptyOutcome) Add(row.ItemId, row.Count);
                    break;
                }
            }
        }
        if (!definition.PackageRewards.IsDefaultOrEmpty)
            foreach (var row in definition.PackageRewards) Add(row.ItemId, row.Count);
        if (totals.Count is < 1 or > 32) throw new InvalidDataException("攻坚奖励产物数无法显示：" + container);
        return totals.OrderBy(row => row.Key).Select(row => new InventoryReward(row.Key, row.Value)).ToImmutableArray();

        void Add(int id, int count)
        {
            if (count <= 0 || !items.TryGet(id, out var reward) || reward.StackLimit < 1)
                throw new InvalidDataException("攻坚奖励产物无效：" + id);
            int amount = checked(totals.GetValueOrDefault(id) + count);
            if (amount > ushort.MaxValue) throw new InvalidDataException("攻坚奖励数量无法显示：" + id);
            totals[id] = amount;
        }
    }
}
