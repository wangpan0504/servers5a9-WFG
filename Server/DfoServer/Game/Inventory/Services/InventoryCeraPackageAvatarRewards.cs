using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 固定商城礼包里的时装奖励：选择集合必须覆盖 [package data] 中全部时装编号。
internal static class InventoryCeraPackageAvatarRewards
{
    public static HashSet<int> AvatarIds(IEnumerable<BoosterReward> rewards, ItemCatalog catalog)
    {
        var ids = new HashSet<int>();
        foreach (var row in rewards)
            if (catalog.Get(row.ItemId).Kind == ItemKind.Avatar)
                ids.Add(row.ItemId);
        return ids;
    }

    public static bool Matches(IReadOnlySet<int> avatarIds, CeraPackageChoice[] choices)
    {
        if (avatarIds.Count == 0 || choices.Length != avatarIds.Count)
            return false;
        var seen = new HashSet<int>();
        foreach (var choice in choices)
            if (choice.ItemId <= 0 || !avatarIds.Contains(choice.ItemId) || !seen.Add(choice.ItemId))
                return false;
        return true;
    }

    public static bool TryCreate(ItemCatalog catalog, AvatarPackageCatalog avatars, IEnumerable<BoosterReward> rewards,
        CeraPackageChoice[] choices, DateTimeOffset now, bool shopExpiration, bool ignoreUsablePeriod,
        out InventoryItem[] prototypes, out string? reason)
    {
        prototypes = [];
        reason = null;
        var options = choices.ToDictionary(choice => choice.ItemId, choice => choice.Option);
        var items = new List<InventoryItem>();
        try
        {
            foreach (var row in rewards)
            {
                var template = catalog.Get(row.ItemId);
                if (template.Kind != ItemKind.Avatar)
                    continue;
                if (!options.TryGetValue(row.ItemId, out byte ability))
                {
                    reason = "cera_package_avatar_choice_mismatch";
                    return false;
                }
                var avatar = avatars.GetAvatar(row.ItemId);
                // 礼包 [package data] 没有期限类型索引，使用模板自身期限和孔位，不取 Types[0] 的7天行。
                if (avatar.PendingRule is not null || !avatar.Abilities.Contains(ability))
                {
                    reason = "cera_package_avatar_ability_invalid";
                    return false;
                }
                int expires = template.Expiration.Resolve(now, shopExpiration, ignoreUsablePeriod);
                if (expires != 0 && expires <= now.ToUnixTimeSeconds())
                {
                    reason = "cera_package_avatar_expired";
                    return false;
                }
                var core = InventoryRewardGrantService.Create(template, utcNow: now) with { Durability = ability, ExpireTime = expires };
                var prototype = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Avatar, 0), core);
                for (int i = 0; i < row.Count; i++)
                    items.Add(prototype);
            }
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or OverflowException)
        {
            reason = "cera_package_avatar_resource_invalid";
            return false;
        }
        if (items.Count == 0)
        {
            reason = "cera_package_avatar_choice_mismatch";
            return false;
        }
        prototypes = items.ToArray();
        return true;
    }
}
