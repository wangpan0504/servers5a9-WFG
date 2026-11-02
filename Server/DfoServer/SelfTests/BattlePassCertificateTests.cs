using System.Buffers.Binary;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Inventory.Models;
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
        Console.WriteLine($"PASS BattlePassCertificateTests core ({Count - before} checks)");
    }
}
