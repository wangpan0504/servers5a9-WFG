using System.Collections.Immutable;
using System.Security.Cryptography;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 仅解析当前卢克声明的容器；多池各抽一次，冻结实际产物，不同时发容器和内容。
internal sealed class LukeResourceRewards
{
    internal const string Source = "95jp/luke/rewards-v1;direct;undefeated=pvf;privilege=qualified";
    internal const string LegacySource = "95jp/luke/rewards-v1;direct;undefeated=25+75;privilege=qualified";
    // 旧资格仍按原冻结产物领取，不随新资源策略补差或重抽。
    internal static bool IsFormalSource(string source) => source is Source or LegacySource;
    readonly RaidScript script;
    readonly ItemCatalog catalog;
    readonly RaidRewardContainers odds;
    readonly ImmutableDictionary<int, BoosterDefinition> containers;
    internal AccountMaterialDefinition? UndefeatedSongLimit => RaidSongQuota.Limit(catalog);

    internal LukeResourceRewards(RaidScript script, ItemCatalog items, BoosterCatalog boosters)
    {
        if (script.Phases.Length != 2 || !script.Issues.IsEmpty) throw new InvalidDataException("卢克奖励阶段声明无效");
        this.script = script;
        catalog = items;
        odds = new(script, items, boosters);
        var loaded = ImmutableDictionary.CreateBuilder<int, BoosterDefinition>();
        var visiting = new HashSet<int>();
        void Load(int id, int depth)
        {
            if (loaded.ContainsKey(id)) return;
            if (depth > 4 || !visiting.Add(id) || !items.TryGet(id, out var item))
                throw new InvalidDataException("卢克奖励依赖缺失或循环");
            if (item.StackableType.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is
                ["[booster]" or "[upgradable legacy]", _])
            {
                var box = boosters.Get(id);
                if (!box.Available || box.PendingRule is not null || !box.PackageRewards.IsDefaultOrEmpty ||
                    box.Pools.IsDefaultOrEmpty || box.LotteryUseNeedItem is not null || box.DailyUseLimit is not null ||
                    box.Pools.Any(pool => pool.Draws != 1 || pool.JobTag is not null || pool.GrowType != -1 ||
                        pool.Rewards.IsDefaultOrEmpty || pool.Rewards.Any(row => row.Weight <= 0 || row.Count <= 0) ||
                        pool.Rewards.Sum(row => row.Weight) > int.MaxValue))
                    throw new InvalidDataException("卢克奖励容器不支持或奖池声明变化：" + id);
                foreach (int child in box.Pools.SelectMany(pool => pool.Rewards).Select(row => row.ItemId).Distinct()) Load(child, depth + 1);
                loaded.Add(id, box);
            }
            visiting.Remove(id);
        }
        foreach (int id in script.Phases.SelectMany(phase => phase.Rewards).Select(row => row.ItemId).Distinct()) Load(id, 0);
        containers = loaded.ToImmutable();
    }

    internal ImmutableArray<RaidPhaseRewardPolicy> Create(bool pcroom, bool privilege = false, Func<int, int>? draw = null, decimal epicMultiplier = 1)
    {
        int Roll(int maximum)
        {
            int value = (draw ?? RandomNumberGenerator.GetInt32)(maximum);
            return value >= 0 && value < maximum ? value : throw new InvalidDataException("卢克奖励随机值越界");
        }
        void Resolve(int id, int count, List<InventoryReward> grants)
        {
            if (!containers.TryGetValue(id, out var box)) { grants.Add(new(id, count)); return; }
            if (count != 1) throw new InvalidDataException("卢克奖励嵌套容器数量变化");
            foreach (var pool in box.Pools)
            {
                int value = Roll(checked((int)pool.Rewards.Sum(row => row.Weight)));
                var selected = pool.Rewards.First(row => (value -= checked((int)row.Weight)) < 0);
                Resolve(selected.ItemId, selected.Count, grants);
            }
        }
        RaidRewardOffer Offer(byte kind, List<InventoryReward> grants, byte code = 0)
        {
            var actual = grants.GroupBy(row => row.ItemId).Select(group => new InventoryReward(group.Key, group.Sum(row => row.Count))).ToImmutableArray();
            if (actual.IsEmpty || actual.Length > 32 || actual.Any(row => row.Count > ushort.MaxValue))
                throw new InvalidDataException("卢克奖励结果数量越界");
            var display = actual.Select(row => new LukeRewardResultSpec(code, checked((uint)row.ItemId), checked((ushort)row.Count), 0, 0, row.EnchantUpgrade)).ToImmutableArray();
            return new(kind, 0, actual, []) { LukeResult = display[0], LukeAdditionalResults = display.RemoveAt(0) };
        }
        return script.Phases.Select((phase, index) =>
        {
            var personal = new List<InventoryReward>();
            var party = phase.Rewards.Where(row => row.Kind == "party_card").ToArray();
            if (party is not [{ RawA: -1, RawB: 100, RawD: 0 } fixedRow]) throw new InvalidDataException("卢克固定奖励声明变化");
            Resolve(fixedRow.ItemId, 1, personal);
            if (personal is not [{ ItemId: 10308807, Count: > 0 and <= ushort.MaxValue }])
                throw new InvalidDataException("卢克不渝之咏资源声明变化");
            foreach (string kind in new[] { "pcroom_card", "pcroom_card_a", "privilege_card" })
            {
                var rows = phase.Rewards.Where(row => row.Kind == kind).ToArray();
                if (rows is not [{ RawA: -1, RawD: 0 } row] || row.RawB <= 0)
                    throw new InvalidDataException("卢克附加奖励声明变化");
                if (kind == "privilege_card" ? privilege : pcroom) Resolve(row.ItemId, 1, personal);
            }
            var offers = ImmutableArray.CreateBuilder<RaidRewardOffer>();
            offers.Add(Offer(1, personal));
            var squad = phase.Rewards.Where(row => row.Kind == "squad_item").ToArray();
            if (index == 0 && squad.Length != 0 || index == 1 && squad.Length == 0 ||
                squad.Any(row => row.RawA != -1 || row.RawB <= 0 || row.RawD is not (0 or 1)))
                throw new InvalidDataException("卢克团队奖励声明变化");
            if (squad.Length != 0)
            {
                int value = Roll(squad.Aggregate(0, (sum, row) => checked(sum + row.RawB)));
                var adjusted = odds.Weights(squad, row => row.ItemId, row => row.RawB, epicMultiplier);
                var winner = squad.Where((row, index) => (value -= adjusted[index]) < 0).First();
                var grants = new List<InventoryReward>(); Resolve(winner.ItemId, 1, grants);
                offers.Add(Offer(0, grants, checked((byte)winner.RawD)));
            }
            return new RaidPhaseRewardPolicy(Source, offers.ToImmutable(), new(0, checked((byte)index), 0, 0, 2, 0));
        }).ToImmutableArray();
    }
}
