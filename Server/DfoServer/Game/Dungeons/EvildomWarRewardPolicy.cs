using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal sealed record EvildomWarRewardItem(byte Group, int ItemId, int Count);
internal sealed record EvildomWarRewardSelection(byte Mode, ImmutableArray<EvildomWarRewardItem> Items);

// 本服兼容规则，不把未证实的原服抽样、困难追加或配置类型解释写成协议事实。
internal sealed class EvildomWarRewardPolicy(EvildomWarCatalog catalog, ItemCatalog items, BoosterCatalog boosters)
{
    public const string LegacyRule = "evildom-war-local-reward-v1";
    public const string Rule = "evildom-war-local-reward-v2";
    public const string HiddenRule = "evildom-war-local-laboratory-v1";
    public const int Denominator = 10_000;
    public const int MaximumProducts = 128;

    internal static ImmutableArray<EvildomWarRewardDefinition> Select(byte mode,
        ImmutableArray<EvildomWarRewardDefinition> normal, ImmutableArray<EvildomWarRewardDefinition> hard,
        ImmutableArray<EvildomWarRewardDefinition> guide, Func<int, int> draw)
    {
        if (mode > 2) throw new ArgumentOutOfRangeException(nameof(mode));
        var result = ImmutableArray.CreateBuilder<EvildomWarRewardDefinition>();
        Pick(mode == 2 ? guide : normal);
        if (mode == 1) Pick(hard);
        return result.ToImmutable();

        void Pick(ImmutableArray<EvildomWarRewardDefinition> table)
        {
            if (table.IsDefaultOrEmpty) throw new InvalidDataException("魔界奖励表为空");
            foreach (var group in table.GroupBy(row => row.GroupRaw))
            {
                if (group.Key is < 0 or > 3 || group.Any(row => row.TypeRaw is < 0 or > 1 || row.ItemRaw < -1 ||
                    row.CountRaw is < 0 or > MaximumProducts || row.WeightRaw is < 0 or > Denominator ||
                    (row.ItemRaw == -1 ? row.CountRaw != 0 : row.ItemRaw == 0 || row.CountRaw == 0)) ||
                    group.Sum(row => (long)row.WeightRaw) > Denominator)
                    throw new InvalidDataException("魔界本服奖励表的组、类型、数量或万分权重无效");
                // 第1组为吉赛尔的实验室通关奖励（随王座结算发放或单独恢复），第2组不在结算翻牌发放。
                if (group.Key is 1 or 2) continue;
                int roll = draw(Denominator);
                if (roll is < 0 or >= Denominator) throw new InvalidDataException("魔界奖励随机值越界");
                foreach (var row in group)
                {
                    if (roll < row.WeightRaw)
                    {
                        if (row.ItemRaw > 0) result.Add(row);
                        break;
                    }
                    roll -= row.WeightRaw;
                }
                // 未满万分权重的尾部保留为空，不归一化或增加稀有物品概率。
            }
        }
    }

    public EvildomWarRewardSelection PrepareHidden(byte mode)
    {
        if (mode > 2) throw new ArgumentOutOfRangeException(nameof(mode));
        var rows = (mode == 1 ? catalog.Rewards : catalog.GuideModeRewards).Where(row => row.GroupRaw == 1).ToArray();
        if (rows is not [{ TypeRaw: 0, ItemRaw: > 0, CountRaw: > 0 and <= MaximumProducts, WeightRaw: Denominator }] ||
            !items.Contains(rows[0].ItemRaw))
            throw new InvalidDataException("魔界实验室额外奖励必须是资源中的单项固定奖励");
        return new(mode, [new(1, rows[0].ItemRaw, rows[0].CountRaw)]);
    }

    public EvildomWarRewardSelection Prepare(byte mode, Character character, Func<int, int> draw, bool includeHidden = false)
    {
        var selected = Select(mode, catalog.Rewards, catalog.HardModeRewards, catalog.GuideModeRewards, draw);
        var result = new List<EvildomWarRewardItem>();
        foreach (var row in selected)
        {
            if (row.TypeRaw == 0) Add(row.GroupRaw, row.ItemRaw, row.CountRaw);
            else
                for (int i = 0; i < row.CountRaw; i++) Expand(row.GroupRaw, row.ItemRaw);
        }
        if (includeHidden)
            result.AddRange(PrepareHidden(mode).Items);
        var finalItems = ImmutableArray.CreateBuilder<EvildomWarRewardItem>(result.Count);
        foreach (var row in result)
        {
            if (items.Get(row.ItemId).Kind == ItemKind.Talisman)
            {
                for (int i = 0; i < row.Count; i++)
                    finalItems.Add(new(row.Group, row.ItemId, 1));
            }
            else
            {
                int existing = -1;
                for (int i = 0; i < finalItems.Count; i++)
                {
                    if (finalItems[i].Group == row.Group && finalItems[i].ItemId == row.ItemId)
                    {
                        existing = i;
                        break;
                    }
                }
                if (existing >= 0)
                    finalItems[existing] = finalItems[existing] with { Count = checked(finalItems[existing].Count + row.Count) };
                else
                    finalItems.Add(row);
            }
        }
        return new(mode, finalItems.ToImmutable());

        void Add(int group, int id, int count)
        {
            if (id <= 0 || count <= 0 || result.Count >= MaximumProducts || !items.Contains(id))
                throw new InvalidDataException("魔界奖励产物或数量超界");
            result.Add(new(checked((byte)group), id, count));
        }

        void Expand(int group, int id)
        {
            var definition = boosters.Get(id);
            if (!definition.Available || definition.Selection is not null || !definition.PackageRewards.IsDefaultOrEmpty ||
                definition.UnsealsRandomOptions || definition.GrantsEpicExchangeOrigin || definition.SetsDisjointInstanceType ||
                definition.DailyUseLimit is not null || !definition.AllowsFirstGrowType(character.GrowType))
                throw new InvalidDataException("魔界随机奖励包含未支持的开箱附加语义");
            if (definition.RuneBox is { } box)
            {
                if (box.Family is < 0 or > 1 || !items.RuneRandomChoices().TryGet(box.Family, box.Tier, out var choice))
                    throw new InvalidDataException("魔界护石符文奖励缺少抽样表");
                bool canBoostOwn = RuneBoxSelection.CanBoostOwnClass(character.Job, character.GrowType);
                string? rejected = RuneBoxSelection.Select(choice,
                    grade => box.Family == 0 ? items.RuneCandidates(checked((byte)grade)) : items.TalismanCandidates(grade),
                    product => canBoostOwn && (box.Family == 0
                        ? items.Rune(product)?.AllowsUse(character.Level, character.Job, character.GrowType) == true
                        : items.Talisman(product)?.AllowsUse(character.Level, character.Job, character.GrowType) == true),
                    maximum => draw(checked((int)maximum)), out _, out int product);
                if (rejected is not null) throw new InvalidDataException("魔界护石符文抽样失败: " + rejected);
                Add(group, product, 1);
                return;
            }
            var pools = definition.Pools.Where(pool => pool.Matches(character.Job, character.GrowType, character.Level)).ToArray();
            if (pools.Length == 0) throw new InvalidDataException("魔界随机奖励没有合法产物池");
            foreach (var pool in pools)
            {
                if (pool.Draws is < 1 or > MaximumProducts || pool.Rewards.IsDefaultOrEmpty ||
                    pool.Rewards.Any(reward => reward.Weight < 0 || reward.ItemId <= 0 || reward.Count <= 0))
                    throw new InvalidDataException("魔界随机奖励池无效");
                int total = checked((int)pool.Rewards.Sum(reward => reward.Weight));
                if (total <= 0) throw new InvalidDataException("魔界随机奖励权重为空");
                for (int i = 0; i < pool.Draws; i++)
                {
                    int roll = draw(total);
                    if (roll < 0 || roll >= total) throw new InvalidDataException("魔界开箱随机值越界");
                    foreach (var reward in pool.Rewards)
                    {
                        if (roll < reward.Weight) { Add(group, reward.ItemId, reward.Count); break; }
                        roll -= checked((int)reward.Weight);
                    }
                }
            }
        }
    }

    public ImmutableArray<InventoryItem> Materialize(EvildomWarRewardSelection reward, DateTimeOffset now)
    {
        var result = ImmutableArray.CreateBuilder<InventoryItem>();
        foreach (var row in reward.Items)
        {
            var template = items.Get(row.ItemId);
            if (!(template.Kind is ItemKind.Consumable or ItemKind.Material or ItemKind.Rune or ItemKind.Talisman || template.IsMonsterCard) ||
                template.StackLimit <= 0 || !template.Expiration.SupportsCreation)
                throw new InvalidDataException($"魔界奖励无法生成完整物品核心: {row.ItemId}/{template.Kind}/{template.StackLimit}");
            for (int remaining = row.Count; remaining > 0;)
            {
                if (result.Count == MaximumProducts) throw new InvalidDataException("魔界奖励附件超界");
                int count = Math.Min(remaining, template.Kind == ItemKind.Talisman ? 1 : template.StackLimit);
                var core = InventoryRewardGrantService.Create(template, count, now);
                result.Add(InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 9), core));
                remaining -= count;
            }
        }
        return result.ToImmutable();
    }

    public InventoryPlan PlanGrant(Character character, ImmutableArray<InventoryItem> rewardItems, DateTimeOffset now) =>
        InventoryRewardGrantService.PlanPreMaterializedRewards(character, rewardItems, items, now);
}
