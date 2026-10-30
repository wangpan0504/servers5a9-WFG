using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.CashShopPromotions;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    const string PromotionScopeFilter = "campaign=$campaign AND period=$period AND realm=$realm AND promotion_group=$group";

    public PromotionProgress Read(AccountOwners.Lease owner, PromotionScope scope) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        return ReadPromotionProgress(db, tx, owner.Account, scope);
    });

    public PromotionProgress Read(AccountOwners.Lease owner, PromotionCatalogEntry entry) => owner.Execute(() =>
    {
        var policy = entry.Policy ?? throw new InvalidOperationException("促销策略无效");
        policy.Validate(entry.Configuration.Rules);
        using var db = database.Open();
        using var tx = db.BeginRead();
        ValidatePromotionSource(db, tx, policy);
        ValidateExistingPromotionRules(db, tx, entry.Configuration.Rules,
            CashShopPromotionCatalog.RuleSnapshot(entry.Configuration.Rules, policy), policy);
        return ReadPromotionProgress(db, tx, owner.Account, policy.CounterScope,
            policy.IsPermanent ? entry.Configuration.Rules.Scope : null);
    });

    public PromotionPurchaseCommit? RestoreCommitted(AccountOwners.Lease owner, Guid orderId) => owner.Execute(() =>
    {
        if (orderId == Guid.Empty) return null;
        using var db = database.Open();
        using var tx = db.BeginRead();
        return ReadPromotionOrder(db, tx, owner.Account, orderId);
    });

    static PromotionProgress ReadPromotionProgress(SqliteConnection db, SqliteTransaction tx, string account, PromotionScope scope,
        PromotionScope? successor = null)
    {
        RejectPromotionSuccessor(db, tx, scope, successor);
        using var command = PromotionCommand(db, tx, scope, account);
        command.CommandText = $"SELECT rules_version FROM cash_shop_promotion_rules WHERE {PromotionScopeFilter}";
        if (command.ExecuteScalar() is string version && version != scope.RulesVersion)
            throw new InvalidOperationException("同一促销规则期不能更换规则版本");
        command.CommandText = $"SELECT count,revision FROM cash_shop_promotion_progress WHERE account=$account AND {PromotionScopeFilter}";
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(scope, reader.GetInt64(0), reader.GetInt64(1)) : new(scope, 0, 0);
    }

    public PromotionPurchaseCommit Commit(AccountOwners.Lease owner, PromotionPurchasePlan plan, DateTimeOffset now) => owner.Execute(() =>
    {
        var order = plan.Order;
        if (owner.Account != order.Account || order.Id == Guid.Empty || plan.Expected.Id != order.RecipientId || order.Scope != plan.Rules.Scope ||
            plan.Inventory.CharacterPrecondition?.MatchesIdentity(plan.Expected) is false)
            throw new InvalidOperationException("促销订单身份与当前账号角色不一致");
        using var db = database.Open();
        // 恢复只读原事实；不重新结算、分配UID，运行开关和进程修订不改变已提交事实。
        using (var read = db.BeginRead())
        {
            var replay = ReadPromotionOrder(db, read, owner.Account, order.Id,
                () => PromotionBusinessSnapshot(plan, CashShopPromotionCatalog.RuleSnapshot(plan.Rules, plan.EffectivePolicy)));
            if (replay is not null) return replay;
        }
        var policy = plan.EffectivePolicy;
        string rules = CashShopPromotionCatalog.RuleSnapshot(plan.Rules, policy);
        string business = PromotionBusinessSnapshot(plan, rules);
        ValidatePromotionCatalog(plan, rules);
        ValidatePromotionPurchase(plan, now);
        // 唯一外层写事务；库存与每封邮件只参与同一连接，不调用独立提交入口。
        using var write = db.BeginWrite("CharacterRepository.CashShopPromotions.Commit");
        using var tx = write.Transaction;
        var raced = ReadPromotionOrder(db, tx, owner.Account, order.Id, () => business);
        if (raced is not null) return raced;
        // 等待writer期间也可能重新装载目录；提交前必须重新捕获当前修订。
        ValidatePromotionCatalog(plan, rules);
        long committedAt = ValidatePromotionTime(plan, now);
        BindPromotionRules(db, tx, plan.Rules, rules, policy);
        var progress = AdvancePromotionProgress(db, tx, order, policy.CounterScope);
        var character = CommitInventoryInTransaction(db, tx, owner, plan.Expected, plan.Inventory);
        using var command = PromotionCommand(db, tx, order.Scope, owner.Account);
        command.CommandText = """
            INSERT INTO cash_shop_promotion_orders(order_id,account,recipient_id,campaign,period,realm,promotion_group,rules_version,
                business_snapshot,count,revision,mail_ids,committed_at)
            VALUES($order,$account,$recipient,$campaign,$period,$realm,$group,$version,$business,$count,$revision,'[]',$now)
            """;
        command.Parameters.AddWithValue("$order", order.Id.ToString("N"));
        command.Parameters.AddWithValue("$recipient", order.RecipientId);
        command.Parameters.AddWithValue("$version", order.Scope.RulesVersion);
        command.Parameters.AddWithValue("$business", business);
        command.Parameters.AddWithValue("$count", progress.Count);
        command.Parameters.AddWithValue("$revision", progress.Revision);
        command.Parameters.AddWithValue("$now", committedAt);
        command.ExecuteNonQuery();
        var mailIds = ImmutableArray.CreateBuilder<uint>(plan.Deliveries.Length);
        for (int part = 0; part < plan.Deliveries.Length; part++)
        {
            var delivery = plan.Deliveries[part];
            var (error, mailId) = DeliverMailInTransaction(db, tx, delivery);
            if (error != MailboxError.None)
                throw new InvalidOperationException($"累计赠礼邮件投递失败：{error}");
            mailIds.Add(mailId);
            var actual = ReadMail(db, tx, order.RecipientId, mailId)
                ?? throw new InvalidOperationException("累计赠礼已提交邮件缺失");
            for (int index = 0; index < delivery.Items.Length; index++)
            {
                var award = plan.Awards[part * 10 + index];
                using var receipt = PromotionCommand(db, tx, order.Scope, owner.Account);
                receipt.CommandText = """
                    INSERT INTO cash_shop_promotion_awards(account,campaign,period,realm,promotion_group,rules_version,
                        global_purchase,order_id,reward_snapshot,mail_id)
                    VALUES($account,$campaign,$period,$realm,$group,$version,$purchase,$order,$reward,$mail)
                    """;
                receipt.Parameters.AddWithValue("$version", order.Scope.RulesVersion);
                receipt.Parameters.AddWithValue("$purchase", award.PurchaseNumber);
                receipt.Parameters.AddWithValue("$order", order.Id.ToString("N"));
                receipt.Parameters.AddWithValue("$reward", JsonSerializer.Serialize(new { Award = award, Item = actual.Attachments[index].Item }));
                receipt.Parameters.AddWithValue("$mail", mailId);
                receipt.ExecuteNonQuery();
            }
        }
        command.CommandText = "UPDATE cash_shop_promotion_orders SET mail_ids=$mails WHERE order_id=$order";
        command.Parameters.AddWithValue("$mails", JsonSerializer.Serialize(mailIds.ToImmutable()));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("累计赠礼订单回执丢失");
        tx.Commit();
        return new(character, order.Id, progress, mailIds.ToImmutable());
    });

    void ValidatePromotionCatalog(PromotionPurchasePlan plan, string rules)
    {
        var catalog = promotions?.Invoke();
        if (catalog is null || !catalog.IsCurrent(plan.CatalogRevision, plan.Order.Scope) ||
            !catalog.Activities.Any(entry => entry.Availability == PromotionAvailability.Active &&
                entry.Configuration.Rules.Scope == plan.Order.Scope && entry.Policy is { } policy && policy.IsPermanent == plan.EffectivePolicy.IsPermanent &&
                policy.CounterScope == plan.EffectivePolicy.CounterScope &&
                CashShopPromotionCatalog.RuleSnapshot(entry.Configuration.Rules, policy) == rules))
            throw new InvalidOperationException("促销目录、开关或规则已变化");
    }

    static void ValidatePromotionPurchase(PromotionPurchasePlan plan, DateTimeOffset now)
    {
        var order = plan.Order;
        if (order.ExpectedRevision < 0 || order.ExpectedCount < 0 || order.PlannedAt > now ||
            plan.Inventory.Reason != "cash_shop_buy" || plan.Inventory.CharacterPrecondition is null ||
            plan.Inventory.CharacterPrecondition.IsExpired(now.ToUnixTimeSeconds()) ||
            plan.Awards.IsDefault || plan.Deliveries.IsDefault || plan.Awards.Length > 64 ||
            plan.Deliveries.Length != (plan.Awards.Length + 9) / 10 || plan.Deliveries.Length > 7 ||
            plan.Purchase.Products.Count is < 1 or > 64)
            throw new InvalidOperationException("累计赠礼购买规划无效或已到期");
        var awards = CashShopPromotionRules.Settle(plan.Rules, order.ExpectedCount, order.Units, plan.EffectivePolicy);
        if (!awards.SequenceEqual(plan.Awards)) throw new InvalidOperationException("累计赠礼资格必须覆盖完整购买区间");
        int units = 0;
        foreach (var product in plan.Purchase.Products)
        {
            var mapping = plan.Rules.Products.SingleOrDefault(row => row.CommodityId == product.CommodityId);
            if (mapping is null) continue;
            if (product.ItemId != mapping.ItemId || product.Count != 1) throw new InvalidOperationException("累计商品映射不一致");
            units = checked(units + mapping.Units);
        }
        if (units != order.Units) throw new InvalidOperationException("累计必须来自本次实际购买商品行");
        for (int part = 0; part < plan.Deliveries.Length; part++)
        {
            var mail = plan.Deliveries[part];
            int offset = part * 10;
            if (mail.Key != PromotionMailKey.Create(order, awards[offset].PurchaseNumber, part) ||
                mail.Recipient.Id != order.RecipientId || mail.Recipient.Account != order.Account || mail.Gold != 0 ||
                mail.Expires is not null || mail.Created != order.PlannedAt.ToUnixTimeSeconds() ||
                mail.Items.IsDefault || mail.Items.Length != Math.Min(10, awards.Length - offset))
                throw new InvalidOperationException("累计赠礼邮件分封、身份或内容不完整");
            for (int index = 0; index < mail.Items.Length; index++)
            {
                var item = mail.Items[index];
                var award = awards[offset + index];
                if (item.Uid != 0 || item.Core.ItemId != award.ItemId || item.Core.Count != award.Count || item.IsExpired(now.ToUnixTimeSeconds()))
                    throw new InvalidOperationException("累计赠礼必须投递全部原外盒新实例");
            }
        }
    }

    long ValidatePromotionTime(PromotionPurchasePlan plan, DateTimeOffset now)
    {
        // writer等待之后再读真实时钟；调用方捕获的旧时刻不能延长实例或资格期限。
        long current = Math.Max(now.ToUnixTimeSeconds(), (promotionClock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds());
        foreach (var item in plan.Inventory.After.Items)
        {
            if (!item.IsExpired(current)) continue;
            var before = plan.Inventory.BeforeIndex.FindUid(item.Uid);
            // 新发、续期和合并后增加的数量都须有效；仅移动或原样保留的历史到期项不影响无关购买。
            if (before is null || before.Core.ItemId != item.Core.ItemId || before.Core.Kind != item.Core.Kind ||
                item.Core.Count > before.Core.Count || item.ExpireTime != before.ExpireTime)
                throw new InvalidOperationException("商城主货或固定附赠实例已到期");
        }
        if (plan.Inventory.CharacterPrecondition?.IsExpired(current) == true ||
            plan.Inventory.OverflowItems.Any(item => item.IsExpired(current)) ||
            plan.Deliveries.Any(mail => mail.Items.Any(item => item.IsExpired(current))))
            throw new InvalidOperationException("累计赠礼购买资格或外盒实例已到期");
        return current;
    }

    PromotionPurchaseCommit? ReadPromotionOrder(SqliteConnection db, SqliteTransaction tx, string account,
        Guid orderId, Func<string>? expectedBusiness = null)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT account,recipient_id,campaign,period,rules_version,realm,promotion_group,business_snapshot,count,revision,mail_ids FROM cash_shop_promotion_orders WHERE order_id=$id";
        command.Parameters.AddWithValue("$id", orderId.ToString("N"));
        long count, revision; ushort recipientId; PromotionScope scope;
        ImmutableArray<uint> mailIds;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            if (reader.GetString(0) != account) throw new InvalidOperationException("累计订单不属于当前账号");
            recipientId = checked((ushort)reader.GetInt64(1));
            scope = new(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6));
            if (expectedBusiness is not null && reader.GetString(7) != expectedBusiness())
                throw new InvalidOperationException("相同累计订单ID的业务内容不同");
            count = reader.GetInt64(8); revision = reader.GetInt64(9);
            mailIds = JsonSerializer.Deserialize<ImmutableArray<uint>>(reader.GetString(10));
        }
        var recipient = FindMailRecipientById(db, tx, recipientId);
        if (recipient is null || recipient.Account != account) throw new InvalidOperationException("累计订单原收件角色已不存在");
        var character = Require(db, tx, account, recipient.Name);
        if (character.Id != recipientId) throw new InvalidOperationException("累计订单角色身份已变化");
        using var binding = PromotionCommand(db, tx, scope, account);
        binding.CommandText = $"SELECT rule_kind,carry_campaign,carry_period,carry_rules_version,carry_realm,carry_group FROM cash_shop_promotion_rules WHERE {PromotionScopeFilter}";
        using (var reader = binding.ExecuteReader())
            if (reader.Read() && reader.GetInt64(0) == 1)
                scope = new(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
        return new(character, orderId, new(scope, count, revision), mailIds);
    }

    static void RejectPromotionSuccessor(SqliteConnection db, SqliteTransaction tx, PromotionScope source, PromotionScope? allowed)
    {
        using var command = PromotionCommand(db, tx, source, "");
        command.CommandText = "SELECT campaign,period,rules_version,realm,promotion_group FROM cash_shop_promotion_rules " +
            "WHERE rule_kind=1 AND carry_campaign=$campaign AND carry_period=$period AND carry_realm=$realm AND carry_group=$group AND carry_rules_version=$version";
        command.Parameters.AddWithValue("$version", source.RulesVersion);
        using var reader = command.ExecuteReader();
        if (reader.Read() && (allowed is null || allowed.Value != new PromotionScope(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4))))
            throw new InvalidOperationException("原促销规则期已永久衔接后继，不能继续新购或再次分叉");
    }

    static void ValidatePromotionSource(SqliteConnection db, SqliteTransaction tx, PromotionPolicy policy)
    {
        if (policy.SourceRules is not { } source) return;
        ValidateExistingPromotionRules(db, tx, source, policy.SourceSnapshot!, PromotionPolicy.Pilot(source));
        RejectPromotionSuccessor(db, tx, source.Scope, policy.Scope);
    }

    static bool ValidateExistingPromotionRules(SqliteConnection db, SqliteTransaction tx, PromotionRules rules,
        string snapshot, PromotionPolicy policy)
    {
        using var command = PromotionCommand(db, tx, rules.Scope, "");
        command.CommandText = $"SELECT rules_version,rules_snapshot,rule_kind,carry_campaign,carry_period,carry_rules_version,carry_realm,carry_group,carry_rules_snapshot FROM cash_shop_promotion_rules WHERE {PromotionScopeFilter}";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return false;
        if (reader.GetString(0) != rules.Scope.RulesVersion || reader.GetString(1) != snapshot ||
            reader.GetInt64(2) != (policy.IsPermanent ? 1 : 0) ||
            (policy.IsPermanent && (new PromotionScope(reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7)) != policy.CounterScope || reader.GetString(8) != policy.SourceSnapshot)))
            throw new InvalidOperationException("同一促销规则期的奖励、版本与衔接策略已冻结");
        return true;
    }

    static void BindPromotionRules(SqliteConnection db, SqliteTransaction tx, PromotionRules rules, string snapshot, PromotionPolicy policy)
    {
        ValidatePromotionSource(db, tx, policy);
        RejectPromotionSuccessor(db, tx, policy.CounterScope, policy.IsPermanent ? rules.Scope : null);
        if (policy.SourceRules is { } source)
            InsertPromotionRulesIfMissing(db, tx, source, policy.SourceSnapshot!, PromotionPolicy.Pilot(source));
        InsertPromotionRulesIfMissing(db, tx, rules, snapshot, policy);
    }

    static void InsertPromotionRulesIfMissing(SqliteConnection db, SqliteTransaction tx, PromotionRules rules, string snapshot,
        PromotionPolicy policy)
    {
        if (ValidateExistingPromotionRules(db, tx, rules, snapshot, policy)) return;
        using var command = PromotionCommand(db, tx, rules.Scope, "");
        command.CommandText = "INSERT INTO cash_shop_promotion_rules(campaign,period,realm,promotion_group,rules_version,rules_snapshot,rule_kind,carry_campaign,carry_period,carry_rules_version,carry_realm,carry_group,carry_rules_snapshot) " +
            "VALUES($campaign,$period,$realm,$group,$version,$snapshot,$kind,$fromCampaign,$fromPeriod,$fromVersion,$fromRealm,$fromGroup,$fromSnapshot)";
        command.Parameters.AddWithValue("$version", rules.Scope.RulesVersion);
        command.Parameters.AddWithValue("$snapshot", snapshot);
        command.Parameters.AddWithValue("$kind", policy.IsPermanent ? 1 : 0);
        command.Parameters.AddWithValue("$fromCampaign", (object?)policy.SourceRules?.Scope.Campaign ?? DBNull.Value);
        command.Parameters.AddWithValue("$fromPeriod", (object?)policy.SourceRules?.Scope.Period ?? DBNull.Value);
        command.Parameters.AddWithValue("$fromVersion", (object?)policy.SourceRules?.Scope.RulesVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$fromRealm", (object?)policy.SourceRules?.Scope.Realm ?? DBNull.Value);
        command.Parameters.AddWithValue("$fromGroup", (object?)policy.SourceRules?.Scope.Group ?? DBNull.Value);
        command.Parameters.AddWithValue("$fromSnapshot", (object?)policy.SourceSnapshot ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    static PromotionProgress AdvancePromotionProgress(SqliteConnection db, SqliteTransaction tx, PromotionOrder order, PromotionScope counterScope)
    {
        // 在SQL之前进行整数边界检查，SQLite的自动数值提升不能参与累计或revision。
        long after = checked(order.ExpectedCount + order.Units);
        long nextRevision = checked(order.ExpectedRevision + 1);
        using var command = PromotionCommand(db, tx, counterScope, order.Account);
        command.CommandText = """
            INSERT INTO cash_shop_promotion_progress(account,campaign,period,realm,promotion_group,count,revision)
            SELECT $account,$campaign,$period,$realm,$group,0,0 WHERE $before=0 AND $revision=0
            ON CONFLICT(account,campaign,period,realm,promotion_group) DO NOTHING
            """;
        command.Parameters.AddWithValue("$before", order.ExpectedCount);
        command.Parameters.AddWithValue("$revision", order.ExpectedRevision);
        command.ExecuteNonQuery();
        command.CommandText = $"UPDATE cash_shop_promotion_progress SET count=$after,revision=$nextRevision WHERE account=$account AND {PromotionScopeFilter} AND count=$before AND revision=$revision";
        command.Parameters.AddWithValue("$after", after); command.Parameters.AddWithValue("$nextRevision", nextRevision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("累计赠礼进度CAS冲突");
        return new(counterScope, after, nextRevision);
    }

    static SqliteCommand PromotionCommand(SqliteConnection db, SqliteTransaction tx, PromotionScope scope, string account)
    {
        var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$campaign", scope.Campaign); command.Parameters.AddWithValue("$period", scope.Period);
        command.Parameters.AddWithValue("$realm", scope.Realm); command.Parameters.AddWithValue("$group", scope.Group);
        return command;
    }

    static string PromotionBusinessSnapshot(PromotionPurchasePlan plan, string rules) => plan.EffectivePolicy.IsPermanent
        ? JsonSerializer.Serialize(new { Business = PromotionLegacyBusinessSnapshot(plan, rules),
            Policy = new { RuleKind = "approved-permanent", plan.EffectivePolicy.CounterScope, plan.EffectivePolicy.SourceSnapshot } })
        : PromotionLegacyBusinessSnapshot(plan, rules);

    // v1无限制资格的序列化形状保持；额外地点与期限必须进入订单身份，不能序列化成空对象。
    static string PromotionLegacyBusinessSnapshot(PromotionPurchasePlan plan, string rules) => JsonSerializer.Serialize(new
    {
        plan.Order, plan.Expected, Rules = rules,
        Inventory = new { plan.Inventory.Before, plan.Inventory.After, plan.Inventory.Reason,
            CharacterPrecondition = PromotionCharacterPrecondition(plan.Inventory.CharacterPrecondition),
            plan.Inventory.OverflowItems, plan.Inventory.PreserveMaterials,
            ExpBonus = plan.Inventory.ExpBonus is { } bonus ? new { bonus.Until, bonus.Permille } : null,
            plan.Inventory.EquipmentSlotFlagsAfter },
        Products = plan.Purchase.Products, Bonuses = plan.Purchase.Bonuses.OrderBy(pair => pair.Key).ToArray(),
        plan.Awards, plan.Deliveries
    });

    static object? PromotionCharacterPrecondition(InventoryCharacterPrecondition? value)
    {
        if (value is null || value.QualificationExpiresAt is null && value.RequiredPosition is null)
            return value;
        return new { value.QualificationExpiresAt, value.RequiredPosition };
    }
}
