using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AuctionCouponTests
{
    public static void RunResources(ItemResources resources)
    {
        int before = Count;
        var one = resources.CashShop.Product(100122);
        var month = resources.CashShop.Product(100205);
        That(one is { ItemId: 137, Count: 1, CeraPrice: 780, Section: "premium", GoldPrice: 0 } &&
            month is { ItemId: 193, Count: 1, CeraPrice: 780, Section: "premium", GoldPrice: 0 },
            "真实商城行 100122/137 与 100205/193 都是 780 点券的 premium");
        That(resources.Contracts.Get(137) == new PremiumContract(137, PremiumContractCatalog.AuctionCouponType, 86_400) &&
            resources.Contracts.Get(193) == new PremiumContract(193, PremiumContractCatalog.AuctionCouponType, 30 * 86_400) &&
            resources.Contracts.Get(901) == new PremiumContract(901, PremiumContractCatalog.AuctionCouponType, 10 * 86_400),
            "premiumlist 类型 29：137 为 1 日、901 为 10 日、193 为 30 日");
        That(resources.Items.Get(137) is { IsPremiumService: true } day &&
            day.StackableType.Contains("[etc]", StringComparison.Ordinal) &&
            day.AcquisitionContract == resources.Contracts.Get(137) &&
            resources.Items.Get(193).AcquisitionContract == resources.Contracts.Get(193) &&
            resources.Contracts.TryAcquisitionContract(137, out _) && resources.Contracts.TryAcquisitionContract(193, out _) &&
            !new InventoryPremiumContractUseService(resources.Items, resources.Contracts).TryDefinition(137, out _),
            "两件券按获得即激活，不进入霸王/达人的使用入口");
        var now = DateTimeOffset.Parse("2026-11-04T04:00:00Z");
        long instant = now.ToUnixTimeSeconds();
        var point = new Location(38, 1, 450, 234, 0);
        var character = new Character(1, 0, "Coupon", 1, point, point) { Level = 50 };
        character = character with { Inventory = CurrencyWallet.Set(character.Inventory, CurrencyKind.Cera, 2_000) };
        var cash = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits,
            resources.EpicPieces, resources.Cargo, contracts: resources.Contracts);
        var monthBuy = cash.Buy(character, new(0, [new(100205, 0)]), out var monthReceipt, now);
        var monthPlan = monthBuy.Plan;
        That(monthBuy.Accepted && monthPlan is not null && monthReceipt?.Products.Single().CommodityId == 100205 &&
            monthPlan.After.Items.IsEmpty &&
            monthPlan.After.AccountWallet.ContractExpiry(PremiumContractCatalog.AuctionCouponType) == instant + 30 * 86_400 &&
            CurrencyWallet.Get(monthPlan.After, CurrencyKind.Cera) == 1_220,
            "购买 100205 立即记 30 日类型 29，不把 193 放进背包");
        var extended = cash.Buy(character with { Inventory = monthPlan!.After }, new(0, [new(100122, 0)]), out _, now);
        That(extended.Accepted && extended.Plan!.After.AccountWallet.ContractExpiry(PremiumContractCatalog.AuctionCouponType) ==
            instant + 31 * 86_400 && CurrencyWallet.Get(extended.Plan.After, CurrencyKind.Cera) == 440,
            "未到期再买 100122 在原到期上加 1 日");
        Console.WriteLine($"PASS AuctionCouponTests resources ({Count - before} checks)");
    }
}
