using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class PromotionPageSessionTests
{
    // 资源阶段集成直接调用真实Session、Buy、规划器和仓储。
    public static void RunPurchase(TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        string directory = Path.Combine(Path.GetTempPath(), "promotion-page-buy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        GameSession? session = null;
        try
        {
            var rules = CashShopPromotionTests.PilotRules();
            var other = rules with { Scope = rules.Scope with { Group = "other" }, Products = [new(700101, 490705385, 1)] };
            // 合成商品行使用真实主货和奖励模板，验证生产Buy路径。
            var shop = CashShopCatalog.Parse("[item]\n" + string.Concat(rules.Products.Select(product =>
                $"{product.CommodityId} {product.ItemId} 1 0 0 100 0 0 0\n")) +
                "700101 490705385 1 0 0 100 0 0 0\n700102 490705385 1 0 0 100 0 0 0\n");
            var catalog = CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] {
                new PromotionConfiguration(rules, true), new PromotionConfiguration(other, true) }), shop, resources.Items.Get, _ => null);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("promotion-page-buy", () => { });
            var repository = new CharacterRepository(Path.Combine(directory, "game.db"), promotions: () => catalog);
            var created = repository.Create(owner, new("PageBuyer", towns.Room, towns.InitialReturn) { AcceptedStoryLevel = 1 });
            repository.CommitInventory(owner, created, new(created.Inventory,
                CurrencyWallet.Set(created.Inventory, CurrencyKind.Cera, 10000), "fund-page-buy"));
            session = new(repository, towns, owner, promotionStore: repository);
            session.Select(0); session.MarkReady();
            var capture = Begin(session, catalog);
            var cash = new InventoryCashShopService(resources.Items, shop, resources.GoldLimits, resources.EpicPieces, resources.Cargo);
            var promotion = new CashShopPromotionService(() => catalog, resources.Items.Get);
            var failed = session.BuyCashShop(new(0, [new(700199, 0)]), cash, promotion, out _, out var failedCommit);
            That(!failed.Accepted && failedCommit is null && session.PromotionPageSnapshotStatus(capture, catalog, 0) == "ready",
                "真实失败购买不推进页面floor或产生changed");
            var previous = session.Selected!;
            var ordinary = session.BuyCashShop(new(0, [new(700102, 0)]), cash, promotion, out _, out var ordinaryCommit);
            That(ordinary.Accepted && ordinaryCommit is null && !ReferenceEquals(previous, session.Selected) &&
                session.PromotionPageSnapshotStatus(capture, catalog, 0) == "ready" &&
                session.CapturePromotionPageChanged(catalog, new(Scope, 0, 0)) is null,
                "真实普通商城成功替换Selected而不推进促销floor或发changed");
            var otherResult = session.BuyCashShop(new(0, [new(700101, 0)]), cash, promotion, out _, out var otherCommit);
            That(otherResult.Accepted && otherCommit is not null && otherCommit.Progress.Scope == other.Scope &&
                session.PromotionPageSnapshotStatus(capture, catalog, 0) == "ready" &&
                session.CapturePromotionPageChanged(catalog, otherCommit.Progress) is null,
                "真实其它scope促销成功不推进或污染当前页面");
            VerifyCommitBeforePublication(session, owner, capture, catalog, cash, promotion).GetAwaiter().GetResult();
            var ledger = repository.ReadPage(owner, catalog.Activities.Single(entry => entry.Configuration.Rules.Scope == Scope));
            That(ledger.Verified && ledger.Progress is { Count: 2, Revision: 1 } && ledger.AwardMask27 == 1,
                "成功购买后的新读取恢复同一真实账本，不靠页面状态虚构累计");
        }
        finally { session?.End(); TestFiles.DeleteDirectory(directory, true); }
        Console.WriteLine($"Promotion page purchase integration: {Count - before} checks passed.");
    }

    static async Task VerifyCommitBeforePublication(GameSession session, AccountOwners.Lease owner,
        PromotionPageCapture capture, CashShopPromotionCatalog catalog, InventoryCashShopService cash,
        CashShopPromotionService promotion)
    {
        using var stop = new CancellationTokenSource();
        using var stream = new GameSenderBudgetTests.HeldStream();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => { });
        Task held = sender.Send(0, 2, [1], "held-before-page-buy");
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            That(sender.TryPost(0, 1709, EmptySnapshot(capture), "old-page-before-buy",
                () => !stop.IsCancellationRequested && session.PromotionPageSnapshotStatus(capture, catalog, 0) == "ready"),
                "旧snapshot已在真实sender队列中");
            var success = session.BuyCashShop(new(0, [new(105681, 0), new(105681, 0)]), cash, promotion,
                out _, out var committed);
            That(success.Accepted && committed?.Progress is { Count: 2, Revision: 1 } &&
                session.PromotionPageSnapshotStatus(capture, catalog, 0) != "ready" &&
                session.PromotionPageSnapshotStatus(capture, catalog, 1) == "ready",
                "真实Buy成功返回、任何ACK/Refresh发布前已在owner锁内推进floor");
            That(ReferenceEquals(session.CapturePromotionPageChanged(catalog, committed!.Progress), capture) &&
                session.PromotionPageChangedStatus(capture, catalog, committed.Progress.Revision) == "ready",
                "发布末端可捕获当前view发changed，正常购买资产revision变化不误失效");
            stream.Release.TrySetResult();
            await held.WaitAsync(TimeSpan.FromSeconds(5));
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            That(stream.Frames.Count == 1 && sender.PendingBytes == 0,
                "ACK前已推进的floor使真实发送泵丢弃旧snapshot，不等待发布末端才撤销");
        }
        finally { stream.Release.TrySetResult(); }
    }
}
