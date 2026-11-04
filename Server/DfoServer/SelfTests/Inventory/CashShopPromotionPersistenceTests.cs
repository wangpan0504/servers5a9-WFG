using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Persistence;
using DfoServer.Network.Protocol;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopPromotionPersistenceTests
{
    public static void RunCore()
    {
        That(SqliteMigrations.CurrentVersion >= 156, "保留正式1至155，更名卡资格顺延156");
        int before = Count;
        using var f = new Fixture();
        That(f.Sql.Query("SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('cash_shop_promotion_rules','cash_shop_promotion_orders','cash_shop_promotion_progress','cash_shop_promotion_awards')") is 4L,
            "商城累计事务四表在空临时库完整建立");
        string snapshot = f.Sql.Snapshot();
        That(f.Store.Read(f.Owner, f.Rules.Scope) == new PromotionProgress(f.Rules.Scope, 0, 0) && f.Sql.Snapshot() == snapshot,
            "未购买账号读取不建进度或规则行");
        using (var db = f.Sql.Open())
        using (var writer = db.BeginTransaction(deferred: false))
            That(f.Store.Read(f.Owner, f.Rules.Scope).Count == 0, "读累计在其它writer持有期间仍仅申请只读快照");
        ImmutableArray<ConsumableState> states = [
            new(ConsumableStateKind.Effect, 2058, f.Now.AddHours(1).ToUnixTimeMilliseconds(), 7, 1),
            new(ConsumableStateKind.Cooldown, 2058, f.Now.AddMinutes(1).ToUnixTimeMilliseconds(), 7, 1)];
        f.Character = f.Store.CommitInventory(f.Owner, f.Character,
            new(f.Character.Inventory, f.Character.Inventory with { ConsumableStates = states }, "seed_consumable_states"));
        var plan = f.Plan(12);
        plan.Inventory.ExpBonus = (f.Now.ToUnixTimeSeconds() + 600, 1000);
        var commit = f.Store.Commit(f.Owner, plan, f.Now);
        f.Character = commit.Character;
        That(commit.Progress.Count == 12 && commit.Progress.Revision == 1 && commit.MailIds.Length == 11 &&
            commit.Character.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 8800 &&
            commit.Character.Inventory.Items.Count(item => item.Core.ItemId is 51000 or 51003) == 2,
            "主货固定bonus扣款与十二套累计十一封逐档外盒原子提交");
        That(commit.Character.Inventory.ConsumableStates.SequenceEqual(states) &&
            f.Store.LoadCharacter(f.Owner, 0)!.Inventory.ConsumableStates.SequenceEqual(states),
            "促销完整事务及重读保留既有药效和冷却，不用促销快照覆盖新上游状态");
        That(f.Store.Read(f.Owner, f.Rules.Scope) == commit.Progress && f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_awards") is 11L,
            "读取真实累计与每套发奖唯一资格");
        That(f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_awards WHERE json_extract(reward_snapshot,'$.Award.PurchaseNumber')=global_purchase AND json_extract(reward_snapshot,'$.Item.Uid')>0") is 11L,
            "发奖来源全局序号与真实持久实例UID一起写入资格回执");
        var mails = f.Store.LoadMailbox(f.Owner, f.Character.Id, f.Now.ToUnixTimeSeconds()).Entries;
        That(mails.Length == 11 && mails.All(mail => mail.Attachments.Length == 1) &&
            mails.Sum(mail => mail.Attachments.Length) == 11 && mails.SelectMany(mail => mail.Attachments).All(a => a.Item.Uid > 0) &&
            mails.SelectMany(mail => mail.Attachments).Select(a => a.Item.Uid).Distinct().Count() == 11,
            "十一档各自独立邮件完整投递并分配不重复持久UID");
        snapshot = f.Sql.Snapshot();
        var replay = f.Store.Commit(f.Owner, plan, f.Now.AddYears(1));
        That(replay.Progress == commit.Progress && replay.MailIds.SequenceEqual(commit.MailIds) && f.Sql.Snapshot() == snapshot,
            "同订单相同业务恢复原事实且不再扣款分配或寄送");
        plan.Inventory.ExpBonus = (f.Now.ToUnixTimeSeconds() + 601, 1000);
        Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, plan, f.Now), "同订单不同限时效果业务快照拒绝");
        plan.Inventory.ExpBonus = (f.Now.ToUnixTimeSeconds() + 600, 1000);
        var samePrecondition = new InventoryPlan(plan.Inventory.Before, plan.Inventory.After, plan.Inventory.Reason,
            new(plan.Expected)) { ExpBonus = plan.Inventory.ExpBonus };
        var sameReplay = f.Store.Commit(f.Owner, plan with { Inventory = samePrecondition }, f.Now.AddYears(1));
        That(sameReplay.Progress == commit.Progress && sameReplay.MailIds.SequenceEqual(commit.MailIds) && f.Sql.Snapshot() == snapshot,
            "重建同义资格仍恢复原订单，不以资格对象引用判不同业务");
        var changedPrecondition = new InventoryPlan(plan.Inventory.Before, plan.Inventory.After, plan.Inventory.Reason,
            new(plan.Expected, requirePosition: true)) { ExpBonus = plan.Inventory.ExpBonus };
        Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, plan with { Inventory = changedPrecondition }, f.Now),
            "同订单不同地点资格业务快照拒绝");
        var changedDeadline = new InventoryPlan(plan.Inventory.Before, plan.Inventory.After, plan.Inventory.Reason,
            new(plan.Expected, f.Now.ToUnixTimeSeconds() + 60)) { ExpBonus = plan.Inventory.ExpBonus };
        Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, plan with { Inventory = changedDeadline }, f.Now),
            "同订单不同到期资格业务快照拒绝");
        var changedIdentity = new InventoryPlan(plan.Inventory.Before, plan.Inventory.After, plan.Inventory.Reason,
            new(plan.Expected with { Revision = plan.Expected.Revision + 1 })) { ExpBonus = plan.Inventory.ExpBonus };
        Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, plan with { Inventory = changedIdentity }, f.Now),
            "同订单私有资格身份与原Expected不符也拒绝");
        That(f.Sql.Snapshot() == snapshot, "资格业务冲突只读拒绝，不改订单累计扣款或邮件");
        f.Character = f.Store.CommitInventory(f.Owner, f.Character,
            new(f.Character.Inventory, f.Character.Inventory with { Gold = 123 }, "later_update"));
        var current = f.Store.Commit(f.Owner, plan, f.Now);
        That(current.Character.Revision == f.Character.Revision && current.Character.Inventory.Gold == 123 && current.Progress == commit.Progress,
            "历史订单只返回当前合法角色不会倒灌历史库存");
        snapshot = f.Sql.Snapshot();
        Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, plan with { Order = plan.Order with { Units = 11 } }, f.Now), "同ID不同业务拒绝");
        That(f.Sql.Snapshot() == snapshot, "ID冲突不改任何数据");
        var next = f.Store.Commit(f.Owner, f.Plan(12), f.Now);
        f.Character = next.Character;
        That(next.Progress.Count == 24 && next.Progress.Revision == 2 && next.MailIds.All(id => !commit.MailIds.Contains(id)) &&
            f.Character.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 7600,
            "不同订单相同购买内容合法复购不按包体去重");
        using (var db = f.Sql.Open())
        using (var writer = db.BeginTransaction(deferred: false))
        {
            var restored = f.Store.Commit(f.Owner, plan with { CatalogRevision = long.MaxValue }, f.Now);
            That(restored.Progress.Count == 12 && restored.Character.Revision == next.Character.Revision &&
                restored.MailIds.SequenceEqual(commit.MailIds), "后续购买后旧订单只读恢复原累计和当前角色且不申请writer");
        }
        Rollback();
        InvalidPlans();
        FreezeAndRecovery();
        Migration();
        Legacy64Migration();
        Formal153Migration();
        RunWriterGate();
        ClockBoundaries();
        PurchaseExpirationBoundaries();
        RenewalExpirationBoundary();
        CatalogBoundaries();
        OriginalLimitAndRecovery();
        RetiredContinuationConfiguration();
        PilotRollbackAndRace();
        HistoricalPromotionMigration();
        PilotIdentityAndRecipients();
        Console.WriteLine($"Cash shop promotion persistence: {Count - before} checks passed.");
    }

    static void Rollback()
    {
        // SQL触发器在真实写路径抛故障，不在正式仓储添加测试专用断点。
        foreach (string trigger in new[]
        {
            "BEFORE INSERT ON cash_shop_promotion_orders",
            "AFTER UPDATE ON cash_shop_promotion_progress",
            "AFTER INSERT ON mailbox_audit_log WHEN NEW.action_name='system' AND (SELECT count(*) FROM mailbox_audit_log WHERE action_name='system')=1",
            "AFTER INSERT ON mailbox_audit_log WHEN NEW.action_name='system' AND (SELECT count(*) FROM mailbox_audit_log WHERE action_name='system')=2",
            "AFTER INSERT ON mailbox_attachments WHEN (SELECT count(*) FROM mailbox_attachments)>=10",
            "AFTER INSERT ON cash_shop_promotion_awards"
        })
        {
            using var f = new Fixture();
            var plan = f.Plan(12);
            string before = f.Sql.Snapshot();
            f.Sql.Execute($"CREATE TRIGGER fail_promotion {trigger} BEGIN SELECT RAISE(ABORT,'promotion-fault'); END");
            Reject<SqliteException>(() => f.Store.Commit(f.Owner, plan, f.Now), "事务断点" + trigger);
            That(f.Sql.Snapshot() == before, "故障回滚主货bonus钱包角色进度订单资格邮件及全部审计" + trigger);
            f.Sql.Execute("DROP TRIGGER fail_promotion");
            f.Sql.Integrity();
        }
    }

    static void InvalidPlans()
    {
        using var f = new Fixture();
        var plan = f.Plan(12);
        foreach (var bad in new[]
        {
            plan with { Order = plan.Order with { ExpectedRevision = 1 } },
            plan with { Order = plan.Order with { ExpectedCount = 1 } },
            plan with { Awards = plan.Awards.RemoveAt(0) },
            plan with { Deliveries = plan.Deliveries.RemoveAt(0) },
            plan with { Deliveries = plan.Deliveries.SetItem(0, plan.Deliveries[0] with { Gold = 1 }) },
            plan with { Deliveries = plan.Deliveries.SetItem(0, plan.Deliveries[0] with { Recipient = plan.Deliveries[0].Recipient with { Name = "已消失" } }) },
            plan with { Deliveries = plan.Deliveries.SetItem(0, plan.Deliveries[0] with { Items = plan.Deliveries[0].Items.SetItem(0, plan.Deliveries[0].Items[0] with { Uid = 123 }) }) },
            plan with { Inventory = new(plan.Inventory.Before, plan.Inventory.After, "cash_shop_buy", new(plan.Expected, f.Now.ToUnixTimeSeconds() - 1)) },
            plan with { Rules = f.Rules with { OverflowItemId = 51009 }, Policy = null }
        })
        {
            string before = f.Sql.Snapshot();
            Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, bad, f.Now), "坏CAS规则或不完整邮件规划拒绝");
            That(f.Sql.Snapshot() == before, "坏规划不产生部分状态");
        }
        var historical = plan.Order with { PlannedAt = f.Now.AddMinutes(-2) };
        var expired = plan with
        {
            Order = historical,
            Deliveries = plan.Deliveries.Select((mail, part) => mail with
            {
                Created = historical.PlannedAt.ToUnixTimeSeconds(),
                Items = part == 0 ? mail.Items.SetItem(0, mail.Items[0] with
                    { Core = mail.Items[0].Core with { ExpireTime = checked((int)f.Now.ToUnixTimeSeconds()) } }) : mail.Items
            }).ToImmutableArray()
        };
        Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, expired, historical.PlannedAt), "调用方旧时刻不能越过事务真实道具等号到期");
        f.Character = f.Store.CommitInventory(f.Owner, f.Character,
            new(f.Character.Inventory, f.Character.Inventory with { Gold = 1 }, "new_revision"));
        Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, plan, f.Now), "旧角色revision拒绝");
        var valid = f.Plan(12);
        f.Catalog = f.MakeCatalog(false);
        Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, valid, f.Now), "关闭拒绝旧计划");
        f.Catalog = f.MakeCatalog(true);
        Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, valid, f.Now), "关再开ABA拒绝");
        var fresh = f.Plan(12);
        var missing = new CharacterRepository(f.Sql.Path);
        Reject<InvalidOperationException>(() => missing.Commit(f.Owner, fresh, f.Now), "缺装配目录新交易拒绝");
        using var nextOwner = f.Sql.Owners.Acquire(f.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, fresh, f.Now), "旧owner拒绝");
        using var cas = new Fixture();
        var staleCount = cas.Plan(12);
        cas.Character = cas.Store.Commit(cas.Owner, cas.Plan(1), cas.Now).Character;
        string casBefore = cas.Sql.Snapshot();
        Reject<InvalidOperationException>(() => cas.Store.Commit(cas.Owner, staleCount, cas.Now), "已有进度后旧CAS拒绝");
        That(cas.Sql.Snapshot() == casBefore, "CAS冲突不改原累计或产生奖励");
    }

    static void FreezeAndRecovery()
    {
        using var f = new Fixture();
        var plan = f.Plan(3, special: true);
        var first = f.Store.Commit(f.Owner, plan, f.Now);
        f.Character = first.Character;
        var stored = f.Store.LoadMailbox(f.Owner, f.Character.Id, f.Now.ToUnixTimeSeconds()).Entries
            .Where(mail => first.MailIds.Contains(mail.Id)).OrderBy(mail => mail.Id).ToArray();
        That(stored.Length == 2 && stored.All(mail => mail.Attachments.Length == 1) &&
            stored.SelectMany(mail => mail.Attachments).Any(item => item.Item.Avatar?.Color1 == 7) &&
            stored.SelectMany(mail => mail.Attachments).Any(item => item.Item.Creature?.Name == "赠礼宠物"),
            "时装和宠物各自随对应逐档邮件及真实UID保存");
        var reopened = new CharacterRepository(f.Sql.Path);
        That(JsonSerializer.Serialize(reopened.LoadMailbox(f.Owner, f.Character.Id, f.Now.ToUnixTimeSeconds())) ==
            JsonSerializer.Serialize(f.Store.LoadMailbox(f.Owner, f.Character.Id, f.Now.ToUnixTimeSeconds())) &&
            reopened.Read(f.Owner, f.Rules.Scope) == first.Progress,
            "仓储重开恢复完整UID详情来源及账号累计");
        f.Catalog = f.MakeCatalog(false);
        That(reopened.Commit(f.Owner, plan, f.Now).MailIds.SequenceEqual(first.MailIds), "关闭后已提交订单仍可只读恢复");
        f.Catalog = f.MakeCatalog(true);
        foreach (var changed in new[] { f.Rules with { Scope = f.Rules.Scope with { RulesVersion = "v2" } }, f.Rules with { OverflowItemId = 51009 } })
        {
            var original = f.Rules;
            f.Rules = changed;
            f.Catalog = f.MakeCatalog(true);
            string before = f.Sql.Snapshot();
            Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, f.Plan(1), f.Now), "同规则期换版本或奖励内容拒绝");
            That(f.Sql.Snapshot() == before, "同规则期不换计数不重复发奖");
            f.Rules = original;
        }
        f.Rules = f.Rules with { Scope = f.Rules.Scope with { Period = "next" } };
        f.Catalog = f.MakeCatalog(true);
        That(f.Store.Read(f.Owner, f.Rules.Scope).Count == 0, "真实新期独立从零起算");
        var newPeriod = f.Store.Commit(f.Owner, f.Plan(1), f.Now);
        f.Character = newPeriod.Character;
        That(newPeriod.Progress.Count == 1 && f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_progress") is 2L, "新期保留旧期事实");
        // 沿原邮件领取事务将真实附件移入背包，再删除邮件、角色和重建同名角色。
        f.Sql.Receiver = f.Character;
        f.Sql.Claim(stored.SelectMany(mail => mail.Attachments).Select(a => a.ClaimId).ToImmutableArray());
        f.Character = f.Sql.Receiver;
        That(f.Store.ChangeMailState(f.Owner, f.Character.Id, first.MailIds, 0, f.Now.ToUnixTimeSeconds()).Accepted,
            "逐封领取完整奖项后正常删除邮件不受历史资格外键阻止");
        That(f.Store.Commit(f.Owner, plan, f.Now).MailIds.SequenceEqual(first.MailIds), "删邮件后重放只读原mailId绝不补发");
        f.Store.Delete(f.Owner, f.Store.LoadDeletionTarget(f.Owner, f.Character.Slot));
        var rebuilt = f.Store.Create(f.Owner, new("Receiver", new(38, 1, 450, 234, 0), null) { Inventory = new([]) });
        That(rebuilt.Id != plan.Order.RecipientId && f.Store.Read(f.Owner, plan.Order.Scope).Count == 3 &&
            f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_awards") is 2L, "删角重建同名保留账号累计与不可再领资格");
        string snapshot = f.Sql.Snapshot();
        Reject<InvalidOperationException>(() => f.Store.Commit(f.Owner, plan, f.Now), "原收件人消失旧订单安全拒绝");
        That(f.Sql.Snapshot() == snapshot && f.Store.LoadMailbox(f.Owner, rebuilt.Id, f.Now.ToUnixTimeSeconds()).Entries.IsEmpty,
            "同名新角色不得接收旧订单奖励");
        f.Rules = f.Rules with { Scope = plan.Order.Scope };
        f.Catalog = f.MakeCatalog(true);
        f.Character = f.Store.CommitInventory(f.Owner, rebuilt, new(rebuilt.Inventory,
            rebuilt.Inventory with { AccountWallet = rebuilt.Inventory.AccountWallet.Set(CurrencyKind.Cera, 10000) }, "refill_fixture"));
        var rebuiltPurchase = f.Store.Commit(f.Owner, f.Plan(1), f.Now);
        That(rebuiltPurchase.Progress.Count == 4 && f.Store.LoadMailbox(f.Owner, rebuilt.Id, f.Now.ToUnixTimeSeconds()).Entries.Single().Attachments.Length == 1 &&
            f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_awards") is 3L,
            "重建角色继续账号下一档而不重复旧资格");
        f.Sql.Integrity();
        using var budget = new Fixture();
        budget.Character = budget.Store.Commit(budget.Owner, budget.Plan(2), budget.Now).Character;
        var maximum = budget.Store.Commit(budget.Owner, budget.Plan(64), budget.Now);
        That(maximum.Progress.Count == 66 && maximum.MailIds.Length == 64 && maximum.MailIds.Distinct().Count() == 64 &&
            budget.Sql.Query("SELECT count(*) FROM mailbox_attachments") is 65L,
            "最坏单车64奖项各有独立邮件和附件，不受单次邮箱页大小截断");
        int drained = 0;
        while (true)
        {
            var page = budget.Store.LoadMailbox(budget.Owner, maximum.Character.Id, budget.Now.ToUnixTimeSeconds());
            if (page.Entries.IsEmpty) break;
            That(page.Entries.Length <= MailboxService.PageSize && page.Entries.All(mail => mail.Attachments.Length == 1),
                "64奖邮件按公共20封邮箱页读取且逐封一附件");
            if (drained == 0)
                That(page.Entries.Length == MailboxService.PageSize && page.NotLoaded == 45,
                    "总65封邮件首屏保留20封上限并报告其余45封待加载");
            var mailIds = page.Entries.Select(mail => mail.Id).ToImmutableArray();
            var attachments = page.Entries.SelectMany(mail => mail.Attachments).ToArray();
            var claims = attachments.Select(item => item.ClaimId).ToImmutableArray();
            budget.Sql.Receiver = budget.Store.LoadCharacter(budget.Owner, 0)!;
            That(budget.Sql.Claim(claims).Accepted,
                "当前20封分页内的逐档奖励可领取");
            var received = budget.Sql.Receiver;
            var consumedUids = attachments.Select(item => item.Item.Uid).ToHashSet();
            budget.Sql.Receiver = budget.Store.CommitInventory(budget.Owner, received,
                new(received.Inventory, received.Inventory with
                { Items = received.Inventory.Items.Where(item => !consumedUids.Contains(item.Uid)).ToImmutableArray() }, "consume_bulk_mail_fixture"));
            That(budget.Store.ChangeMailState(budget.Owner, maximum.Character.Id, mailIds, 0,
                    budget.Now.ToUnixTimeSeconds()).Accepted,
                "领取并删除当前已空档案以恢复后续邮箱批次");
            budget.Character = budget.Sql.Receiver;
            drained += page.Entries.Length;
        }
        That(drained == 65 && budget.Store.Read(budget.Owner, budget.Rules.Scope).Count == 66 &&
            budget.Store.LoadMailbox(budget.Owner, maximum.Character.Id, budget.Now.ToUnixTimeSeconds()).NotLoaded == 0,
            "超过邮箱页大小的奖励逐批可读、可领、可删除且累计账本不丢");
    }

    static void Migration()
    {
        using var f = new Fixture();
        f.Sql.Send([f.Sql.Sender.Inventory.Items[2], f.Sql.Sender.Inventory.Items[3]], 100);
        f.Sql.Execute($"INSERT INTO character_skills(character_id,skill_id,level) VALUES({f.Character.Id},100,1)");
        f.Sql.Execute($"INSERT INTO character_consumable_states(character_id,account,kind,item_id,until_ms,maintenance,cool_group) VALUES({f.Character.Id},'{f.Owner.Account}',0,2058,{f.Now.AddHours(1).ToUnixTimeMilliseconds()},7,1)");
        f.Sql.Execute($"UPDATE characters SET blacklist_count=17 WHERE id={f.Character.Id}");
        string original = StableAssetSnapshot(f.Sql, includeBlacklist: true);
        string freshSchema = (string)f.Sql.Query("SELECT group_concat(sql,'') FROM (SELECT sql FROM sqlite_schema WHERE name LIKE 'cash_shop_promotion_%' ORDER BY name)")!;
        using (var db = f.Sql.Open()) DatabaseSchemaTests.RemoveSkillTreeExpansionSchema(db);
        f.Sql.Execute("DROP TABLE cash_shop_promotion_awards; DROP TABLE cash_shop_promotion_progress; DROP TABLE cash_shop_promotion_orders; DROP TABLE cash_shop_promotion_rules; PRAGMA user_version=65");
        string before = f.Sql.Snapshot();
        f.Sql.Execute("CREATE TABLE cash_shop_promotion_orders(broken INTEGER)");
        Reject<InvalidDataException>(() => new CharacterRepository(f.Sql.Path), "迁移153拒绝部分促销结构并回滚");
        That(f.Sql.Query("PRAGMA user_version") is 65L && f.Sql.Query("SELECT count(*) FROM sqlite_schema WHERE name='cash_shop_promotion_rules'") is 0L &&
            f.Sql.Query($"SELECT blacklist_count FROM characters WHERE id={f.Character.Id}") is 17L,
            "促销153迁移拒绝部分结构，整体回滚前置DDL并保留旧65及黑名单计分");
        f.Sql.Execute("DROP TABLE cash_shop_promotion_orders");
        That(f.Sql.Snapshot() == before, "迁移失败保留原钱包角色库存邮件任务技能和持续状态");
        _ = new CharacterRepository(f.Sql.Path);
        That(StableAssetSnapshot(f.Sql, includeBlacklist: true) == original && Convert.ToInt64(f.Sql.Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion &&
            f.Sql.Query("SELECT count(*) FROM characters WHERE selected_skill_tree<>-1") is 0L, "受影响v65布局连续正式迁移及促销154/155，保留黑名单计分持续状态与所有原数据");
        That(f.Sql.Query("SELECT group_concat(sql,'') FROM (SELECT sql FROM sqlite_schema WHERE name LIKE 'cash_shop_promotion_%' ORDER BY name)") is string upgradedSchema && upgradedSchema.Replace("\r\n", "\n", StringComparison.Ordinal) == freshSchema.Replace("\r\n", "\n", StringComparison.Ordinal),
            "新库完整SQL与65到106迁移的四表索引和冻结约束完全一致");
        f.Sql.Integrity();
        f.Sql.Execute("DROP TABLE cash_shop_promotion_awards");
        f.Sql.Execute(CashShopPromotionSchema.Sql.Replace("PRIMARY KEY(account,campaign,period,realm,promotion_group,rules_version,global_purchase)", "CHECK(global_purchase>0)", StringComparison.Ordinal));
        Reject<InvalidDataException>(() => new CharacterRepository(f.Sql.Path), "资格唯一约束残缺不能仅凭列齐全进入运行");
        f.Sql.Execute("DROP TABLE cash_shop_promotion_awards");
        f.Sql.Execute(CashShopPromotionSchema.Sql);
        f.Sql.Execute("DROP TRIGGER cash_shop_promotion_rules_immutable_update");
        Reject<InvalidDataException>(() => new CharacterRepository(f.Sql.Path), "冻结约束缺失不得开放写入");
    }

    static void Formal153Migration()
    {
        using var f = new Fixture();
        f.Sql.Execute($"INSERT INTO character_ekern_cycle(character_id,account,service_day,normal_clears,trial_clears) SELECT id,account,20261029,2,1 FROM characters WHERE id={f.Character.Id}; " +
            "DROP TABLE cash_shop_promotion_awards; DROP TABLE cash_shop_promotion_progress; DROP TABLE cash_shop_promotion_orders; DROP TABLE cash_shop_promotion_rules; PRAGMA user_version=153");
        string before = StableAssetSnapshot(f.Sql, includeBlacklist: true);
        _ = new CharacterRepository(f.Sql.Path);
        That(Convert.ToInt64(f.Sql.Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion && StableAssetSnapshot(f.Sql, includeBlacklist: true) == before &&
            f.Sql.Query($"SELECT count(*) FROM character_ekern_cycle WHERE character_id={f.Character.Id} AND service_day=20261029 AND normal_clears=2 AND trial_clears=1") is 1L &&
            f.Sql.Query("SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('cash_shop_promotion_rules','cash_shop_promotion_orders','cash_shop_promotion_progress','cash_shop_promotion_awards')") is 4L,
            "正式153非空艾肯循环升级156新增促销与空更名资格，原通关与全部旧资产逐字保留");
    }

    static void Legacy64Migration()
    {
        using var f = new Fixture();
        f.Sql.Send([f.Sql.Sender.Inventory.Items[2]], 100);
        string before = StableAssetSnapshot(f.Sql);
        string fresh = (string)f.Sql.Query("SELECT group_concat(sql,'') FROM (SELECT sql FROM sqlite_schema WHERE name LIKE 'cash_shop_promotion_%' ORDER BY name)")!;
        using (var db = f.Sql.Open()) DatabaseSchemaTests.RemoveSkillTreeExpansionSchema(db);
        f.Sql.Execute("DROP TABLE cash_shop_promotion_awards; DROP TABLE cash_shop_promotion_progress; DROP TABLE cash_shop_promotion_orders; DROP TABLE cash_shop_promotion_rules; ALTER TABLE characters DROP COLUMN blacklist_count; PRAGMA user_version=64");
        _ = new CharacterRepository(f.Sql.Path);
        That(Convert.ToInt64(f.Sql.Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion && f.Sql.Query("SELECT max(blacklist_count) FROM characters") is 0L &&
            f.Sql.Query("SELECT count(*) FROM characters WHERE selected_skill_tree<>-1") is 0L &&
            StableAssetSnapshot(f.Sql) == before, "受影响64布局连续补正式迁移与促销154/155，全部旧资产原样且计分默认为0");
        That(f.Sql.Query("SELECT group_concat(sql,'') FROM (SELECT sql FROM sqlite_schema WHERE name LIKE 'cash_shop_promotion_%' ORDER BY name)") is string migrated && migrated.Replace("\r\n", "\n", StringComparison.Ordinal) == fresh.Replace("\r\n", "\n", StringComparison.Ordinal),
            "64连续升级的促销完整结构与新库一致");
        f.Sql.Integrity();
    }

    // ALTER ADD COLUMN会改变物理列顺序；按列名排序比较全部旧资产，不把DDL布局差异当数据变化。
    static string StableAssetSnapshot(MailboxPersistenceTests.Fixture fixture, bool includeBlacklist = false)
    {
        using var db = fixture.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var tables = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        var rows = new List<object>();
        foreach (string table in tables)
        {
            command.CommandText = "SELECT name FROM pragma_table_info($table) " +
                (includeBlacklist ? "" : "WHERE name!='blacklist_count' ") + "ORDER BY name";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$table", table);
            var columns = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(0));
            command.Parameters.Clear();
            command.CommandText = "SELECT " + string.Join(',', columns.Select(column => "[" + column + "]")) + " FROM [" + table + "] ORDER BY rowid";
            using var values = command.ExecuteReader();
            while (values.Read()) rows.Add(new { Table = table, Values = Enumerable.Range(0, values.FieldCount).Select(values.GetValue).ToArray() });
        }
        return JsonSerializer.Serialize(rows);
    }

    public static void RunWriterGate()
    {
        foreach (int change in new[] { 0, 1, 2 })
        {
            using var f = new Fixture();
            var plan = f.Plan(2);
            var clock = new MutableClock(f.Now);
            if (change == 2)
                plan = plan with { Deliveries = [plan.Deliveries[0] with { Items = [plan.Deliveries[0].Items[0] with
                    { Core = plan.Deliveries[0].Items[0].Core with { ExpireTime = checked((int)f.Now.ToUnixTimeSeconds()) + 1 } }] }] };
            string before = f.Sql.Snapshot();
            var gateDatabase = new SqliteGameDatabase(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Sql.Path)!, "gate.db"));
            using var entered = new ManualResetEventSlim();
            var store = new CharacterRepository(f.Sql.Path, promotions: () => { entered.Set(); return f.Catalog; }, promotionClock: clock);
            Task<PromotionPurchaseCommit> pending;
            bool bypassed;
            using (var db = gateDatabase.Open())
            using (var write = db.BeginWrite("CashShopPromotionPersistenceTests.HoldUnrelatedWriter"))
            {
                pending = Task.Run(() => store.Commit(f.Owner, plan, f.Now));
                That(entered.Wait(TimeSpan.FromSeconds(5)), "促销提交已完成写前目录检查");
                bypassed = pending.Wait(TimeSpan.FromMilliseconds(150));
                if (change == 1) f.Catalog = f.MakeCatalog(false);
                if (change == 2) clock.Now = f.Now.AddSeconds(1);
            }
            if (change == 0)
            {
                var committed = pending.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                That(!bypassed && committed.Progress.Count == 2, "促销唯一写事务须等待共享writer闸门，释放后只提交一次");
            }
            else
            {
                Reject<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(),
                    "writer排队后重新检查已关闭目录或等号到期的奖励");
                That(!bypassed && f.Sql.Snapshot() == before, "writer排队后的失效拒绝保留全部资产累计订单和邮件");
            }
        }
    }

    // 需要真实商城目录，随资源Session回归运行；不再假设连接上下文拥有已移除的提交标记。
    public static async Task CommittedBusyBoundary(TownCatalog towns, ItemResources resources)
    {
        foreach (int code in new[] { 5, 6 })
        {
            using var f = new Fixture(new PromotionConfiguration(CashShopPromotionSessionTests.Rules, true));
            var session = new GameSession(f.Store, towns, f.Owner, timeProvider: new FixedClock(f.Now), promotionStore: f.Store);
            session.Select(0); session.MarkReady();
            var cash = new InventoryCashShopService(resources.Items, CashShopPromotionSessionTests.Shop,
                resources.GoldLimits, resources.EpicPieces, resources.Cargo);
            var promotions = new CashShopPromotionService(() => f.Catalog, resources.Items.Get);
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            await using var sender = new GameConnectionSender(f.Owner, stream, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), f.Owner, session, sender);
            using var log = new ServerLog(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Sql.Path)!, "publication"));
            var commands = new GameCommandRegistry();
            new ShopHandler(null!, cash, new InventoryProjection(null!, null!, resources.EpicPieces), log,
                promotions: promotions).Register(commands);
            commands.Seal();
            byte[] body = new byte[34];
            body[2] = 2;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), 700101);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(22), 700101);
            var request = new Packet(1, (ushort)CmdPacketType.BUY_CERASHOP_ITEM, body);
            int attempts = 0, reports = 0, publications = 0;
            var failure = new SqliteException("真实促销提交后的可控发布故障", code);
            session.MailboxArrived = _ => { publications++; throw failure; };
            bool propagated = false;
            try
            {
                await SqliteBusyRetry.TryExecuteAsync(() =>
                {
                    attempts++;
                    return commands.DispatchAsync(context, request);
                }, CancellationToken.None, (_, _) => reports++);
            }
            catch (IOException error) { propagated = ReferenceEquals(error.InnerException, failure); }
            f.Character = f.Store.LoadCharacter(f.Owner, 0)!;
            long count = f.Store.Read(f.Owner, f.Rules.Scope).Count;
            long wallet = f.Character.Inventory.AccountWallet.Get(CurrencyKind.Cera);
            object? orders = f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_orders");
            object? mails = f.Sql.Query("SELECT count(*) FROM mailbox_messages");
            That(propagated && attempts == 1 && reports == 0 && publications == 1 && count == 2 && wallet == 9800 &&
                f.Character.Inventory.Items.Where(item => item.Core.ItemId == 2058).Sum(item => item.Core.Count) == 2 &&
                orders is 1L && f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_awards") is 1L && mails is 1L,
                $"真实ShopHandler提交后Busy/Locked只完成一次：code={code}, attempts={attempts}, reports={reports}, " +
                $"publications={publications}, propagated={propagated}, count={count}, wallet={wallet}, orders={orders}, mails={mails}");
            session.MailboxArrived = _ => publications++;
            var (completed, next) = await SqliteBusyRetry.TryExecuteAsync(
                () => commands.DispatchAsync(context, request), CancellationToken.None);
            f.Character = f.Store.LoadCharacter(f.Owner, 0)!;
            That(completed && next == CommandResult.Handled && publications == 2 &&
                f.Store.Read(f.Owner, f.Rules.Scope).Count == 4 &&
                f.Character.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 9600 &&
                f.Character.Inventory.Items.Where(item => item.Core.ItemId == 2058).Sum(item => item.Core.Count) == 4 &&
                f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_orders") is 2L &&
                f.Sql.Query("SELECT count(*) FROM mailbox_messages") is 3L &&
                f.Sql.Query("SELECT count(*) FROM cash_shop_promotion_awards") is 3L,
                "下一真实请求按新订单合法复购，未以包体去重或永久阻塞账号");
            f.Sql.Integrity();
        }
    }

    static void ClockBoundaries()
    {
        using var f = new Fixture();
        var plan = f.Plan(2);
        var observed = f.Now.AddMinutes(-2);
        var beforeExpiry = plan with
        {
            Order = plan.Order with { PlannedAt = observed },
            Deliveries = [plan.Deliveries[0] with
            {
                Created = observed.ToUnixTimeSeconds(),
                Items = [plan.Deliveries[0].Items[0] with
                    { Core = plan.Deliveries[0].Items[0].Core with { ExpireTime = checked((int)f.Now.ToUnixTimeSeconds()) + 1 } }]
            }]
        };
        var accepted = f.Store.Commit(f.Owner, beforeExpiry, observed);
        That(accepted.Progress.Count == 2 && f.Sql.Query("SELECT committed_at FROM cash_shop_promotion_orders") is long committed && committed == f.Now.ToUnixTimeSeconds(),
            "固定注入时钟在到期前一秒允许提交并记录writer取得后的权威时刻");
    }

    static void PurchaseExpirationBoundaries()
    {
        var failures = new List<string>();
        foreach (string kind in new[] { "主货", "固定bonus", "溢出附件", "主货合并", "bonus合并" })
        foreach (bool atExpiry in new[] { false, true })
        {
            using var f = new Fixture();
            int expires = checked((int)f.Now.ToUnixTimeSeconds()) + 1;
            int itemId = kind.StartsWith("主货", StringComparison.Ordinal) ? 51000 : 51003;
            bool merge = kind.EndsWith("合并", StringComparison.Ordinal);
            if (merge)
            {
                var stack = new InventoryItem(0, new(InventoryListType.Main, 65), new(itemId, ItemKind.Consumable, 3) { ExpireTime = expires });
                f.Character = f.Store.CommitInventory(f.Owner, f.Character, new(f.Character.Inventory,
                    f.Character.Inventory with { Items = f.Character.Inventory.Items.Add(stack) }, "seed_timed_stack"));
            }
            var plan = f.Plan(2);
            var added = plan.Inventory.After.Items.Single(item => item.Uid == 0 && item.Core.ItemId == itemId);
            var timed = added with { Core = added.Core with { ExpireTime = expires } };
            var after = plan.Inventory.After with { Items = plan.Inventory.After.Items.Replace(added, timed) };
            ImmutableArray<InventoryItem> overflow = [];
            if (merge)
            {
                var stack = plan.Inventory.Before.Items.Single(item => item.Core.ItemId == itemId);
                after = after with { Items = after.Items.Remove(timed).Replace(stack,
                    stack with { Core = stack.Core with { Value = stack.Core.Count + timed.Core.Count } }) };
            }
            if (kind == "溢出附件")
            {
                after = after with { Items = after.Items.Remove(timed) };
                overflow = [timed];
            }
            plan = plan with { Inventory = new(plan.Inventory.Before, after, plan.Inventory.Reason, plan.Inventory.CharacterPrecondition, overflow) };
            var clock = atExpiry ? f.Now.AddSeconds(1) : f.Now;
            var store = new CharacterRepository(f.Sql.Path, promotions: () => f.Catalog, promotionClock: new FixedClock(clock));
            string before = f.Sql.Snapshot();
            if (atExpiry)
            {
                bool rejected = false;
                try { store.Commit(f.Owner, plan, f.Now); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) failures.Add(kind + "到期仍提交");
                if (f.Sql.Snapshot() != before) failures.Add(kind + "到期改变原数据");
            }
            else
            {
                var committed = store.Commit(f.Owner, plan, f.Now);
                var received = kind == "溢出附件"
                    ? store.LoadMailbox(f.Owner, committed.Character.Id, f.Now.ToUnixTimeSeconds()).Entries.SelectMany(mail => mail.Attachments).Select(a => a.Item)
                    : committed.Character.Inventory.Items;
                That(committed.Progress.Count == 2 && committed.Character.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 9800 &&
                    received.Any(item => item.Core.ItemId == itemId && item.Uid > 0 && item.ExpireTime == expires && item.Core.Count == (merge ? 5 : 2)),
                    kind + "在T有效时正常提交且保留真实实例期限与数量");
            }
        }
        That(failures.Count == 0, "T规划、T+1权威提交拒绝完整购买结果且全表不变：" + string.Join("；", failures));

        using var history = new Fixture();
        var old = new InventoryItem(0, new(InventoryListType.Main, 65), new(51999, ItemKind.Consumable, 3)
            { ExpireTime = checked((int)history.Now.ToUnixTimeSeconds()) - 1 });
        history.Character = history.Store.CommitInventory(history.Owner, history.Character, new(history.Character.Inventory,
            history.Character.Inventory with { Items = history.Character.Inventory.Items.Add(old) }, "seed_expired_history"));
        var historical = history.Character.Inventory.Items.Single();
        var result = history.Store.Commit(history.Owner, history.Plan(2), history.Now);
        That(result.Progress.Count == 2 && result.Character.Inventory.Items.Contains(historical),
            "原样保留的历史到期实例不阻止无关合法购买");
        history.Character = result.Character;
        var moved = history.Plan(2);
        var destination = historical with { Position = new(InventoryListType.Main, 80) };
        moved = moved with { Inventory = new(moved.Inventory.Before,
            moved.Inventory.After with { Items = moved.Inventory.After.Items.Replace(historical, destination) },
            moved.Inventory.Reason, moved.Inventory.CharacterPrecondition) };
        var movedResult = history.Store.Commit(history.Owner, moved, history.Now);
        That(movedResult.Progress.Count == 4 && movedResult.Character.Inventory.Items.Contains(destination),
            "仅移动槽位的历史到期实例仍不阻止无关合法购买");
    }

    static void RenewalExpirationBoundary()
    {
        foreach (bool atExpiry in new[] { false, true })
        {
            using var f = new Fixture();
            var old = new InventoryItem(0, new(InventoryListType.Equipment, 30), new(51998, ItemKind.Equipment, 0)
                { ExpireTime = checked((int)f.Now.ToUnixTimeSeconds()) - 1 });
            f.Character = f.Store.CommitInventory(f.Owner, f.Character, new(f.Character.Inventory,
                f.Character.Inventory with { Items = f.Character.Inventory.Items.Add(old) }, "seed_old_name_tag"));
            old = f.Character.Inventory.Find(old.Position)!;
            // 对齐正式Buy的visual续期结果：同UID/数量/身份，只将旧期限从now续一天。
            // 这是已证Buy结果的存储合同fixture；不冒充调用真实Buy或读取PVF。
            var renewed = old with { Core = old.Core with { ExpireTime = checked((int)f.Now.AddDays(1).ToUnixTimeSeconds()) } };
            var plan = f.Plan(2);
            var after = plan.Inventory.After with
            {
                Items = plan.Inventory.After.Items.Replace(old, renewed),
                AccountWallet = plan.Inventory.After.AccountWallet.Set(CurrencyKind.Cera, 9500)
            };
            plan = plan with
            {
                Inventory = new(plan.Inventory.Before, after, plan.Inventory.Reason, plan.Inventory.CharacterPrecondition),
                Purchase = new(plan.Purchase.Products.Append(new CashShopProduct(1001, 51998, 1, 300, "visual", 1)).ToArray(), plan.Purchase.Bonuses)
            };
            var clock = atExpiry ? DateTimeOffset.FromUnixTimeSeconds(renewed.ExpireTime) : f.Now;
            var store = new CharacterRepository(f.Sql.Path, promotions: () => f.Catalog, promotionClock: new FixedClock(clock));
            if (atExpiry)
            {
                string before = f.Sql.Snapshot();
                Reject<InvalidOperationException>(() => store.Commit(f.Owner, plan, f.Now),
                    "同UID仅期限变化的付费visual续期结果在After等号到期时拒绝");
                That(f.Sql.Snapshot() == before, "visual续期到期混车拒绝后资产累计订单邮件全表不变");
            }
            else
            {
                var result = store.Commit(f.Owner, plan, f.Now);
                That(result.Progress.Count == 2 && result.Character.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 9500 &&
                    result.Character.Inventory.Find(old.Position) == renewed, "visual续期混车在T有效时扣费提交且保留原UID数量身份");
            }
        }
    }

    static void CatalogBoundaries()
    {
        foreach (bool enabled in new[] { false, true })
        {
            using var f = new Fixture();
            var plan = f.Plan(2);
            var latest = f.MakeCatalog(enabled);
            int reads = 0;
            var store = new CharacterRepository(f.Sql.Path,
                promotions: () => ++reads == 1 ? f.Catalog : latest, promotionClock: new FixedClock(f.Now));
            string before = f.Sql.Snapshot();
            Reject<InvalidOperationException>(() => store.Commit(f.Owner, plan, f.Now),
                "writer取得后必须再次拒绝已关闭或重新启用的新目录修订");
            That(reads == 2 && f.Sql.Snapshot() == before, "目录在writer边界变化时复验且原数据全不变");
        }
    }

    sealed class Fixture : IDisposable
    {
        public MailboxPersistenceTests.Fixture Sql { get; } = new();
        public DfoServer.Game.Accounts.AccountOwners.Lease Owner => Sql.ReceiverOwner;
        public CharacterRepository Store { get; }
        public Character Character { get; set; }
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public PromotionRules Rules { get; set; } = new(new("test", "permanent", "v1", "realm", "package"), 2, 1, 3, 51002, 1, 5000,
            [new(1000, 51000, 1)], [new(2, 51001, 1)]);
        public CashShopPromotionCatalog Catalog { get; set; }
        public PromotionConfiguration? Configuration { get; set; }
        public Fixture(PromotionConfiguration? configuration = null)
        {
            Configuration = configuration;
            Rules = configuration?.Rules ?? Rules;
            Catalog = MakeCatalog(true);
            Store = new(Sql.Path, promotions: () => Catalog, promotionClock: new FixedClock(Now));
            var c = Sql.Receiver;
            Character = Store.CommitInventory(Owner, c, new(c.Inventory,
                c.Inventory with { AccountWallet = c.Inventory.AccountWallet.Set(CurrencyKind.Cera, 10000) }, "fund_fixture"));
        }
        public CashShopPromotionCatalog MakeCatalog(bool enabled) => CashShopPromotionCatalog.Parse(
            JsonSerializer.Serialize(new[] { (Configuration ?? new PromotionConfiguration(Rules)) with { Rules = Rules, Enabled = enabled } }),
            CashShopCatalog.Parse("[item]\n" + string.Concat(Rules.Products.Select(p => $"{p.CommodityId} {p.ItemId} 1 0 0 100 0 0 0\n"))),
            id => new(id, ItemKind.Consumable, 0, 1, 0, 1, 0, 0, 0, "[free]", ushort.MaxValue, 0, "合成外盒"), _ => null);
        public PromotionPurchasePlan Plan(int units, bool special = false)
        {
            var entry = Catalog.Activities[0];
            var progress = Store.Read(Owner, entry);
            var order = new PromotionOrder(Guid.NewGuid(), Owner.Account, Character.Id, Rules.Scope, progress.Count, progress.Revision, units, Now);
            var awards = CashShopPromotionRules.Settle(Rules, progress.Count, units, entry.Policy!);
            var recipient = Store.FindMailRecipient(Character.Name)!;
            var deliveries = ImmutableArray.CreateBuilder<MailboxDelivery>();
            for (int index = 0; index < awards.Length; index++)
            {
                var award = awards[index];
                ImmutableArray<InventoryItem> items = special
                    ? award.ItemId == 51001
                        ? [new(0, new(InventoryListType.Avatar, 0), new(51001, ItemKind.Avatar, 0)) { Avatar = new() { Color1 = 7 } }]
                        : [new(0, new(InventoryListType.Pet, 0), new(51002, ItemKind.Creature, 0)) { Creature = new() { Name = "赠礼宠物" } }]
                    : [new(0, new(InventoryListType.Main, 0), new(award.ItemId, ItemKind.Consumable, award.Count))];
                deliveries.Add(new(PromotionMailKey.Create(order, award.PurchaseNumber, index), recipient, "累计赠礼", "累计奖励", 0, items, Now.ToUnixTimeSeconds()));
            }
            var after = Character.Inventory with { AccountWallet = Character.Inventory.AccountWallet.Set(CurrencyKind.Cera,
                Character.Inventory.AccountWallet.Get(CurrencyKind.Cera) - units * 100) };
            foreach (int id in new[] { Rules.Products[0].ItemId, 51003 })
            {
                short slot = 65;
                while (after.Find(new(InventoryListType.Main, slot)) is not null) slot++;
                after = after with { Items = after.Items.Add(new(0, new(InventoryListType.Main, slot), new(id, ItemKind.Consumable, units))) };
            }
            return new(order, Character, new(Character.Inventory, after, "cash_shop_buy", new(Character)),
                new(Enumerable.Repeat(new CashShopProduct(Rules.Products[0].CommodityId, Rules.Products[0].ItemId, 1, 100, "item"), units).ToArray(),
                    new Dictionary<int, IReadOnlyList<CashShopBonus>> { [Rules.Products[0].CommodityId] = new[] { new CashShopBonus(51003, 1) } }), awards, deliveries.ToImmutable())
            { Rules = Rules, Policy = entry.Policy, CatalogRevision = Catalog.Revision };
        }
        public void Dispose() => Sql.Dispose();
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
