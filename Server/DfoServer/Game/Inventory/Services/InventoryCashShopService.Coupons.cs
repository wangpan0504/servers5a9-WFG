using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed partial class InventoryCashShopService
{
    bool TryProductCoupon(Character character, CashShopPurchaseItem request, CashShopProduct product,
        InventorySnapshot paid, DateTimeOffset now, out InventorySnapshot after, out long? deadline)
    {
        after = paid;
        deadline = null;
        if (!shop.IsCouponProduct(product.CommodityId)) return request.RelatedItemId == 0 && request.RelatedSlot == 0;
        // 隐藏映射商品必须携带指定背包券；装扮兑换仍属于原独立的PaymentMode=1流程。
        if (request.PaymentMode != 0 || product.Section == "avatar" || product.CeraPrice < 0 ||
            request.RelatedItemId is 0 or > int.MaxValue || request.RelatedSlot > short.MaxValue) return false;
        var source = paid.Find(new(InventoryListType.Main, (short)request.RelatedSlot));
        if (source is null || source.Core.ItemId != request.RelatedItemId || !source.Core.IsStackable || source.Core.Count <= 0 ||
            source.Core.EquipmentLockId != 0 || source.Core.IsRental || source.IsExpired(now.ToUnixTimeSeconds())) return false;
        try
        {
            if (catalog.CashShopCouponKey(source.Core.ItemId) is not { } key || !shop.AllowsCoupon(key, product.CommodityId) ||
                !InventoryAvatarPackageService.SourceEligible(character, source, catalog.Get(source.Core.ItemId))) return false;
        }
        catch (InvalidDataException) { return false; }
        var consumed = InventoryDeleteService.Plan(paid, source.Position, source.Core.Value, 1);
        if (!consumed.Accepted) return false;
        after = consumed.Plan!.After;
        // 券实例期限不受商城忽略模板历史绝对日期的调试开关影响。
        deadline = source.ExpireTime > 0 ? source.ExpireTime : null;
        return true;
    }
}
