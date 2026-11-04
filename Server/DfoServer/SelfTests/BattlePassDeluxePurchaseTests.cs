using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassDeluxePurchaseTests
{
    public static void RunResources(PvfArchive pvf, ItemResources resources)
    {
        int before = Count;
        var offer = BattlePassDeluxePurchase.Resolve(resources.CashShop);
        var template = resources.Items.Get(BattlePassDeluxePurchase.Item);
        That(offer is { CommodityId: 105658, ItemId: 490705044, Count: 1, CeraPrice: 15_600, Section: "package", GoldPrice: 0 } &&
            resources.CashShop.Mileage.CeraOnly.Contains(490705044) && resources.CashShop.Bonus(105658).Count == 0 &&
            template is { IsPremiumService: true, HasAcquisitionEffect: false, Name: "高级探险家豪华礼包" },
            "当前PVF豪华礼包是商品105658、物品490705044、15600点券且只扣真实点券");
        var catalog = BattlePassCatalog.Load(pvf);
        That(catalog.TotalExperienceToReach(40) == 50_700 && catalog.PackageIndex == 490705044 && catalog.BuyRestrictCode == 70289,
            "豪华购买使用资源40级阈值50700和购买限制码70289");
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-deluxe-resource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool succeeded = false;
        try
        {
            var start = new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8));
            var now = start.AddHours(2);
            var clock = new Clock(now);
            var policy = new BattlePassMissionPolicy("deluxe-resource", start, catalog);
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy, clock);
            new AuthenticationService(new AuthenticationRepository(path)).Create("deluxe-resource", "fixture-password");
            using var owner = new AccountOwners().Acquire("deluxe-resource", () => { });
            var role = repository.Create(owner, new NewCharacter("DeluxeShop", new Location(1, 0, 10, 10), null) { Level = 95 });
            role = repository.CommitInventory(owner, role, new InventoryPlan(role.Inventory,
                CurrencyWallet.Set(role.Inventory, CurrencyKind.Cera, 20_000), "fixture_credit", new(role)));
            var cash = new InventoryCashShopService(resources.Items, resources.CashShop, resources.GoldLimits, resources.EpicPieces, resources.Cargo,
                contracts: resources.Contracts, clock: clock, deluxeOffer: offer);
            var request = new CashShopPurchaseRequest(0, [new(105658, 0)]);
            That(!cash.Buy(role, request, out _, now).Accepted, "普通商城购买仍拒绝惰性豪华契约，不会把044放进背包");
            var point = new Location(1, 0, 10, 10);
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), point, point, new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repository, towns, owner, timeProvider: clock);
            session.Select(role.Slot); session.MarkReady();
            var result = session.BuyCashShop(request, cash, null, out var receipt, out var promotion, out var committed);
            That(result.Accepted && promotion is null && committed is not null && receipt?.Products.Single().CommodityId == 105658 &&
                committed.State.Season is { PassType: 1, TotalExperience: 50_700 } &&
                CurrencyWallet.Get(committed.Character.Inventory, CurrencyKind.Cera) == 4_400 &&
                !committed.Character.Inventory.Items.Any(row => row.Core.ItemId == 490705044),
                "直购105658提交type1、50700经验和4400点券余额，不生成礼包道具");
            That(!session.BuyCashShop(request, cash, null, out _, out _, out _).Accepted &&
                CurrencyWallet.Get(session.Selected!.Inventory, CurrencyKind.Cera) == 4_400,
                "账号已经购买过豪华礼包时再次直购拒绝且不扣点券");
            succeeded = true;
        }
        finally
        {
            if (succeeded) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.Error.WriteLine($"Battle-pass deluxe resource fixture preserved: {root}");
        }
        Console.WriteLine($"PASS BattlePassDeluxePurchaseTests resources ({Count - before} checks)");
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
