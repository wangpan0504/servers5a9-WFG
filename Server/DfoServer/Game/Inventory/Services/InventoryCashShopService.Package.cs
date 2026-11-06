using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed partial class InventoryCashShopService
{
    // 购买时已完成的选择在同一购买计划中展开；不先发无选择礼包，再另起事务补奖。
    bool TryPackage(Character character, CashShopPurchaseItem request, CashShopProduct product, DateTimeOffset now,
        bool shopExpiration, bool ignoreUsablePeriod, out InventoryReward[] rewards, out InventoryItem[] avatarItems,
        out long? sourceDeadline, out string? reason)
    {
        rewards = [];
        avatarItems = [];
        sourceDeadline = null;
        reason = "cash_shop_package_selection";
        if (packages is null) return false;
        try
        {
            var source = catalog.Get(product.ItemId);
            var definition = packages.Get(product.ItemId);
            if (!source.AllowsUse(character.Level, character.Job) || !InventoryMoveService.RequiredSkillSatisfied(character, source) ||
                !definition.Available || !definition.AllowsFirstGrowType(character.GrowType) || definition.PackageRewards.IsDefaultOrEmpty)
                return false;
            int expires = source.Expiration.Resolve(now, shopExpiration, ignoreUsablePeriod);
            if (expires != 0 && expires <= now.ToUnixTimeSeconds())
            {
                reason = "cash_shop_package_expired";
                return false;
            }
            sourceDeadline = expires > 0 ? expires : null;
            var groups = definition.PackageSelections.IsDefault ? [] : definition.PackageSelections;
            if (request.PackageChoices.Count != groups.Length) return false;
            var seen = new HashSet<ushort>();
            var rows = definition.PackageRewards.ToList();
            foreach (var choice in request.PackageChoices)
            {
                if (choice.PackageItemId != product.ItemId || choice.GroupIndex >= groups.Length || !seen.Add(choice.GroupIndex))
                    return false;
                var group = groups[choice.GroupIndex];
                if (choice.SelectionIndex >= group.Rewards.Length) return false;
                rows.Add(group.Rewards[choice.SelectionIndex]);
            }
            var all = rows.Select(row => row with { Count = checked(row.Count * product.Count) }).ToArray();
            var avatarIds = InventoryCeraPackageAvatarRewards.AvatarIds(all, catalog);
            var choices = request.AvatarChoices.ToArray();
            if (avatarIds.Count == 0 ? choices.Length != 0 : !InventoryCeraPackageAvatarRewards.Matches(avatarIds, choices))
                return false;
            if (avatarIds.Count != 0 && (avatars is null ||
                !InventoryCeraPackageAvatarRewards.TryCreate(catalog, avatars, all, choices, now, shopExpiration,
                    ignoreUsablePeriod, out avatarItems, out reason))) return false;
            // 同一物品可同时出现于固定清单和多组选择，先足量合并，再由统一grant处理钱包、详情和溢出。
            rewards = all.Where(row => !avatarIds.Contains(row.ItemId)).GroupBy(row => row.ItemId)
                .Select(group => new InventoryReward(group.Key, checked(group.Sum(row => row.Count)))).ToArray();
            reason = null;
            return true;
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            reason = "cash_shop_package_resource_invalid";
            return false;
        }
    }
}
