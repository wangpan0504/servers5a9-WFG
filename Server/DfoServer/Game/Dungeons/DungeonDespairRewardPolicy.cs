using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonDespairRewardPlan(int Layer, ImmutableArray<InventoryItem?> Cards, long CreatedAt)
{
    // 兼容规则：当前PVF物品/clearreward；历史端5/9抽；2013官方变更及2015梦幻之尘署名实玩记录177。
    // 6个气息来自公开玩家记录，不声称当前V31原服数量已经动态核实。
    public const string RuleId = "pvf-despair-clear-reference-player2015-v1";
    public void Validate()
    {
        if (Layer is < 1 or > 100 || Cards.IsDefault || Cards.Length != 10 || CreatedAt < 0)
            throw new InvalidDataException("绝望冻结翻牌维度无效");
        foreach (var item in Cards)
        {
            if (item is null) continue;
            if (item.Uid != 0) throw new InvalidDataException("绝望冻结奖励不能借用已有UID");
            item.Core.Validate();
            item.ValidateDetails(allocating: true);
        }
    }
}

internal sealed class DungeonDespairRewardPolicy(DungeonAltarClearItemCatalog clear, DungeonItemDropCatalog candidates,
    ItemCatalog items, GoldLimitCatalog limits, EpicPieceCatalog pieces)
{
    public DungeonDespairRewardPlan Prepare(int layer, Func<int, int> draw, DateTimeOffset now)
    {
        if (layer is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(layer));
        const int level = 70; // 历史TOD专用clear generator的参考等级，和100层AIC自身92级相互独立。
        var probability = clear.Probabilities.Single(row => level >= row.MinimumLevel && level <= row.MaximumLevel);
        var reference = clear.ItemReferences.Single(row => row.Level == level);
        if (probability.RawExtra != 0 || clear.KindWeights[0] != 0 || clear.KindWeights[2] != 0)
            throw new NotSupportedException("绝望翻牌存在未绑定的生成通道");
        int Next(int bound)
        {
            int value = draw(bound);
            return value >= 0 && value < bound ? value : throw new InvalidDataException("绝望翻牌随机值越界");
        }
        bool playerFloor = layer < 100 && layer % 10 == 0;
        var cards = new InventoryItem?[10];
        for (int at = 0; at < (playerFloor ? 9 : 5); at++)
        {
            if (Next(10000) + 1 >= clear.RandomItemProbability || Next(10000) >= probability.Probability) continue;
            int kind = Next(10000) < clear.KindWeights[1] ? 2 : 4;
            int rarityRoll = Next(1000000) + 1;
            int rarity = 0;
            while (rarity < clear.RarityThresholds.Length && rarityRoll > clear.RarityThresholds[rarity]) rarity++;
            var pool = candidates.AltarPool(kind == 4, level - reference.Value0, level + reference.Value1 - 1, rarity);
            if (pool.TotalWeight == 0) continue;
            var template = pool.Select(Next(pool.TotalWeight));
            var core = InventoryRewardGrantService.Create(template, 1, now);
            if (template.Kind == ItemKind.Equipment) core = core with { Value = Next(int.MaxValue) };
            cards[at] = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0), core);
        }
        // 100层奖章覆盖特殊槽；不把旧端1252黄金合成球带到当前版本。
        int specialId = layer == 100 ? 3314 : playerFloor ? 10089402 : 0;
        if (specialId != 0)
        {
            var template = items.Get(specialId);
            if (template.Kind != ItemKind.Material || template.StackLimit < (layer == 100 ? 1 : 6))
                throw new InvalidDataException("绝望特殊奖励未绑定当前PVF材料");
            cards[9] = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0),
                InventoryRewardGrantService.Create(template, layer == 100 ? 1 : 6, now));
        }
        var result = new DungeonDespairRewardPlan(layer, cards.ToImmutableArray(), now.ToUnixTimeSeconds());
        result.Validate();
        return result;
    }

    public InventoryPlan Delivery(Character character, DungeonDespairRewardPlan frozen, DateTimeOffset now)
    {
        frozen.Validate();
        var result = InventoryRewardGrantService.TryGrantItems(character, frozen.Cards.OfType<InventoryItem>().ToArray(),
            items, limits, pieces, now, allowOverflowMail: true);
        return result is { Accepted: true, Plan: { } plan }
            ? new(character.Inventory, plan.After, "despair_clear_reward", overflowItems: plan.OverflowItems)
            : throw new InvalidOperationException("绝望完整翻牌奖励无法规划入库");
    }
}
