using DfoServer.Game.CashShopPromotions;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopPromotionPersistenceTests
{
    public static void RunPageCore()
    {
        int before = Count;
        var failures = new List<Exception>();
        void Check(string name, Action action)
        {
            try { action(); }
            catch (Exception error) { failures.Add(new InvalidOperationException(name, error)); }
        }
        Check("未购账号只读零快照不建行", PageEmpty);
        Check("真实32套订单奖励账本及disabled恢复", PageCommitted);
        Check("缺奖错订单等不完整账本保已读进度", PageCorruption);
        Check("历史5001和Int64上界不钳制不循环补发", PageHistory);
        Check("账号隔离不读取他人累计", PageAccount);
        Check("领取删信不撤销已发事实", PageClaimed);
        Check("其它writer在场只申请只读快照", PageWriter);
        Check("旧owner不能取得新读取", PageOwner);
        if (failures.Count != 0)
            throw new AggregateException("Promotion ledger read behavioral failures", failures);
        Console.WriteLine($"Promotion ledger page: {Count - before} checks passed.");
    }

    static Fixture PageFixture() => new(new PromotionConfiguration(CashShopPromotionTests.PilotRules(), true));

    static PromotionLedgerSnapshot PageRead(Fixture f)
    {
        string before = f.Sql.Snapshot();
        var snapshot = f.Store.ReadPage(f.Owner, f.Catalog.Activities.Single());
        That(f.Sql.Snapshot() == before, "页面读取不改规则/累计/订单/奖励/邮件/资产及其它表");
        return snapshot;
    }

    static void PageEmpty()
    {
        using var f = PageFixture();
        f.Catalog = f.MakeCatalog(false);
        var snapshot = PageRead(f);
        That(snapshot.Verified && snapshot.Progress == new PromotionProgress(f.Rules.Scope, 0, 0) &&
            snapshot.AwardMask27 == 0 && snapshot.OverflowAwardCount == 0,
            "未购买账号应取得真实空账本，disabled不阻止只读");
    }

    static void PageCommitted()
    {
        using var f = PageFixture();
        var committed = f.Store.Commit(f.Owner, f.Plan(32), f.Now);
        f.Character = committed.Character;
        f.Catalog = f.MakeCatalog(false);
        var snapshot = PageRead(f);
        That(snapshot.Verified && snapshot.Progress == committed.Progress &&
            snapshot.AwardMask27 == 134217727 && snapshot.OverflowAwardCount == 2,
            "同一只读快照恢复32套真实订单、27档及两次追加，不把disabled变0");
    }

    static void PageCorruption()
    {
        // 每项只污染当前隔离临时库；正式读取必须返回不可核并保原进度，不修库。
        foreach (string sql in new[]
        {
            "DELETE FROM cash_shop_promotion_awards WHERE global_purchase=2",
            "UPDATE cash_shop_promotion_awards SET reward_snapshot=json_set(reward_snapshot,'$.Award.ItemId',490705449) WHERE global_purchase=2",
            "UPDATE cash_shop_promotion_awards SET reward_snapshot=json_set(reward_snapshot,'$.Item.Uid',0) WHERE global_purchase=2",
            "UPDATE cash_shop_promotion_awards SET mail_id=1073741823 WHERE global_purchase=2",
            "UPDATE cash_shop_promotion_awards SET rules_version='wrong' WHERE global_purchase=2",
            "UPDATE cash_shop_promotion_awards SET account='other' WHERE global_purchase=2",
            "UPDATE cash_shop_promotion_orders SET recipient_id=65535",
            "UPDATE cash_shop_promotion_orders SET count=31",
            "UPDATE cash_shop_promotion_orders SET revision=2",
            "UPDATE cash_shop_promotion_orders SET business_snapshot=json_set(business_snapshot,'$.Order.Units',31)"
        })
        {
            using var f = PageFixture();
            var committed = f.Store.Commit(f.Owner, f.Plan(32), f.Now);
            f.Character = committed.Character;
            f.Sql.Execute(sql);
            var snapshot = PageRead(f);
            That(!snapshot.Verified && snapshot.Progress == committed.Progress &&
                snapshot.AwardMask27 is null && snapshot.OverflowAwardCount is null,
                "缺奖/错奖励实例/邮件/规则/账号/recipient/区间/revision必须不可核且不假绿：" + sql);
        }
        using var rules = PageFixture();
        var saved = rules.Store.Commit(rules.Owner, rules.Plan(32), rules.Now);
        rules.Character = saved.Character;
        rules.Rules = rules.Rules with { OverflowItemId = 490705416 };
        rules.Catalog = rules.MakeCatalog(false);
        var changed = PageRead(rules);
        That(!changed.Verified && changed.Progress == saved.Progress &&
            changed.AwardMask27 is null && changed.OverflowAwardCount is null,
            "当前规则与持久绑定不同，不能重绑/清零或把旧奖励冒作新奖励");
    }

    static void PageHistory()
    {
        foreach (long count in new long[] { 5001, long.MaxValue })
        {
            using var f = PageFixture();
            var committed = f.Store.Commit(f.Owner, f.Plan(2), f.Now);
            f.Character = committed.Character;
            f.Sql.Execute("UPDATE cash_shop_promotion_progress SET count=" +
                count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            f.Catalog = f.MakeCatalog(false);
            var snapshot = PageRead(f);
            That(!snapshot.Verified && snapshot.Progress == new PromotionProgress(f.Rules.Scope, count, committed.Progress.Revision) &&
                snapshot.AwardMask27 is null && snapshot.OverflowAwardCount is null,
                "历史超界原值须恢复；缺订单不能按i64累计逐套循环、补发或伪5000");
        }
    }

    static void PageAccount()
    {
        using var f = PageFixture();
        var committed = f.Store.Commit(f.Owner, f.Plan(32), f.Now);
        f.Character = committed.Character;
        string before = f.Sql.Snapshot();
        var other = f.Store.ReadPage(f.Sql.OtherOwner, f.Catalog.Activities.Single());
        That(other.Verified && other.Progress == new PromotionProgress(f.Rules.Scope, 0, 0) &&
            other.AwardMask27 == 0 && other.OverflowAwardCount == 0 && f.Sql.Snapshot() == before,
            "同范围的另一个真实账号只能读自己的空账本，不能泄漏receiver累计");
    }

    static void PageClaimed()
    {
        using var f = PageFixture();
        var committed = f.Store.Commit(f.Owner, f.Plan(2), f.Now);
        f.Character = committed.Character;
        f.Sql.Receiver = f.Character;
        That(f.Sql.Claim(committed.MailIds).Accepted, "夹具先经真实邮件事务领取附件");
        f.Character = f.Sql.Receiver;
        That(f.Store.ChangeMailState(f.Owner, f.Character.Id, committed.MailIds, 0,
            f.Now.ToUnixTimeSeconds()).Accepted, "夹具经真实邮件事务删除已领取邮件");
        var snapshot = PageRead(f);
        That(snapshot.Verified && snapshot.Progress == committed.Progress &&
            snapshot.AwardMask27 == 1 && snapshot.OverflowAwardCount == 0,
            "奖励账本和订单仍证明已发，不依赖当前邮箱/附件仍存在");
    }

    static void PageWriter()
    {
        using var f = PageFixture();
        var committed = f.Store.Commit(f.Owner, f.Plan(2), f.Now);
        f.Character = committed.Character;
        string before = f.Sql.Snapshot();
        using (var db = f.Sql.Open())
        using (var writer = db.BeginTransaction(deferred: false))
        {
            var snapshot = f.Store.ReadPage(f.Owner, f.Catalog.Activities.Single());
            That(snapshot.Verified && snapshot.Progress == committed.Progress && snapshot.AwardMask27 == 1,
                "独立writer持有时页面读取仍只用BeginRead，不申请writer或补绑定");
        }
        That(f.Sql.Snapshot() == before, "writer在场只读完成后全表原样");
    }

    static void PageOwner()
    {
        using var f = PageFixture();
        string before = f.Sql.Snapshot();
        using var replacement = f.Sql.Owners.Acquire(f.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Store.ReadPage(f.Owner, f.Catalog.Activities.Single()),
            "过期账号lease不能查询新的账本结果");
        That(f.Sql.Snapshot() == before, "过期读取拒绝无写入");
    }
}