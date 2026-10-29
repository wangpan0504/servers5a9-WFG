using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopTests
{
    static void MappedCouponCore()
    {
        const string products = "[package]\n108628 490701918 1 0 19900 0 0 0 0 0 0\n[/package]";
        var plain = CashShopCatalog.Parse(products);
        That(plain.CouponMappings.Count == 0 && !plain.IsCouponProduct(108628), "没有券表的既有目录不推断券规则");
        var shop = CashShopCatalog.Parse(products, couponText:
            "[coupon]\n54 105276 108628\n54 105276 108628\n55 105372 108628\n[/coupon]");
        That(shop.CouponMappings.Count == 2 && shop.CouponMappings[54].Count == 1 && shop.CouponMappings[54][105276] == 108628 &&
            shop.Product(108628) is { CeraPrice: 19900, Count: 1 } && shop.Product(105276) is null,
            "三列映射保持原商品与目标的职责，相同声明不叠加或制造原价");
        That(shop.IsCouponProduct(108628) && !shop.IsCouponProduct(105276) && shop.AllowsCoupon(54, 108628) &&
            shop.AllowsCoupon(55, 108628) && !shop.AllowsCoupon(53, 108628) && !shop.AllowsCoupon(54, 105276),
            "映射目标可共享但券索引必须精确匹配，原商品和其他券不能套用");
        foreach (string malformed in new[]
        {
            "54 105276", "54 105276 108628 54", "0 105276 108628", "54 0 108628", "54 105276 0",
            "2147483648 105276 108628", "54 105276 `x`", "54 105276 108628 54 105276 108629"
        }) Reject<InvalidDataException>(() => CashShopCatalog.Parse(products, couponText: "[coupon]\n" + malformed + "\n[/coupon]"),
            "券表拒绝残行、无效索引、错误类型、溢出和同索引原商品冲突");
    }
}
