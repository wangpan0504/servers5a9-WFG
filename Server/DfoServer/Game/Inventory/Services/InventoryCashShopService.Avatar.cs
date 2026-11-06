using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed partial class InventoryCashShopService
{
    internal static int? GiftType(ItemTemplate template, int typeIndex) =>
        template.Grade is >= 1 and <= 3 && typeIndex is >= 1 and <= 3
            ? (template.Grade - 1) * 3 + typeIndex : null;

    bool TryAvatar(Character character, CashShopPurchaseItem request, CashShopProduct product, InventorySnapshot paid,
        DateTimeOffset now, bool shopExpiration, bool ignoreUsablePeriod, out InventoryItem? reward, out AvatarPackageType? type,
        out InventorySnapshot? after, out long? sourceDeadline)
    {
        reward = null;
        type = null;
        after = null;
        sourceDeadline = null;
        if (avatars is null || request.PaymentMode > 1 || request.TypeIndex != product.AvatarType) return false;
        var template = catalog.Get(product.ItemId);
        var definition = avatars.GetAvatar(product.ItemId);
        if (template.Kind != ItemKind.Avatar || !template.Expiration.SupportsCreation || definition.PendingRule is not null || request.TypeIndex >= definition.Types.Length ||
            !definition.Abilities.Contains(request.AttributeValue)) return false;
        type = definition.Types[request.TypeIndex];
        if (type.Cera < 0 || type.Gold < 0 || type.WinPoints < 0) return false;
        if (request.PaymentMode == 1)
        {
            // 请求没有券槽；按背包槽顺序寻找同档次、同期限的有效兑换券，不绑定某个活动物品编号。
            if (GiftType(template, request.TypeIndex) is not { } giftType) return false;
            InventoryItem? coupon = null;
            foreach (var item in paid.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.IsStackable)
                .OrderBy(item => item.Position.Index))
            {
                if (item.Core.EquipmentLockId != 0 || item.IsExpired(now.ToUnixTimeSeconds())) continue;
                var source = catalog.Get(item.Core.ItemId);
                if (source.AvatarGiftType != giftType || !InventoryAvatarPackageService.SourceEligible(character, item, source)) continue;
                coupon = item;
                break;
            }
            if (coupon is null) return false;
            paid = InventoryDeleteService.Plan(paid, coupon.Position, coupon.Core.Value, 1).Plan!.After;
            sourceDeadline = coupon.ExpireTime > 0 ? coupon.ExpireTime : null;
        }
        // 期限与属性都来自本次选中的类型行；详情与核心由同一创建入口构造，不能仅发一件默认永久时装。
        var expiration = template.Expiration with { Days = type.Days };
        int expires = expiration.Resolve(now, shopExpiration, ignoreUsablePeriod);
        if (expires != 0 && expires <= now.ToUnixTimeSeconds()) return false;
        var core = InventoryRewardGrantService.Create(template, utcNow: now) with
            { Durability = request.AttributeValue, ExpireTime = expires };
        var created = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Avatar, 0), core);
        var sockets = type.Sockets.WithDefaults(template.AvatarSockets);
        reward = created with { Avatar = created.Avatar! with
        {
            Socket0 = AvatarSocket.Empty(sockets.First), Socket1 = AvatarSocket.Empty(sockets.Second),
            Socket2 = AvatarSocket.Empty(sockets.Third), Socket3 = AvatarSocket.Empty(sockets.Fourth), Socket4 = AvatarSocket.Empty(sockets.Fifth)
        } };
        after = paid;
        return true;
    }
}
