using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class DevilContractService(DevilContractCatalog devil, ItemCatalog items, GoldLimitCatalog gold, EpicPieceCatalog pieces)
{
    public DevilContractCatalog Catalog => devil;
    public static bool NeedsGift(InventorySnapshot inventory, DateTimeOffset now) =>
        DevilContractPolicy.Expiry(inventory.AccountWallet, 2) > now.ToUnixTimeSeconds() && DevilContractPolicy.Used(inventory, 2, now) == 0;

    public InventoryResult DailyGift(Character character, DateTimeOffset now)
    {
        var before = character.Inventory;
        if (!NeedsGift(before, now)) return new(true, 0, new(before, before, "devil_gift_unchanged"));
        if (devil.Assistants.Count != 4) return InventoryResult.Reject(19);
        var gift = InventoryRewardGrantService.TryGrantBatch(character,
            devil.Assistants.Keys.Order().Select(id => new InventoryReward(id, 1)).ToArray(), items, gold, pieces, now, allowOverflowMail: true);
        if (!gift.Accepted) return gift;
        // 道具期限来自模板的usable period；领取标记与物品/溢出邮件在同一库存事务落库。
        if (!DevilContractPolicy.TryConsume(gift.Plan!.After, 2, 1, now, out var after))
            return InventoryResult.Reject(19);
        return new(true, 0, new(before, after, "devil_daily_assistants",
            new(character, DevilContractPolicy.Expiry(before.AccountWallet, 2)), overflowItems: gift.Plan.OverflowItems));
    }
}
