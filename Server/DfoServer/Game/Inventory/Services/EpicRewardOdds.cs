using System.Collections.Concurrent;
using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 目录归服务实例所有；只缓存已注册物品的不可变资源事实，不保存角色或抽样结果。
internal sealed class EpicRewardOdds(ItemCatalog items, BoosterCatalog? boosters)
{
    readonly ConcurrentDictionary<int, bool> guaranteed = new();
    internal bool Guaranteed(int id) => Resolve(id, [], 0);

    bool Resolve(int id, HashSet<int> visiting, int depth)
    {
        if (guaranteed.TryGetValue(id, out bool cached)) return cached;
        if (depth > 8 || !items.Contains(id) || !visiting.Add(id)) return false;
        bool epic = DungeonEpicDropAdjustment.IsEpic(items.Get(id));
        if (!epic && boosters is not null)
        {
            var box = boosters.Get(id);
            epic = box.Available && (!box.PackageRewards.IsDefaultOrEmpty && box.PackageRewards.Any(row => row.Count > 0 && Resolve(row.ItemId, visiting, depth + 1)) ||
                box.Pools.Any(pool => pool.Draws > 0 && pool.JobTag is null && pool.Rewards.Any(row => row.Weight > 0) &&
                    pool.Rewards.Where(row => row.Weight > 0).All(row => !row.EmptyOutcome && Resolve(row.ItemId, visiting, depth + 1))));
        }
        visiting.Remove(id);
        guaranteed.TryAdd(id, epic);
        return epic;
    }

    internal ImmutableArray<BoosterReward> Apply(ImmutableArray<BoosterReward> rows, decimal multiplier)
    {
        if (multiplier == 1 || rows.IsDefaultOrEmpty) return rows;
        var epics = rows.Select(row => !row.EmptyOutcome && row.Weight > 0 && Guaranteed(row.ItemId)).ToImmutableArray();
        // 已经保证一件史诗的池封顶100%，不改变该保证或装备数量。
        if (!epics.Any(value => value) || rows.Where(row => row.Weight > 0).All(row => !row.EmptyOutcome && Guaranteed(row.ItemId))) return rows;
        long total = 0;
        var cumulative = rows.Select(row => total = checked(total + row.Weight)).ToImmutableArray();
        var adjusted = DungeonEpicRateAdjustment.AdjustWide(cumulative, epics, total, multiplier);
        var result = ImmutableArray.CreateBuilder<BoosterReward>(rows.Length);
        long previous = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            result.Add(rows[i] with { Weight = adjusted[i] - previous });
            previous = adjusted[i];
        }
        return result.MoveToImmutable();
    }
}
