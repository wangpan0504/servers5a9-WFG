using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Currency;

namespace DfoServer.Game.TagEvildom;

internal sealed record TagEvildomRewardSelection(int DungeonId, int DefinitionId, ImmutableArray<TagEvildomRewardItem> Items, string ResourceKey)
{
    public string Rule => TagEvildomRewardPolicy.Rule;
}

internal static class TagEvildomRewardPolicy
{
    // 本服兼容规则：资源万分池互斥抽一份；不把抽样器或固定队首收件策略冒称原服抓包结论。
    public const string Rule = "tag-evildom-pvf-single-team-mail-primary-v1";

    public static TagEvildomRewardSelection Prepare(TagEvildomRewardCatalog catalog, int dungeonId, Func<int, int> draw)
    {
        var pool = catalog.ForDungeon(dungeonId);
        int roll = draw(10_000);
        if (roll is < 0 or >= 10_000) throw new InvalidDataException("再战奖励随机值越界");
        foreach (var row in pool)
        {
            if (roll >= row.Weight) { roll -= row.Weight; continue; }
            return new(dungeonId, row.DefinitionId, row.Items, catalog.ResourceKey);
        }
        throw new InvalidDataException("再战奖励未命中完整权重表");
    }

    // 调用方先在本attempt冻结selection；物品规划失败也不能重新抽奖。所有附件均为真正产物，统一创建完整核心。
    public static ImmutableArray<InventoryItem> Materialize(TagEvildomRewardSelection selection, ItemCatalog items, DateTimeOffset at) =>
        Materialize(selection, items.Get, at);

    internal static ImmutableArray<InventoryItem> Materialize(TagEvildomRewardSelection selection,
        Func<int, ItemTemplate> template, DateTimeOffset at)
    {
        if (selection.Items.IsDefaultOrEmpty || selection.Items.Length > 10) throw new InvalidDataException("再战奖励附件数量无效");
        var result = ImmutableArray.CreateBuilder<InventoryItem>();
        foreach (var item in selection.Items)
        {
            var source = template(item.ItemId);
            if (source.Id != item.ItemId || item.Count <= 0 || source.IsPremiumService || source.AcquisitionContract is not null ||
                CurrencyWallet.RewardKind(source.Id) is not null || !source.Expiration.SupportsCreation ||
                !source.AttachType.Contains("[account]", StringComparison.Ordinal) ||
                !(source.Kind == ItemKind.Material && source.StackableType == "[material] 0" ||
                    source.Kind == ItemKind.Consumable && source.StackableType == "[enchant waste] 0"))
                throw new InvalidDataException("再战产物不是本版可邮件的账号材料或宝珠");
            if (source.StackLimit <= 0) throw new InvalidDataException("再战产物堆叠上限无效");
            for (int remaining = item.Count; remaining > 0;)
            {
                int count = Math.Min(remaining, source.StackLimit);
                if (result.Count == 10) throw new InvalidDataException("再战完整奖励超出单封邮件附件上限");
                var core = InventoryRewardGrantService.Create(source, count, at);
                result.Add(InventoryRewardGrantService.CreateItem(source, new(InventoryListType.Main, 9), core));
                remaining -= count;
            }
        }
        return result.ToImmutable();
    }
}
