using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopPromotionPersistenceTests
{
    static PromotionConfiguration RetiredConfiguration() => new(CashShopPromotionTests.PilotRules() with
    {
        Scope = CashShopPromotionTests.PilotRules().Scope with { Period = "replica-permanent-1", RulesVersion = "v2" },
        MaximumPurchases = long.MaxValue
    }, true, RuleKind: "approved-permanent", CarryForward: new(CashShopPromotionTests.PilotRules().Scope,
        "B2C325D85D49C20050DB49718F6DFF676BAD03C244B5ADEED237DCBD9C499E28"));

    static void OriginalLimitAndRecovery()
    {
        using var f = new Fixture(new(CashShopPromotionTests.PilotRules(), true));
        var oldPlan = f.Plan(2);
        var old = f.Store.Commit(f.Owner, oldPlan, f.Now); f.Character = old.Character;
        f.Sql.Execute("UPDATE cash_shop_promotion_progress SET count=4999,revision=9");
        string oldBusiness = (string)f.Sql.Query("SELECT business_snapshot FROM cash_shop_promotion_orders")!;
        string oldReward = (string)f.Sql.Query("SELECT reward_snapshot FROM cash_shop_promotion_awards")!;
        string oldRules = (string)f.Sql.Query("SELECT rules_snapshot FROM cash_shop_promotion_rules")!;
        string before = f.Sql.Snapshot();
        Reject<ArgumentOutOfRangeException>(() => f.Plan(2), "原4999累计不能整车再购两套，不部分受理");
        That(f.Sql.Snapshot() == before, "4999越界整车不扣费、不发主货bonus或赠礼，不改四表和UID");
        var last = f.Store.Commit(f.Owner, f.Plan(1), f.Now); f.Character = last.Character;
        That(last.Progress == new PromotionProgress(f.Rules.Scope, 5000, 10) && last.MailIds.Length == 1 &&
            f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_progress WHERE period='replica-1' AND count=5000") is 1L &&
            f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_awards WHERE global_purchase=5000 AND json_extract(reward_snapshot,'$.Award.ItemId')=490705449") is 1L &&
            f.Character.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 9700,
            "原v1最后一套到5000全额扣费并提交唯一449奖励，不创建后继累计");
        before = f.Sql.Snapshot();
        Reject<ArgumentOutOfRangeException>(() => f.Plan(1), "达到5000后下一套新购整笔拒绝");
        That(f.Sql.Snapshot() == before, "5001请求不改钱包、主货、bonus、累计、订单、邮件或奖励UID");
        That((string)f.Sql.Query("SELECT business_snapshot FROM cash_shop_promotion_orders WHERE count=2")! == oldBusiness &&
            (string)f.Sql.Query("SELECT reward_snapshot FROM cash_shop_promotion_awards WHERE global_purchase=2")! == oldReward &&
            (string)f.Sql.Query("SELECT rules_snapshot FROM cash_shop_promotion_rules WHERE period='replica-1'")! == oldRules,
            "原规则、历史订单和奖励快照逐字保留");
        var reopened = new CharacterRepository(f.Sql.Path, promotions: () => f.Catalog, promotionClock: new FixedClock(f.Now));
        That(reopened.Read(f.Owner, f.Catalog.Activities[0]).Count == 5000, "重开仓储保留原5000累计");
        using (var db = f.Sql.Open())
        using (var writer = db.BeginTransaction(deferred: false))
            That(reopened.Commit(f.Owner, oldPlan, f.Now.AddYears(1)).Progress.Count == 2 &&
                reopened.RestoreCommitted(f.Owner, oldPlan.Order.Id)!.MailIds.SequenceEqual(old.MailIds) &&
                reopened.RestoreCommitted(f.Owner, Guid.NewGuid()) is null,
                "旧冻结plan与原GUID只读恢复已提交事实，不等待writer或覆盖当前5000");
        f.Catalog = f.MakeCatalog(false);
        That(reopened.Commit(f.Owner, oldPlan, f.Now).Progress.Count == 2 && f.Sql.Snapshot() == before,
            "关闭新购后原订单仍能只读恢复且全库不变");
        // 仅模拟历史已存超界事实；撤回新购权限不能钳制、清零或重写已有资产。
        f.Sql.Execute("UPDATE cash_shop_promotion_progress SET count=5001,revision=11");
        f.Catalog = f.MakeCatalog(true);
        before = f.Sql.Snapshot();
        That(reopened.Read(f.Owner, f.Rules.Scope).Count == 5001 &&
            reopened.RestoreCommitted(f.Owner, oldPlan.Order.Id)!.Progress.Count == 2,
            "历史5001事实与原订单可只读识别，不迁移清零或降成5000");
        Reject<ArgumentOutOfRangeException>(() => f.Plan(1), "历史超界累计也不得新增购买");
        using var other = f.Sql.Owners.Acquire("other-promotion-account", () => { });
        Reject<InvalidOperationException>(() => reopened.RestoreCommitted(other, oldPlan.Order.Id), "原GUID不能跨账号恢复");
        Reject<InvalidOperationException>(() => reopened.Commit(f.Owner, oldPlan with
            { Order = oldPlan.Order with { Units = 1 } }, f.Now), "旧订单不同业务不能借只读恢复绕过");
        That(f.Sql.Snapshot() == before, "历史超界读取与所有拒绝均无数据写入");
        f.Sql.Integrity();
    }

    static void RetiredContinuationConfiguration()
    {
        using var f = new Fixture(new(CashShopPromotionTests.PilotRules(), true));
        var oldPlan = f.Plan(2);
        var old = f.Store.Commit(f.Owner, oldPlan, f.Now); f.Character = old.Character;
        var uncommitted = f.Plan(1);
        foreach (var invalid in new[]
        {
            RetiredConfiguration(), RetiredConfiguration() with { CarryForward = null },
            RetiredConfiguration() with { RuleKind = "pilot" }, RetiredConfiguration() with { RuleKind = "unknown" },
            RetiredConfiguration() with { Rules = CashShopPromotionTests.PilotRules() },
            new PromotionConfiguration(CashShopPromotionTests.PilotRules() with { MaximumPurchases = 5001 }, true)
        })
        {
            f.Configuration = invalid; f.Rules = invalid.Rules; f.Catalog = f.MakeCatalog(true);
            string before = f.Sql.Snapshot();
            That(f.Catalog.Product(105681)?.Availability == PromotionAvailability.Invalid,
                "原常驻配置及任何5000以上规则均无新购授权");
            Reject<InvalidDataException>(() => CashShopPromotionService.Select(f.Catalog, new(0, [new(105681, 0)])),
                "无效累计配置不能回落普通商城购买");
            Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, uncommitted, f.Now),
                "撤权后的旧未提交plan也不能提交新单");
            That(f.Store.RestoreCommitted(f.Owner, oldPlan.Order.Id)!.MailIds.SequenceEqual(old.MailIds) && f.Sql.Snapshot() == before,
                "配置撤权只阻止新单，不改原GUID恢复与已提交资产");
        }
    }

    static void PilotRollbackAndRace()
    {
        foreach (string trigger in new[] {
            "AFTER INSERT ON cash_shop_promotion_rules", "AFTER UPDATE ON cash_shop_promotion_progress",
            "AFTER INSERT ON cash_shop_promotion_orders", "AFTER INSERT ON mailbox_audit_log WHEN NEW.action_name='system'",
            "AFTER INSERT ON mailbox_audit_log WHEN NEW.action_name='system' AND (SELECT count(*) FROM mailbox_audit_log WHERE action_name='system')=7",
            "AFTER INSERT ON cash_shop_promotion_awards",
            "AFTER INSERT ON cash_shop_promotion_awards WHEN (SELECT count(*) FROM cash_shop_promotion_awards)=61" })
        {
            using var f = new Fixture(new(CashShopPromotionTests.PilotRules(), true));
            var plan = f.Plan(64); string before = f.Sql.Snapshot();
            f.Sql.Execute($"CREATE TRIGGER promotion_limit_failure {trigger} BEGIN SELECT RAISE(ABORT,'promotion-limit-fault'); END");
            Reject<SqliteException>(() => f.Store.Commit(f.Owner, plan, f.Now), "原5000合同真实事务晚故障" + trigger);
            That(f.Sql.Snapshot() == before, "规则绑定、累计、钱包、主货bonus与全部邮件资格共同回滚" + trigger);
            f.Sql.Execute("DROP TRIGGER promotion_limit_failure"); f.Sql.Integrity();
        }
        using (var f = new Fixture(new(CashShopPromotionTests.PilotRules(), true)))
        {
            var first = f.Plan(2); var stale = f.Plan(2);
            f.Character = f.Store.Commit(f.Owner, first, f.Now).Character;
            string before = f.Sql.Snapshot();
            Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, stale, f.Now), "相同原累计的并发计划只有首单可提交");
            That(f.Sql.Snapshot() == before, "CAS竞争失败不自动重购或留下部分费用奖励");
            f.Sql.Execute($"UPDATE cash_shop_promotion_progress SET count=31,revision={long.MaxValue}");
            var revisionPlan = f.Plan(1); before = f.Sql.Snapshot();
            Reject<OverflowException>(() => f.Store.Commit(f.Owner, revisionPlan, f.Now), "revision上界仍在SQL之前checked拒绝");
            That(f.Sql.Snapshot() == before && f.Sql.Query("SELECT typeof(revision) FROM cash_shop_promotion_progress") is "integer",
                "revision溢出不写资产或转SQLite REAL");
        }
    }

    static void HistoricalPromotionMigration()
    {
        using var f = new Fixture(new(CashShopPromotionTests.PilotRules(), true));
        var plan = f.Plan(32); f.Character = f.Store.Commit(f.Owner, plan, f.Now).Character;
        string before = StableAssetSnapshot(f.Sql, includeBlacklist: true);
        string business = (string)f.Sql.Query("SELECT business_snapshot FROM cash_shop_promotion_orders")!;
        // 受影响私有77布局：保留原四表、原订单/UID/奖励；只移除续接规则列和约束，其它正式对象不变。
        using (var db = f.Sql.Open()) DatabaseSchemaTests.RemoveSkillTreeExpansionSchema(db);
        f.Sql.Execute("DROP TRIGGER cash_shop_promotion_valid_predecessor; DROP INDEX cash_shop_promotion_single_successor; " +
            "ALTER TABLE cash_shop_promotion_rules DROP COLUMN carry_rules_snapshot; " +
            "ALTER TABLE cash_shop_promotion_rules DROP COLUMN carry_group; " +
            "ALTER TABLE cash_shop_promotion_rules DROP COLUMN carry_realm; " +
            "ALTER TABLE cash_shop_promotion_rules DROP COLUMN carry_rules_version; " +
            "ALTER TABLE cash_shop_promotion_rules DROP COLUMN carry_period; " +
            "ALTER TABLE cash_shop_promotion_rules DROP COLUMN carry_campaign; " +
            "ALTER TABLE cash_shop_promotion_rules DROP COLUMN rule_kind; PRAGMA user_version=77");
        string legacy = f.Sql.Snapshot();
        f.Sql.Execute("CREATE INDEX cash_shop_promotion_single_successor ON cash_shop_promotion_rules(campaign)");
        Reject<InvalidDataException>(() => new CharacterRepository(f.Sql.Path), "私有77的未知后继约束在迁移前拒绝");
        That(f.Sql.Query("PRAGMA user_version") is 77L && f.Sql.Snapshot() == legacy &&
            f.Sql.Query("SELECT count(*) FROM pragma_table_info('cash_shop_promotion_rules') WHERE name='rule_kind'") is 0L,
            "非空私有77分类拒绝不残留列、数据或版本修改");
        f.Sql.Execute("DROP INDEX cash_shop_promotion_single_successor");
        _ = new CharacterRepository(f.Sql.Path);
        That(Convert.ToInt64(f.Sql.Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion && StableAssetSnapshot(f.Sql, includeBlacklist: true) == before &&
            f.Sql.Query("SELECT count(*) FROM characters WHERE selected_skill_tree<>-1") is 0L &&
            (string)f.Sql.Query("SELECT business_snapshot FROM cash_shop_promotion_orders")! == business &&
            f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_rules WHERE rule_kind=0 AND carry_rules_snapshot IS NULL") is 1L,
            "非空私有77升级156补规则元数据与空盾配置，订单资格原快照/UID/累计/钱包全部逐字保留");
        f.Configuration = new(CashShopPromotionTests.PilotRules(), true); f.Rules = f.Configuration.Rules; f.Catalog = f.MakeCatalog(true);
        That(f.Store.Commit(f.Owner, f.Plan(1), f.Now).Progress.Count == 33,
            "非空私有77升级后原v1正常购买接原32，不清零或重算历史");
        f.Sql.Integrity();
    }

    static void PilotIdentityAndRecipients()
    {
        using var f = new Fixture(new(CashShopPromotionTests.PilotRules(), true));
        var plan = f.Plan(2); var first = f.Store.Commit(f.Owner, plan, f.Now); f.Character = first.Character;
        var recipient = f.Sql.Other;
        var funded = f.Store.CommitInventory(f.Sql.OtherOwner, recipient, new(recipient.Inventory,
            recipient.Inventory with { AccountWallet = recipient.Inventory.AccountWallet.Set(CurrencyKind.Cera, 1000) }, "fund_second_account"));
        var service = new CashShopPromotionService(() => f.Catalog,
            id => new(id, ItemKind.Consumable, 0, 1, 0, 1, 0, 0, 0, "[free]", ushort.MaxValue, 0, "合成外盒"));
        var secondProgress = f.Store.Read(f.Sql.OtherOwner, f.Catalog.Activities[0]);
        var second = service.Plan(funded, new(funded.Inventory, funded.Inventory, "cash_shop_buy", new(funded)),
            new([new CashShopProduct(105681, 490705385, 1, 0, "item")], new Dictionary<int, IReadOnlyList<CashShopBonus>>()),
            secondProgress, f.Catalog.Activities[0], f.Catalog.Revision, f.Sql.OtherOwner.Account, f.Now);
        That(f.Store.Commit(f.Sql.OtherOwner, second, f.Now).Progress.Count == 1 &&
            f.Store.Read(f.Owner, f.Catalog.Activities[0]).Count == 2,
            "原v1两个账号分别保存各自累计");
        f.Sql.Receiver = f.Character;
        var mail = f.Store.LoadMailbox(f.Owner, f.Character.Id, f.Now.ToUnixTimeSeconds()).Entries.Single();
        var granted = mail.Attachments.Select(a => a.Item).ToImmutableArray();
        That(f.Sql.Claim(mail.Attachments.Select(a => a.ClaimId).ToImmutableArray()).Accepted,
            "原v1赠礼消耗品可合法领取，不以改类型绕过槽位校验");
        f.Character = f.Sql.Receiver;
        That(granted.All(item => item.Core.Kind == ItemKind.Consumable &&
            f.Character.Inventory.Items.Single(candidate => candidate.Uid == item.Uid) is { } actual &&
            actual == (item with { Position = actual.Position }) &&
            actual.Position.List == InventoryListType.Main &&
            actual.Position.Index >= ItemSlotBoundService.Range(ItemKind.Consumable, f.Character.Inventory).Start &&
            ItemSlotBoundService.Allows(f.Character.Inventory, actual.Position, actual.Core.Kind)),
            "原v1邮件奖励进入消耗品段并保留全部UID、核心与详情");
        That(JsonSerializer.Serialize(new CharacterRepository(f.Sql.Path).LoadCharacter(f.Owner, f.Character.Slot)) ==
            JsonSerializer.Serialize(f.Character with { Inventory = f.Character.Inventory with { Items = f.Character.Inventory.Items
                .OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray() } }),
            "原v1赠礼领取后重开仓储，完整角色及全部资产保持");
        That(f.Store.ChangeMailState(f.Owner, f.Character.Id, [mail.Id], 0, f.Now.ToUnixTimeSeconds()).Accepted,
            "原v1奖励仍走原邮件领取和删除链");
        string beforeRestore = f.Sql.Snapshot();
        That(f.Store.RestoreCommitted(f.Owner, plan.Order.Id)!.MailIds.SequenceEqual(first.MailIds) &&
            f.Sql.Snapshot() == beforeRestore, "删邮件后的受信ID恢复原mailIds且不补发");
        f.Store.Delete(f.Owner, f.Store.LoadDeletionTarget(f.Owner, f.Character.Slot));
        var rebuilt = f.Store.Create(f.Owner, new("Receiver", new(38, 1, 450, 234, 0), null) { Inventory = new([]) });
        That(rebuilt.Id != plan.Order.RecipientId && f.Store.Read(f.Owner, f.Catalog.Activities[0]).Count == 2,
            "原v1删角同名重建保留原账号累计");
        string beforeDeleted = f.Sql.Snapshot();
        Reject<InvalidOperationException>(() => f.Store.RestoreCommitted(f.Owner, plan.Order.Id), "已删原recipient不能转给同名新角");
        Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, plan, f.Now), "旧冻结plan恢复也不能转recipient");
        That(f.Sql.Snapshot() == beforeDeleted, "收件角色删除的恢复拒绝无写入");
        f.Character = f.Store.CommitInventory(f.Owner, rebuilt, new(rebuilt.Inventory,
            rebuilt.Inventory with { AccountWallet = rebuilt.Inventory.AccountWallet.Set(CurrencyKind.Cera, 10000) }, "refill_rebuilt"));
        var rebuiltPlan = f.Plan(1);
        That(f.Store.Commit(f.Owner, rebuiltPlan, f.Now).Progress.Count == 3 &&
            f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_progress WHERE account='receiver'") is 1L,
            "重建角色合法新单接第三套且不创建后继累计行");
        // 仅写入历史元数据夹具，不经已撤回的新配置授权创建后继购买。
        f.Sql.Execute("INSERT INTO cash_shop_promotion_rules " +
            "SELECT campaign,'replica-permanent-1',realm,promotion_group,'v2',rules_snapshot,1,campaign,period,rules_version,realm,promotion_group,rules_snapshot " +
            "FROM cash_shop_promotion_rules WHERE rule_kind=0");
        // 数据库自身拒绝分叉和多跳；不依赖目录恰好没有暴露这些目标。
        string beforeFork = f.Sql.Snapshot();
        Reject<SqliteException>(() => f.Sql.Execute("INSERT INTO cash_shop_promotion_rules " +
            "SELECT campaign,'fork',realm,promotion_group,rules_version,rules_snapshot,rule_kind,carry_campaign,carry_period,carry_rules_version,carry_realm,carry_group,carry_rules_snapshot " +
            "FROM cash_shop_promotion_rules WHERE rule_kind=1"), "同前驱唯一后继索引拒绝分叉");
        Reject<SqliteException>(() => f.Sql.Execute("INSERT INTO cash_shop_promotion_rules " +
            "SELECT campaign,'hop',realm,promotion_group,'v3',rules_snapshot,1,campaign,period,rules_version,realm,promotion_group,rules_snapshot " +
            "FROM cash_shop_promotion_rules WHERE rule_kind=1"), "精确前驱触发器拒绝多跳");
        That(f.Sql.Snapshot() == beforeFork, "分叉与多跳不改任何事实");
        string legacy = f.Sql.Snapshot();
        Reject<InvalidOperationException>(() => f.Store.Read(f.Owner, f.Catalog.Activities[0]),
            "已存历史后继仍阻止原规则分叉新购，不清旧累计重开额度");
        That(f.Store.RestoreCommitted(f.Owner, rebuiltPlan.Order.Id)!.Progress.Count == 3 && f.Sql.Snapshot() == legacy,
            "历史后继识别拒绝新购而原GUID仍只读恢复，全部资产保持");
        f.Sql.Integrity();
    }
}
