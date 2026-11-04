using System.Buffers.Binary;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassCertificateTests
{
    public static void RunCore()
    {
        int before = Count;
        byte[] body = new byte[19]; body[2] = 1; body[5] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), 105659);
        var ordinary = CashShopRequests.Buy(body);
        That(BattlePassCertificatePurchase.Contains(ordinary) && BattlePassCertificatePurchase.IsRequest(ordinary) &&
            ordinary.Items.Single() is { CommodityId: 105659, AttributeValue: 0xFF },
            "真实19B CMD64普通单件证书进入联合事务候选，不能再走普通惰性实物发放");
        foreach (int offset in new[] { 0, 1, 4, 11, 12, 13, 17 })
        {
            byte[] changed = (byte[])body.Clone(); changed[offset] = 1;
            var parsed = CashShopRequests.Buy(changed);
            That(BattlePassCertificatePurchase.Contains(parsed) && !BattlePassCertificatePurchase.IsRequest(parsed),
                "含证书的代币/礼赠/支付/选项/关联物品请求保留专用路由后整拒，不回落普通购买 " + offset);
        }
        byte[] display = (byte[])body.Clone(); display[3] = 0xEE; display[5] = 7; display[6] = 0xFE;
        var unknown = CashShopRequests.Buy(display);
        That(BattlePassCertificatePurchase.IsRequest(unknown) && unknown.PcRoom == 0xEE &&
            unknown.Items.Single() is { AttributeValue: 7, TypeIndex: 0xFE },
            "未知显示字段保留原值，不充当资格或订单授权，业务限制取已核实字段");
        var normalItem = new CashShopPurchaseItem(105659, 0);
        foreach (var request in new CashShopPurchaseRequest[]
        {
            new(0, []), new(0, [normalItem, normalItem]),
            new(0, [normalItem, new(105658, 0)]), new(0, [new(105658, 0), normalItem]),
            new(1, [normalItem]), new(0, [normalItem with { PaymentMode = 2 }]),
            new(0, [normalItem]) { CurrencyMode = 2 }, new(0, [normalItem]) { GiftMode = 2 }
        })
            That(!BattlePassCertificatePurchase.IsRequest(request), "非普通单件不能规划证书付费资格");
        That(!BattlePassCertificatePurchase.Contains(new(0, [new(5743, 0)])) &&
            !BattlePassCertificatePurchase.IsRequest(new(0, [new(5743, 0)])),
            "未确证豪华商品5743不借普通证书映射开放");
        var deluxe = new CashShopPurchaseRequest(0, [new(105658, 0)]);
        var deluxeOffer = new CashShopProduct(105658, BattlePassDeluxePurchase.Item, 1, 15600, "package");
        That(BattlePassDeluxePurchase.Contains(deluxe, deluxeOffer) && BattlePassDeluxePurchase.IsRequest(deluxe, deluxeOffer),
            "豪华044只接受共享真实商品映射中的单件点券请求");
        That(BattlePassDeluxePurchase.TargetLevel == 40 && BattlePassDeluxePurchase.Item == 490705044 &&
            BattlePassDeluxePurchase.Commodity == 105658 && deluxeOffer.CeraPrice == 15600,
            "豪华映射使用已确认105658/044及资源价格，40级目标保持");
        That(!BattlePassDeluxePurchase.IsRequest(new(0, [new(105658, 0), new(105658, 0)]), deluxeOffer) &&
            !BattlePassDeluxePurchase.IsRequest(new(0, [new(105658, 0)]) { GiftMode = 1 }, deluxeOffer) &&
            !BattlePassDeluxePurchase.Contains(new(0, [new(105660, 0)]), deluxeOffer),
            "豪华重复行、赠送和相邻商品105660整体拒绝");
        foreach (int price in new[] { 15600, 17800 })
        {
            var shop = CashShopCatalog.Parse($"[package]\n105658 490705044 1 0 {price} 0 0 0 0 0 0\n[/package]\n[buy only cera]\n490705044\n[/buy only cera]");
            That(BattlePassDeluxePurchase.Resolve(shop) is { CommodityId: 105658, ItemId: 490705044 } resolved &&
                resolved.CeraPrice == price, "豪华报价冻结真实资源值，不保留旧运营价常量");
        }
        That(BattlePassDeluxePurchase.Resolve(CashShopCatalog.Parse("[package]\n105660 490705044 1 0 15600 0 0 0 0 0 0\n[/package]\n[buy only cera]\n490705044\n[/buy only cera]")) is null,
            "只有商品105658能打开豪华购买，同物品的其它商品号保持关闭");
        Console.WriteLine($"PASS BattlePassCertificateTests core ({Count - before} checks)");
    }
}
