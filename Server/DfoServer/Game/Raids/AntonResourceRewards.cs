using System.Collections.Immutable;
using System.Security.Cryptography;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 资源依据：etc/raid/anton.etc两阶段STATE REWARD。外层抽样沿既有本服相对权重政策。
// 86直接解析upgradable legacy奖池；95沿共享BoosterCatalog解析相同三列并冻结实际产物。
// 不同格式的booster多池保留原箱，不套86单池算法。
// party及全员privilege福利走kind1；squad走kind0；PC房福利取既有Session.PremiumPcRoom。
internal sealed class AntonResourceRewards(RaidScript script, ItemCatalog items, BoosterCatalog? boosters)
{
    internal const string Source = "anton-resource-reward-v2;etc/raid/anton.etc;legacy=resolved;booster=box;weighted-squad=1;privilege=all";
    // Runtime装配时预热；开战持owner锁时只访问本策略不可变奖池，不再解析PVF。
    readonly ImmutableDictionary<int, ImmutableArray<BoosterReward>> legacyPools = LoadLegacyPools(script, items, boosters);

    static ImmutableDictionary<int, ImmutableArray<BoosterReward>> LoadLegacyPools(RaidScript script, ItemCatalog items, BoosterCatalog? boosters)
    {
        var result = ImmutableDictionary.CreateBuilder<int, ImmutableArray<BoosterReward>>();
        foreach (int id in script.Phases.SelectMany(phase => phase.Rewards).Select(row => row.ItemId).Distinct())
        {
            var definition = (boosters ?? throw new InvalidDataException("安徒恩奖励缺少奖池目录")).Get(id);
            if (!definition.Lottery) continue;
            if (!definition.Available || definition.Pools is not [{ Draws: 1, JobTag: null } pool] ||
                definition.LotteryUseNeedItem is not null || definition.DailyRetainedLimit is not null ||
                pool.Rewards.IsEmpty || pool.Rewards.Any(row => row.Weight < 0 || row.Count <= 0 ||
                    !items.TryGet(row.ItemId, out var item) || item.StackLimit < 1) ||
                pool.Rewards.Sum(row => row.Weight) is <= 0 or > int.MaxValue)
                throw new InvalidDataException("安徒恩单池奖励声明无效：" + id);
            result.Add(id, pool.Rewards);
        }
        return result.ToImmutable();
    }

    InventoryReward ResolveReward(int container, Func<int, int>? draw)
    {
        if (!legacyPools.TryGetValue(container, out var pool)) return new(container, 1);
        int weight = checked((int)pool.Sum(row => row.Weight));
        int roll = (draw ?? RandomNumberGenerator.GetInt32)(weight);
        if (roll < 0 || roll >= weight) throw new InvalidDataException("安徒恩箱内随机值越界");
        foreach (var row in pool)
        {
            if (roll < row.Weight) return new(row.ItemId, row.Count);
            roll -= checked((int)row.Weight);
        }
        throw new InvalidDataException("安徒恩箱内奖池未命中");
    }

    internal ImmutableArray<RaidPhaseRewardPolicy> Create(bool pcroom, Func<int, int>? draw = null, ushort character = 0)
    {
        if (script.Phases.Length != 2 || !script.Issues.IsEmpty) throw new InvalidDataException("安徒恩奖励缺少两阶段声明");
        var result = ImmutableArray.CreateBuilder<RaidPhaseRewardPolicy>(2);
        foreach (var (phase, index) in script.Phases.Select((phase, index) => (phase, index)))
        {
            var fixedGrants = ImmutableArray.CreateBuilder<InventoryReward>();
            foreach (string kind in new[] { "party_card", "pcroom_card", "privilege_card" })
            {
                var rows = phase.Rewards.Where(row => row.Kind == kind).ToArray();
                if (rows is not [{ RawA: -1, RawB: 100, RawD: 0, ItemId: > 0 } row])
                    throw new InvalidDataException("安徒恩固定奖励声明变化：" + kind);
                RequireItem(row.ItemId);
                if (kind != "pcroom_card" || pcroom) fixedGrants.Add(ResolveReward(row.ItemId, draw));
            }
            var squad = phase.Rewards.Where(row => row.Kind == "squad_item").ToArray();
            if (squad.Length is < 1 or > 32 || squad.Any(row => row.RawA != -1 || row.RawB <= 0 || row.RawD is not (0 or 1)))
                throw new InvalidDataException("安徒恩分队奖励声明无效");
            foreach (var row in squad) RequireItem(row.ItemId);
            int weight = squad.Aggregate(0, (sum, row) => checked(sum + row.RawB));
            int selected = (draw ?? RandomNumberGenerator.GetInt32)(weight);
            if (selected < 0 || selected >= weight) throw new InvalidDataException("安徒恩奖励随机值越界");
            var winner = squad.First(row => (selected -= row.RawB) < 0);
            // LIVE-VERIFY：95按类别/牌位/物品ID显示；code=0普通项、flag=0不点亮特殊效果。
            // 只展示本成员正式Card0冻结产物，不开放其它牌位或代领。
            RaidRewardOffer Offer(byte kind, ImmutableArray<InventoryReward> grants) => new(kind, 0, grants,
                [new(character, 0, grants.Select(item => new RaidRewardDisplayItem(0,
                    checked((uint)item.ItemId), checked((ushort)item.Count), 0)).ToImmutableArray())]);
            result.Add(new(Source,
                [Offer(1, fixedGrants.ToImmutable()), Offer(0, [ResolveReward(winner.ItemId, draw)])],
                new(0, checked((byte)index), 0, 0, 2, 0)));

        }
        return result.MoveToImmutable();
    }

    void RequireItem(int id)
    {
        if (!items.TryGet(id, out var item) || item.StackLimit < 1)
            throw new InvalidDataException("安徒恩奖励物品不可发放：" + id);
    }
}
