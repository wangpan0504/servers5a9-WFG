using System.Collections.Immutable;
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
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopPromotionSessionTests
{
    internal static readonly PromotionRules Rules = new(new("session-test", "period", "v1", "test", "package"),
        2, 1, 3, 2058, 1, 5000, [new(700101, 2058, 1)], [new(2, 2058, 1)]);
    internal static readonly CashShopCatalog Shop = CashShopCatalog.Parse(
        "[item]\n700101 2058 1 0 0 100 0 0 0\n700102 2058 1 0 0 100 0 0 0\n");

    internal static CashShopPromotionCatalog Catalog(ItemCatalog items, bool enabled = true, PromotionRules? rules = null) =>
        CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] { new PromotionConfiguration(rules ?? Rules, enabled) }),
            Shop, items.Get, candidate => candidate.Tiers.All(tier => tier.ItemId == 2058 && tier.Count == 1) &&
                candidate.OverflowItemId == 2058 && candidate.OverflowCount == 1 && items.Get(2058).Kind == ItemKind.Consumable
                ? null : "测试只验证普通恢复药完整实例和邮件消费者");

    public static void RunCore()
    {
        int checks = Count;
        var template = new ItemTemplate(2058, ItemKind.Consumable, 0, 1, 100, 1, 0, 0, 1, "[free]", ushort.MaxValue, 0, "合成普通附件");
        var catalog = CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] { new PromotionConfiguration(Rules, true) }),
            Shop, _ => template, candidate => candidate.Tiers.All(tier => tier.ItemId == template.Id) &&
                candidate.OverflowItemId == template.Id ? null : "非合成夹具附件");
        var service = new CashShopPromotionService(() => catalog, _ => template);
        var character = new DfoServer.Game.Characters.Character(1, 0, "Pure", 0, new(1, 0, 10, 10), null);
        var purchase = new CashShopPurchaseReceipt([new(700101, 2058, 1, 100, "item"),
            new(700102, 2058, 1, 100, "item"), new(700101, 2058, 1, 100, "item")],
            new Dictionary<int, IReadOnlyList<CashShopBonus>>());
        var now = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);
        var plan = service.Plan(character, new(character.Inventory, character.Inventory, "cash_shop_buy", new(character)),
            purchase, new(Rules.Scope, 29, 7), Rules, catalog.Revision, "pure", now);
        That(plan.Order.Units == 2 && plan.Order.ExpectedCount == 29 && plan.Order.ExpectedRevision == 7 &&
            plan.Awards.Select(row => row.PurchaseNumber).SequenceEqual(new long[] { 30, 31 }) &&
            plan.Deliveries is [var mail] && mail.Items.Length == 2 && mail.Items.All(row => row.Uid == 0) && mail.Expires is null,
            "纯规划仅累计实际活动行，冻结跨年原进度及完整UID0永久邮件");
        That(service.Plan(character, plan.Inventory, purchase, new(Rules.Scope, 29, 7), Rules, catalog.Revision, "pure", now.AddSeconds(1))
            .Awards.SequenceEqual(plan.Awards), "单纯跨年不改规则身份、不重置累计");
        var another = Rules with { Scope = Rules.Scope with { Group = "other" }, Products = [new(700102, 2058, 1)] };
        var multi = CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] { new PromotionConfiguration(Rules, true),
            new PromotionConfiguration(another, true) }), Shop, _ => template,
            candidate => candidate.OverflowItemId == template.Id ? null : "非合成附件");
        Reject<InvalidDataException>(() => CashShopPromotionService.Select(multi, new(0, [new(700101, 0), new(700102, 0)])),
            "多个已启用活动组整车拒绝，不只处理首组");
        var invalid = CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] { new PromotionConfiguration(Rules, true) }), Shop, _ => template);
        Reject<InvalidDataException>(() => CashShopPromotionService.Select(invalid, new(0, [new(700101, 0)])), "缺可信消费者时Invalid不能回落普通购买");
        Console.WriteLine($"Cash shop promotion planning: {Count - checks} checks passed.");
    }

    public static void Run(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-promotion-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = Catalog(resources.Items);
            var repository = new CharacterRepository(Path.Combine(root, "game.db"), promotions: () => catalog);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("promotion-session", () => { });
            var character = repository.Create(owner, new("Promotion", towns.Room, towns.InitialReturn) { AcceptedStoryLevel = 1 });
            repository.CommitInventory(owner, character, new(character.Inventory,
                CurrencyWallet.Set(character.Inventory, CurrencyKind.Cera, 10000), "seed"));
            var session = new GameSession(repository, towns, owner, promotionStore: repository);
            session.Select(0); session.MarkReady();
            var cash = new InventoryCashShopService(resources.Items, Shop, resources.GoldLimits, resources.EpicPieces, resources.Cargo);
            var service = new CashShopPromotionService(() => catalog, resources.Items.Get);
            var request = new CashShopPurchaseRequest(0, [new(700101, 0), new(700102, 0), new(700101, 0)]);
            var result = session.BuyCashShop(request, cash, service, out var purchase, out var committed);
            That(result.Accepted && purchase?.Products.Count == 3 && committed?.Progress.Count == 2 && committed.MailIds.Length == 1 &&
                session.Selected!.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 9700 &&
                repository.Read(owner, Rules.Scope).Count == 2,
                "原Buy活动两行加普通一行整车提交，只累计实际活动两套并持久投递一封赠礼");
            That(result.Receipt is { } receipt && ReferenceEquals(receipt.Character, session.Selected) &&
                session.CanPublishInventory(receipt) && receipt.Character.Inventory.Items.All(item => item.Uid > 0),
                "促销Session只安装实际持久UID角色并返回原库存发布回执");
            var current = repository.LoadCharacter(owner, 0)!;
            repository.CommitInventory(owner, current, new(current.Inventory, current.Inventory with { Gold = 1 }, "concurrent"));
            var saved = JsonSerializer.Serialize(repository.LoadCharacter(owner, 0));
            That(!session.BuyCashShop(request, cash, service, out _, out _).Accepted &&
                JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == saved && repository.Read(owner, Rules.Scope).Count == 2,
                "数据库已更新时旧快照整车拒绝，不扣费或增加资格");
            session.ReturnToRoster(); session.Select(0); session.MarkReady();
            That(session.BuyCashShop(request, cash, service, out _, out var retry).Accepted && retry?.Progress.Count == 4,
                "拒绝后重读真实角色与累计可正常购买，不永久卡住账号");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Cash shop promotion session: {Count - checks} checks passed.");
        CashShopPromotionPersistenceTests.CommittedBusyBoundary(towns, resources).GetAwaiter().GetResult();
        RunPilotLimitBoundary(towns, resources);
    }

    // 仅临时库启用原v1的5000套合同，实际购买与拒绝均走Session/Buy/事务。
    public static void RunPilotLimitBoundary(TownCatalog towns, ItemResources resources)
    {
        int checks = Count;
        var source = CashShopPromotionTests.PilotRules();
        var configuration = new PromotionConfiguration(source, true);
        var catalog = CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] { configuration }),
            resources.CashShop, resources.Items.Get, _ => null);
        var entry = catalog.Activities.Single();
        That(entry.Error is null && entry.Availability == PromotionAvailability.Active && source.MaximumPurchases == 5000 &&
            entry.Policy is { IsPermanent: false } policy && policy.CounterScope == source.Scope,
            "原v1 Session夹具只启用5000套pilot，不改变正式关闭配置");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-promotion-pilot-limit-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool completed = false;
        try
        {
            string database = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(database, promotions: () => catalog);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("promotion-pilot-limit-session", () => { });
            var character = repository.Create(owner, new("PilotLimit", towns.Room, towns.InitialReturn) { AcceptedStoryLevel = 1 });
            character = repository.CommitInventory(owner, character, new(character.Inventory,
                CurrencyWallet.Set(character.Inventory, CurrencyKind.Cera, 200000), "seed"));
            // 只初始化本临时账号已有的v1冻结规则及4999累计，之后不再直接更改进度。
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
            {
                db.Open();
                using var transaction = db.BeginTransaction();
                using var seed = db.CreateCommand();
                seed.Transaction = transaction;
                seed.CommandText = """
                    INSERT INTO cash_shop_promotion_rules(campaign,period,realm,promotion_group,rules_version,rules_snapshot)
                    VALUES($campaign,$period,$realm,$group,$version,$snapshot);
                    INSERT INTO cash_shop_promotion_progress(account,campaign,period,realm,promotion_group,count,revision)
                    VALUES($account,$campaign,$period,$realm,$group,4999,9);
                    """;
                seed.Parameters.AddWithValue("$campaign", source.Scope.Campaign);
                seed.Parameters.AddWithValue("$period", source.Scope.Period);
                seed.Parameters.AddWithValue("$realm", source.Scope.Realm);
                seed.Parameters.AddWithValue("$group", source.Scope.Group);
                seed.Parameters.AddWithValue("$version", source.Scope.RulesVersion);
                seed.Parameters.AddWithValue("$snapshot", CashShopPromotionCatalog.RuleSnapshot(source));
                seed.Parameters.AddWithValue("$account", owner.Account);
                seed.ExecuteNonQuery();
                transaction.Commit();
            }
            That(repository.Read(owner, entry) == new PromotionProgress(source.Scope, 4999, 9),
                "原v1 Session从唯一原累计读取4999，不另建规则期清零");
            var session = new GameSession(repository, towns, owner, promotionStore: repository);
            session.Select(0); session.MarkReady();
            var cash = new InventoryCashShopService(resources.Items, resources.CashShop,
                resources.GoldLimits, resources.EpicPieces, resources.Cargo);
            var service = new CashShopPromotionService(() => catalog, resources.Items.Get);
            foreach (var (commodity, price) in new[] { (105681, 39900), (108726, 35910) })
                That(resources.CashShop.Product(commodity) is { ItemId: 490705385, Count: 1 } actual && actual.CeraPrice == price,
                    "真实商城商品按各自原价或折扣价购买同一主货一套");
            string Snapshot()
            {
                using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False");
                db.Open();
                using var query = db.CreateCommand();
                query.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                var tables = new List<string>();
                using (var reader = query.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
                var rows = new List<object>();
                foreach (string table in tables)
                {
                    query.CommandText = "SELECT * FROM [" + table + "] ORDER BY rowid";
                    using var reader = query.ExecuteReader();
                    while (reader.Read()) rows.Add(new { Table = table,
                        Values = Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray() });
                }
                return JsonSerializer.Serialize(new { Selected = session.Selected, Rows = rows });
            }
            void RejectAtLimit(CashShopPurchaseRequest request, string message)
            {
                var selected = session.Selected;
                string saved = Snapshot();
                var rejected = session.BuyCashShop(request, cash, service, out var rejectedPurchase, out var rejectedCommit);
                That(!rejected.Accepted && rejected.Error == 17 && rejected.Receipt is null && rejectedPurchase is null && rejectedCommit is null &&
                    ReferenceEquals(selected, session.Selected) && Snapshot() == saved && session.PendingMailboxRestore() is null,
                    $"{message}，整车拒绝且角色、钱包、全部资产、邮件UID和促销四表零副作用：{JsonSerializer.Serialize(new
                    { rejected.Accepted, rejected.Error, rejected.RejectReason, Purchase = rejectedPurchase, Progress = rejectedCommit?.Progress })}");
            }
            RejectAtLimit(new(0, [new(105681, 0), new(105681, 0)]), "原v1累计4999时整车2套越过5000上限");
            int beforeMain = character.Inventory.Items.Where(item => item.Core.ItemId == 490705385).Sum(item => item.Core.Count);
            var result = session.BuyCashShop(new(0, [new(105681, 0)]), cash, service, out var purchase, out var committed);
            That(result.Accepted && purchase is { Products: [var product] } && product.CommodityId == 105681 &&
                committed is not null && committed.Progress == new PromotionProgress(source.Scope, 5000, 10) &&
                committed.MailIds.Length == 1 && session.Selected!.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 200000 - 39900 &&
                session.Selected.Inventory.Items.Where(item => item.Core.ItemId == 490705385).Sum(item => item.Core.Count) == beforeMain + 1,
                $"原v1真实Buy到5000完整扣费、发主货并投递本单赠礼：{JsonSerializer.Serialize(new
                {
                    Commodity = 105681, result.Accepted, result.Error, result.RejectReason,
                    Products = purchase?.Products, Progress = committed?.Progress, MailIds = committed?.MailIds,
                    Cera = session.Selected?.Inventory.AccountWallet.Get(CurrencyKind.Cera),
                    TokenCera = session.Selected?.Inventory.AccountWallet.Get(CurrencyKind.TokenCera),
                    HappyTokenCera = session.Selected?.Inventory.AccountWallet.Get(CurrencyKind.HappyTokenCera),
                    MainCount = session.Selected?.Inventory.Items.Where(item => item.Core.ItemId == 490705385).Sum(item => item.Core.Count),
                    ExpectedCera = 200000 - 39900, ExpectedMainCount = beforeMain + 1
                })}");
            That(result.Receipt is { } receipt && ReferenceEquals(receipt.Character, session.Selected) &&
                session.CanPublishInventory(receipt) && committed!.Character == session.Selected &&
                session.Selected!.Inventory.Items.All(item => item.Uid > 0) && session.PendingMailboxRestore() is null,
                "原v1 Session安装真实UID角色和可发布回执，城镇购买不创建回城邮箱恢复提示");
            RejectAtLimit(new(0, [new(105681, 0)]), "原v1到5000后下一单5001越过上限");
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var mails = repository.LoadMailbox(owner, character.Id, now).Entries;
            var mailIds = committed!.MailIds.ToArray();
            var attachments = mails.SelectMany(mail => mail.Attachments).ToArray();
            That(committed.OrderId != Guid.Empty && mailIds.Distinct().Count() == 1 &&
                mails.Length == 1 && mails.All(mail => mail.System && mail.Expires is null && mail.Attachments.Length == 1) &&
                mails.Select(mail => mail.Id).Order().SequenceEqual(mailIds.Order()) &&
                attachments.Length == 1 && attachments.Select(attachment => attachment.Item.Uid).Distinct().Count() == 1 &&
                attachments.All(attachment => attachment.Item.Uid > 0 && attachment.Item.Core.ItemId == 490705415 && attachment.Item.Core.Count == 1),
                "最后一套5000合法新订单恰一份415，越界拒绝不漏发、多发或新增邮件UID");
            var reopened = new CharacterRepository(database, promotions: () => catalog);
            var expected = session.Selected! with { Inventory = session.Selected!.Inventory with
            { Items = session.Selected.Inventory.Items.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray() } };
            That(reopened.Read(owner, entry) == new PromotionProgress(source.Scope, 5000, 10) &&
                JsonSerializer.Serialize(reopened.LoadCharacter(owner, 0)) == JsonSerializer.Serialize(expected) &&
                JsonSerializer.Serialize(reopened.LoadMailbox(owner, character.Id, now).Entries) == JsonSerializer.Serialize(mails),
                "重开仓储保留5000、完整扣费主货角色及唯一邮件，读取不重新购货");
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
            {
                db.Open();
                using var query = db.CreateCommand();
                query.CommandText = """
                    SELECT count(*)=1 AND sum(CASE WHEN campaign=$campaign AND period=$period AND realm=$realm
                        AND promotion_group=$group AND count=5000 AND revision=10 THEN 1 ELSE 0 END)=1
                    FROM cash_shop_promotion_progress WHERE account=$account
                    """;
                query.Parameters.AddWithValue("$campaign", source.Scope.Campaign);
                query.Parameters.AddWithValue("$period", source.Scope.Period);
                query.Parameters.AddWithValue("$realm", source.Scope.Realm);
                query.Parameters.AddWithValue("$group", source.Scope.Group);
                query.Parameters.AddWithValue("$account", owner.Account);
                That(query.ExecuteScalar() is 1L, "Session合法购买和两次越界拒绝只保留原v1 progress行，不建平行累计");
            }
            completed = true;
        }
        finally
        {
            if (completed) Directory.Delete(root, true);
            else Console.Error.WriteLine($"Pilot limit fixture retained: {root}");
        }
        Console.WriteLine($"Cash shop pilot session limit: {Count - checks} checks passed.");
    }
}
