using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Inventory.Services;

internal sealed partial class InventoryCashShopService
{
    // 只供账号季资格联合事务规划。公共Buy仍按普通奖励守卫拒绝惰性contract0。
    internal InventoryResult BuyBattlePassCertificate(Character character, CashShopPurchaseRequest request,
        out CashShopPurchaseReceipt? receipt, DateTimeOffset now)
    {
        receipt = null;
        if (!BattlePassCertificatePurchase.IsRequest(request) ||
            shop.Product(BattlePassCertificatePurchase.Commodity) is not
            { ItemId: BattlePassCertificatePurchase.Item, Count: 1, CeraPrice: BattlePassCertificatePurchase.Cera,
              Section: "package", GoldPrice: 0, OtherPrice: 0, IsMileage: false } ||
            !shop.Mileage.CeraOnly.Contains(BattlePassCertificatePurchase.Item))
            return InventoryResult.Reject(17) with { RejectReason = "battle_pass_certificate_request" };
        var template = catalog.Get(BattlePassCertificatePurchase.Item);
        if (!template.IsPremiumService || template.HasAcquisitionEffect || !template.AllowsUse(character.Level, character.Job) ||
            !InventoryMoveService.RequiredSkillSatisfied(character, template))
            return InventoryResult.Reject(17) with { RejectReason = "battle_pass_certificate_template" };
        return BuyCore(character, request, out receipt, now, battlePassCertificate: true);
    }
}
