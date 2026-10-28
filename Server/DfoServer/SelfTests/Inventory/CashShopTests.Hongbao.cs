using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopTests
{
    static void VerifyHongbaoPayment(Character character, DfoServer.GameWorld.Items.CashShopProduct ordinary, ItemResources resources)
    {
        var service = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits,
            resources.EpicPieces, resources.Cargo, contracts: resources.Contracts, hongbao: resources.GuildHongbao) { IgnoreAbsoluteExpiration = true };
        var mixed = character with { Guild = new(1, "红包会", 1, 30, "会长", "", 0, 1),
            Inventory = character.Inventory with { AccountWallet = character.Inventory.AccountWallet
            .Set(CurrencyKind.Cera, 100_000).Set(CurrencyKind.TokenCera, 100_000).Set(CurrencyKind.HappyTokenCera, 100_000) } };
        foreach (int commodity in new[] { 103938, 103939, 103940, 103941 })
        {
            var product = resources.CashShop.Product(commodity)!;
            That(product is not null && product.CeraPrice > 0 &&
                resources.Items.StackableAction(product.ItemId) is { Label: "[guild hongbao]" },
                "四种正式红包商品由真实道具动作识别，商品号和模板号不混用");
            var bought = service.Buy(mixed, new(0, [new(commodity, 0)]), out _);
            That(bought.Accepted && bought.Plan!.After.AccountWallet.Get(CurrencyKind.Cera) == 100_000 - product!.CeraPrice &&
                bought.Plan.After.AccountWallet.Get(CurrencyKind.TokenCera) == 100_000 &&
                bought.Plan.After.AccountWallet.Get(CurrencyKind.HappyTokenCera) == 100_000,
                "红包只扣真实点券，两类代币券余额保持不变");
            var voucherOnly = mixed with { Inventory = mixed.Inventory with { AccountWallet = mixed.Inventory.AccountWallet.Set(CurrencyKind.Cera, 0) } };
            That(service.Buy(voucherOnly, new(0, [new(commodity, 0)]), out var failed) is { Accepted: false, Plan: null } && failed is null,
                "代币券充足也不能替代红包真实点券，不返回物品或成功回执");
            var together = service.Buy(mixed, new(0, [new(ordinary.CommodityId, 0), new(commodity, 0)]), out _);
            That(together.Accepted && together.Plan!.After.AccountWallet.Get(CurrencyKind.Cera) == 100_000 - product!.CeraPrice &&
                together.Plan.After.AccountWallet.Get(CurrencyKind.HappyTokenCera) == 100_000 - ordinary.CeraPrice,
                "混合购物车先确保红包真实点券，普通商品继续使用正常代币券顺序");
        }
    }
}
