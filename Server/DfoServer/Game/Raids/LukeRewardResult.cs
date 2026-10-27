using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Raids;

// nullable区分未提供和显式零；不把缺失的未知列补成零。只支持本次原箱结果，不是选择前预览。
internal sealed record LukeRewardResultSpec(byte? RawCode, uint? ItemId, ushort? DisplayNumber,
    byte? RawFlag, uint? RawTail1, uint? EnchantUpgrade)
{
    internal bool Matches(RaidRewardOffer offer) => RawCode.HasValue && RawFlag.HasValue && RawTail1.HasValue &&
        EnchantUpgrade is <= byte.MaxValue && !offer.Grants.IsDefault && offer.Grants is [{ ItemId: > 0, Count: > 0 and <= ushort.MaxValue } grant] &&
        ItemId == (uint)grant.ItemId && DisplayNumber == grant.Count;
}

internal sealed record LukeRewardResult(byte Kind, ushort Owner, byte Card, LukeRewardResultSpec Item);

internal static class LukeRewardResults
{
    // 只读同一生产创建规则的真实core；强化/锻造不等于EnchantUpgrade，展示不能改资产计划。
    internal static LukeRewardResult? Bind(RaidRewardReceipt receipt, ItemCatalog items, DateTimeOffset now)
    {
        if (receipt.Prepared.RaidType != RaidKind.Luke || receipt.Offer.LukeResult is not { } spec ||
            !spec.Matches(receipt.Offer) || receipt.Prepared.Kind != receipt.Offer.Kind || receipt.Prepared.Card != receipt.Offer.Card ||
            !receipt.Prepared.Grants.SequenceEqual(receipt.Offer.Grants)) return null;
        var grant = receipt.Prepared.Grants.Single();
        if (!items.TryGet(grant.ItemId, out var template) || !IsOriginalBox(template)) return null;
        var core = InventoryRewardGrantService.Create(template, utcNow: now, upgrade: grant.Upgrade, refinement: grant.Refinement);
        return spec.EnchantUpgrade == core.EnchantUpgrade
            ? new(receipt.Prepared.Kind, receipt.Prepared.Character, receipt.Prepared.Card, spec) : null;
    }

    internal static bool IsOriginalBox(ItemTemplate item) => item.Kind == ItemKind.Consumable &&
        item.StackableType.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is ["[booster]", "0"];
}
