using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Raids;

// nullable区分未提供和显式零；完整结果沿本次已冻结实际产物，不是选择前预览。
internal sealed record LukeRewardResultSpec(byte? RawCode, uint? ItemId, ushort? DisplayNumber,
    byte? RawFlag, uint? RawTail1, uint? EnchantUpgrade)
{
    internal bool Matches(RaidRewardOffer offer) => !offer.Grants.IsDefaultOrEmpty &&
        !offer.LukeAdditionalResults.IsDefault && offer.LukeAdditionalResults.Length == offer.Grants.Length - 1 &&
        Matches(offer.Grants[0]) && offer.LukeAdditionalResults.All(row => row.RawCode == RawCode) &&
        offer.LukeAdditionalResults.Zip(offer.Grants.Skip(1)).All(pair => pair.First.Matches(pair.Second));
    internal bool Matches(InventoryReward grant) => RawCode.HasValue && RawFlag.HasValue && RawTail1.HasValue &&
        EnchantUpgrade is <= byte.MaxValue && grant is { ItemId: > 0, Count: >= 0 and <= ushort.MaxValue } &&
        (grant.Count > 0 || grant.ItemId == RaidSongQuota.ItemId) &&
        ItemId == (uint)grant.ItemId && DisplayNumber == grant.Count && EnchantUpgrade == grant.EnchantUpgrade;
}

internal sealed record LukeRewardResult(byte Kind, ushort Owner, byte Card, LukeRewardResultSpec Item)
{
    public ImmutableArray<LukeRewardResultSpec> AdditionalItems { get; init; } = [];
}

internal static class LukeRewardResults
{
    // 只读同一生产创建规则的真实core；强化/锻造不等于EnchantUpgrade，展示不能改资产计划。
    internal static LukeRewardResult? Bind(RaidRewardReceipt receipt, ItemCatalog items, DateTimeOffset now)
    {
        if (receipt.Prepared.RaidType != RaidKind.Luke || receipt.Offer.LukeResult is not { } spec ||
            !spec.Matches(receipt.Offer) || receipt.Prepared.Kind != receipt.Offer.Kind || receipt.Prepared.Card != receipt.Offer.Card ||
            !receipt.Prepared.Grants.SequenceEqual(receipt.Offer.Grants)) return null;
        foreach (var grant in receipt.Prepared.Grants)
            if (!items.TryGet(grant.ItemId, out _) && Currency.CurrencyWallet.RewardKind(grant.ItemId) is null) return null;
        return new(receipt.Prepared.Kind, receipt.Prepared.Character, receipt.Prepared.Card, spec)
            { AdditionalItems = receipt.Offer.LukeAdditionalResults };
    }

    internal static bool IsOriginalBox(ItemTemplate item) => item.Kind == ItemKind.Consumable &&
        item.StackableType.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is ["[booster]", "0"];
}
