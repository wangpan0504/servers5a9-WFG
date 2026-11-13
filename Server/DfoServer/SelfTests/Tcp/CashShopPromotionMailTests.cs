using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CashShopPromotionMailTests
{
    public static Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        RunPurePlan4To10();
        RunSessionCart4To10(resources);
        return Task.CompletedTask;
    }

    static void RunPurePlan4To10()
    {
        int before = Count;
        var rules = Inventory.CashShopPromotionTests.PilotRules();
        var shop = CashShopCatalog.Parse("[item]\n" + string.Concat(rules.Products.Select(row =>
            $"{row.CommodityId} {row.ItemId} 1 0 0 100 0 0 0\n")));
        var config = new PromotionConfiguration(rules, true);
        ItemTemplate Template(int id) => new(id, ItemKind.Consumable, 0, 1, 0, 100, 0, 0, 0,
            "[free]", ushort.MaxValue, 0, "synthetic promotion mail test item");
        var catalog = CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] { config }), shop, Template, _ => null);
        var service = new CashShopPromotionService(() => catalog, Template);
        var character = new Character(1, 0, "PromotionMailPlan", 0, new(1, 0, 10, 10), null);
        var inventory = new InventoryPlan(character.Inventory, character.Inventory, "cash_shop_buy", new(character));
        var products = Enumerable.Repeat(new CashShopProduct(105681, 490705385, 1, 39900, "item"), 6).ToArray();
        var receipt = new CashShopPurchaseReceipt(products,
            new Dictionary<int, IReadOnlyList<CashShopBonus>>());
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var plan = service.Plan(character, inventory, receipt, new(rules.Scope, 4, 4), catalog.Activities.Single(),
            catalog.Revision, "promotion-mail-plan", now);
        int[] expectedIds = [490705444, 490705445, 490705446, 490705447, 490705448, 490705449];

        That(plan.Order.Units == 6 && plan.Order.ExpectedCount == 4 && plan.Awards.Select(row => row.PurchaseNumber)
                .SequenceEqual(Enumerable.Range(5, 6).Select(value => (long)value)) &&
            plan.Awards.Select(row => row.ItemId).SequenceEqual(expectedIds),
            "4→10纯计划生成第5至10套六个准确奖档");
        That(plan.Deliveries.Length == 6 && plan.Deliveries.SelectMany(mail => mail.Items)
                .Select(item => item.Core.ItemId).SequenceEqual(expectedIds) &&
            plan.Deliveries.All(mail => mail.Items.Length == 1 && mail.Expires is null),
            "4→10纯计划每个真实奖档独立成一封单附件永久邮件");
        Console.WriteLine($"Cash shop promotion mail plan (pure, no TCP): {Count - before} checks passed.");
    }

    static void RunSessionCart4To10(TcpTestResources testResources)
    {
        int before = Count;
        string testRoot = Path.Combine(Path.GetTempPath(), "dfo-cash-shop-promotion-mail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var towns = testResources.Towns;
            var resources = testResources.ItemResources;
            var rules = Inventory.CashShopPromotionTests.PilotRules();
            var clock = new Inventory.DailyBoosterTestClock(DateTimeOffset.Parse("2035-12-31T23:59:59Z"));
            var catalog = ServerRuntime.LoadCashShopPromotions(resources,
                JsonSerializer.Serialize(new[] { new PromotionConfiguration(rules, true) }),
                DfoServer.Game.Dungeons.HellPartyTicketConsumers.From(towns, testResources.Dungeons));
            var database = Path.Combine(testRoot, "game.db");
            var repository = new CharacterRepository(database, testResources.Quests, promotions: () => catalog, promotionClock: clock);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("promotion-mail-session", () => { });
            var character = repository.Create(owner, CharacterCreation.Prepare("PromoMail", testResources.Characters.Get(0),
                towns.Room, towns.InitialReturn, testResources.Learning.Get));
            character = repository.CommitInventory(owner, character, new(character.Inventory,
                CurrencyWallet.Set(character.Inventory, CurrencyKind.Cera, 1_000_000), "seed-promotion-mail"));
            var session = new GameSession(repository, towns, owner, timeProvider: clock, promotionStore: repository);
            session.Select(0);
            session.MarkReady();
            var cashShop = new InventoryCashShopService(resources.Items, resources.CashShop,
                resources.GoldLimits, resources.EpicPieces, resources.Cargo, clock: clock);
            cashShop.IgnoreAbsoluteExpiration = true;
            var promotions = new CashShopPromotionService(() => catalog,
                id => resources.Items.Get(id) with { Expiration = new() });

            for (int index = 0; index < 4; index++)
            {
                var seeded = Buy([105681]);
                That(seeded.Accepted && seeded.Commit?.Progress.Count == index + 1,
                    $"真实Session购物车模式2建立前置累计{index + 1}");
            }
            var progressBefore = repository.Read(owner, rules.Scope);
            var purchase = Buy(Enumerable.Repeat(105681, 6).ToArray());
            That(purchase.Accepted && purchase.Commit?.Progress.Count == 10 && progressBefore.Count == 4,
                "累计4后的模式2六套购买在Session/Repository中一次提交到10");

            int[] expectedIds = [490705444, 490705445, 490705446, 490705447, 490705448, 490705449];
            var mailIds = purchase.Commit!.MailIds;
            var mails = repository.LoadMailbox(owner, purchase.Commit.Character.Id, clock.GetUtcNow().ToUnixTimeSeconds())
                .Entries.Where(mail => mailIds.Contains(mail.Id)).ToArray();
            That(mailIds.Length == 6 && mailIds.Distinct().Count() == 6 && mails.Length == 6 &&
                mails.Select(mail => mail.Id).ToHashSet().SetEquals(mailIds) &&
                mails.All(mail => mail.Attachments.Length == 1),
                "新购五至十档各自使用独立MailId且每封只含一件附件");
            That(mails.Select(mail => mail.Attachments.Single().Item.Core.ItemId).Order()
                    .SequenceEqual(expectedIds.Order()) &&
                mails.Select(mail => mail.Attachments.Single().Item.Core.Count).All(count => count == 1) &&
                mails.Select(mail => mail.Attachments.Single().Item.Uid).All(uid => uid > 0),
                "独立邮件实际持久化六个不同档位模板各一件和真实UID，不接受同模板叠数量");

            var snapshot = repository.ReadPage(owner, catalog.Activities.Single());
            That(snapshot.Verified && snapshot.Progress == purchase.Commit.Progress && snapshot.AwardMask27 == 511,
                "逐档新邮件写入后只读账本仍核验累计及第2至10档资格");

            var mailbox = new MailboxService(repository, resources.Items, resources.GoldLimits, resources.EpicPieces);
            var claimIds = mails.SelectMany(mail => mail.Attachments).Select(item => item.ClaimId).ToImmutableArray();
            That(mailbox.PlanClaim(owner, purchase.Commit.Character, claimIds, clock.GetUtcNow(), out var claimPlan) ==
                    MailboxError.None && claimPlan is not null &&
                mailbox.Claim(owner, purchase.Commit.Character, claimPlan).Accepted,
                "六封独立奖档邮件均可经常规领取事务领取");
            var afterClaim = repository.ReadPage(owner, catalog.Activities.Single());
            That(afterClaim.Verified && afterClaim.Progress == purchase.Commit.Progress && afterClaim.AwardMask27 == 511,
                "领取附件不撤销新邮件账本的已发奖励事实");

            (bool Accepted, PromotionPurchaseCommit? Commit) Buy(int[] commodities)
            {
                var request = new CashShopPurchaseRequest(0,
                    commodities.Select(id => new CashShopPurchaseItem(id, 0)).ToArray()) { CurrencyMode = 2 };
                var result = session.BuyCashShop(request, cashShop, promotions, out _, out var committed);
                return (result.Accepted, committed);
            }
        }
        finally { TestFiles.DeleteDirectory(testRoot, true); }
        Console.WriteLine($"Cash shop promotion mail session (no TCP): {Count - before} checks passed.");
    }
}
