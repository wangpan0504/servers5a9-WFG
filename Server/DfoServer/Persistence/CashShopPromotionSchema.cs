using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class CashShopPromotionSchema
{
    internal const string Rules = """
        CREATE TABLE IF NOT EXISTS cash_shop_promotion_rules(
            campaign TEXT NOT NULL,
            period TEXT NOT NULL,
            realm TEXT NOT NULL,
            promotion_group TEXT NOT NULL,
            rules_version TEXT NOT NULL,
            rules_snapshot TEXT NOT NULL CHECK(json_valid(rules_snapshot)),
            PRIMARY KEY(campaign,period,realm,promotion_group)
        ) STRICT;
        """;

    internal const string Orders = """
        CREATE TABLE IF NOT EXISTS cash_shop_promotion_orders(
            order_id TEXT NOT NULL PRIMARY KEY CHECK(length(order_id)=32),
            account TEXT NOT NULL,
            recipient_id INTEGER NOT NULL CHECK(recipient_id BETWEEN 1 AND 65535),
            campaign TEXT NOT NULL,
            period TEXT NOT NULL,
            realm TEXT NOT NULL,
            promotion_group TEXT NOT NULL,
            rules_version TEXT NOT NULL,
            business_snapshot TEXT NOT NULL CHECK(json_valid(business_snapshot)),
            count INTEGER NOT NULL CHECK(count>=0),
            revision INTEGER NOT NULL CHECK(revision>0),
            mail_ids TEXT NOT NULL CHECK(json_valid(mail_ids)),
            committed_at INTEGER NOT NULL CHECK(committed_at>=0),
            FOREIGN KEY(campaign,period,realm,promotion_group) REFERENCES cash_shop_promotion_rules(campaign,period,realm,promotion_group)
        ) STRICT;
        """;

    internal const string Progress = """
        CREATE TABLE IF NOT EXISTS cash_shop_promotion_progress(
            account TEXT NOT NULL,
            campaign TEXT NOT NULL,
            period TEXT NOT NULL,
            realm TEXT NOT NULL,
            promotion_group TEXT NOT NULL,
            count INTEGER NOT NULL CHECK(count>=0),
            revision INTEGER NOT NULL CHECK(revision>=0),
            PRIMARY KEY(account,campaign,period,realm,promotion_group),
            FOREIGN KEY(campaign,period,realm,promotion_group) REFERENCES cash_shop_promotion_rules(campaign,period,realm,promotion_group)
        ) STRICT;
        """;

    internal const string Awards = """
        CREATE TABLE IF NOT EXISTS cash_shop_promotion_awards(
            account TEXT NOT NULL,
            campaign TEXT NOT NULL,
            period TEXT NOT NULL,
            realm TEXT NOT NULL,
            promotion_group TEXT NOT NULL,
            rules_version TEXT NOT NULL,
            global_purchase INTEGER NOT NULL CHECK(global_purchase>0),
            order_id TEXT NOT NULL REFERENCES cash_shop_promotion_orders(order_id),
            reward_snapshot TEXT NOT NULL CHECK(json_valid(reward_snapshot)),
            mail_id INTEGER NOT NULL CHECK(mail_id BETWEEN 1 AND 1073741823),
            PRIMARY KEY(account,campaign,period,realm,promotion_group,rules_version,global_purchase)
        ) STRICT;
        """;

    internal const string ImmutableTriggers = """
        CREATE TRIGGER IF NOT EXISTS cash_shop_promotion_rules_immutable_update BEFORE UPDATE ON cash_shop_promotion_rules
        BEGIN SELECT RAISE(ABORT,'promotion rules are immutable'); END;
        CREATE TRIGGER IF NOT EXISTS cash_shop_promotion_rules_immutable_delete BEFORE DELETE ON cash_shop_promotion_rules
        BEGIN SELECT RAISE(ABORT,'promotion rules are immutable'); END;
        """;

    internal const string Sql = Rules + Orders + Progress + Awards + ImmutableTriggers;

    internal const string ContinuationColumns = """
        rule_kind INTEGER NOT NULL DEFAULT 0 CHECK(rule_kind IN(0,1)),
        carry_campaign TEXT,
        carry_period TEXT,
        carry_rules_version TEXT,
        carry_realm TEXT,
        carry_group TEXT,
        carry_rules_snapshot TEXT CHECK((rule_kind=0 AND carry_campaign IS NULL AND carry_period IS NULL AND carry_rules_version IS NULL AND carry_realm IS NULL AND carry_group IS NULL AND carry_rules_snapshot IS NULL) OR (rule_kind=1 AND carry_campaign IS NOT NULL AND carry_period IS NOT NULL AND carry_rules_version IS NOT NULL AND carry_realm IS NOT NULL AND carry_group IS NOT NULL AND carry_rules_snapshot IS NOT NULL AND json_valid(carry_rules_snapshot) AND carry_campaign=campaign AND carry_realm=realm AND carry_group=promotion_group AND carry_period<>period))
        """;

    internal const string SingleSuccessor = """
        CREATE UNIQUE INDEX IF NOT EXISTS cash_shop_promotion_single_successor
            ON cash_shop_promotion_rules(carry_campaign,carry_period,carry_rules_version,carry_realm,carry_group) WHERE rule_kind=1;
        """;

    internal const string ValidPredecessor = """
        CREATE TRIGGER IF NOT EXISTS cash_shop_promotion_valid_predecessor BEFORE INSERT ON cash_shop_promotion_rules
            WHEN NEW.rule_kind=1
            BEGIN
                SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM cash_shop_promotion_rules
                    WHERE campaign=NEW.carry_campaign AND period=NEW.carry_period AND rules_version=NEW.carry_rules_version
                      AND realm=NEW.carry_realm AND promotion_group=NEW.carry_group AND rules_snapshot=NEW.carry_rules_snapshot AND rule_kind=0)
                    THEN RAISE(ABORT,'promotion predecessor must be frozen and exact') END;
            END;
        """;

    internal const string UpgradeColumnsSql = """
        ALTER TABLE cash_shop_promotion_rules ADD COLUMN rule_kind INTEGER NOT NULL DEFAULT 0 CHECK(rule_kind IN(0,1));
        ALTER TABLE cash_shop_promotion_rules ADD COLUMN carry_campaign TEXT;
        ALTER TABLE cash_shop_promotion_rules ADD COLUMN carry_period TEXT;
        ALTER TABLE cash_shop_promotion_rules ADD COLUMN carry_rules_version TEXT;
        ALTER TABLE cash_shop_promotion_rules ADD COLUMN carry_realm TEXT;
        ALTER TABLE cash_shop_promotion_rules ADD COLUMN carry_group TEXT;
        ALTER TABLE cash_shop_promotion_rules ADD COLUMN carry_rules_snapshot TEXT CHECK((rule_kind=0 AND carry_campaign IS NULL AND carry_period IS NULL AND carry_rules_version IS NULL AND carry_realm IS NULL AND carry_group IS NULL AND carry_rules_snapshot IS NULL) OR (rule_kind=1 AND carry_campaign IS NOT NULL AND carry_period IS NOT NULL AND carry_rules_version IS NOT NULL AND carry_realm IS NOT NULL AND carry_group IS NOT NULL AND carry_rules_snapshot IS NOT NULL AND json_valid(carry_rules_snapshot) AND carry_campaign=campaign AND carry_realm=realm AND carry_group=promotion_group AND carry_period<>period));
        """;

    internal static readonly string CurrentRules = Rules.Replace("PRIMARY KEY", ContinuationColumns + ",\n            PRIMARY KEY", StringComparison.Ordinal);

    // 只统一SQLite ADD COLUMN在表级PRIMARY KEY前后的已知布局；每列声明和CHECK仍逐字校验。
    internal static string NormalizeRules(string sql)
    {
        string value = Normalize(sql, "cash_shop_promotion_rules");
        string columns = Normalize(ContinuationColumns, "cash_shop_promotion_rules");
        string before = columns + ",";
        string after = "," + columns;
        if (value.Contains(before, StringComparison.Ordinal)) value = value.Replace(before, "", StringComparison.Ordinal);
        else if (value.Contains(after, StringComparison.Ordinal)) value = value.Replace(after, "", StringComparison.Ordinal);
        else return value;
        return value == Normalize(Rules, "cash_shop_promotion_rules") ? Normalize(CurrentRules, "cash_shop_promotion_rules") : value;
    }

    internal const string UpgradeSql = UpgradeColumnsSql + SingleSuccessor + ValidPredecessor;

    internal enum Layout { Absent, Private77, Private78 }

    // 历史布局按完整结构识别，迁移编号本身不能证明结构身份。
    internal static Layout Classify(SqliteConnection db, SqliteTransaction tx)
    {
        var objects = new Dictionary<string, (string Type, string Sql)>(StringComparer.OrdinalIgnoreCase);
        using (var query = db.CreateCommand())
        {
            query.Transaction = tx;
            query.CommandText = "SELECT type,name,sql FROM sqlite_schema WHERE substr(lower(name),1,20)='cash_shop_promotion_'";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.GetString(1);
                if (reader.IsDBNull(2) || !objects.TryAdd(name, (reader.GetString(0), reader.GetString(2))))
                    throw new InvalidDataException("Unknown promotion schema object: " + name);
            }
        }
        if (objects.Count == 0) return Layout.Absent;

        string actualRules = Required("table", "cash_shop_promotion_rules");
        bool previous = Normalize(actualRules, "cash_shop_promotion_rules") == Normalize(Rules, "cash_shop_promotion_rules");
        bool current = NormalizeRules(actualRules) == NormalizeRules(CurrentRules);
        if (!previous && !current)
            throw new InvalidDataException("Unknown promotion rules layout");
        foreach (var (name, expected) in new[]
        {
            ("cash_shop_promotion_orders", Orders), ("cash_shop_promotion_progress", Progress),
            ("cash_shop_promotion_awards", Awards)
        })
            Match("table", name, expected);
        foreach (string action in new[] { "update", "delete" })
        {
            string name = "cash_shop_promotion_rules_immutable_" + action;
            string expected = $"CREATE TRIGGER {name} BEFORE {action.ToUpperInvariant()} ON cash_shop_promotion_rules BEGIN SELECT RAISE(ABORT,'promotion rules are immutable'); END";
            Match("trigger", name, expected);
        }
        if (current)
        {
            Match("index", "cash_shop_promotion_single_successor", SingleSuccessor);
            Match("trigger", "cash_shop_promotion_valid_predecessor", ValidPredecessor);
        }
        if (objects.Count != (current ? 8 : 6))
            throw new InvalidDataException("Partial or unknown promotion schema objects");
        return current ? Layout.Private78 : Layout.Private77;

        string Required(string type, string name)
        {
            if (!objects.TryGetValue(name, out var actual) || actual.Type != type)
                throw new InvalidDataException("Missing or wrong-type promotion schema object: " + name);
            return actual.Sql;
        }
        void Match(string type, string name, string expected)
        {
            if (Normalize(Required(type, name), name) != Normalize(expected, name))
                throw new InvalidDataException("Unknown promotion schema definition: " + name);
        }
    }

    internal static bool IsPreviousPrivateVersion(SqliteConnection db, SqliteTransaction tx, long version)
    {
        if (version is not (77 or 78)) return false;
        var layout = Classify(db, tx);
        if (layout == Layout.Absent) return false;
        if (version == 77 && layout == Layout.Private77 || version == 78 && layout == Layout.Private78)
            return true;
        throw new InvalidDataException("Historical promotion version and layout disagree");
    }

    internal static void EnsureBase(SqliteConnection db, SqliteTransaction tx)
    {
        if (Classify(db, tx) != Layout.Absent) return;
        SqliteMigrations.Execute(db, tx, Sql);
        if (Classify(db, tx) != Layout.Private77)
            throw new InvalidDataException("Promotion base migration did not produce the known layout");
    }

    internal static void EnsureContinuation(SqliteConnection db, SqliteTransaction tx)
    {
        var layout = Classify(db, tx);
        if (layout == Layout.Absent)
            throw new InvalidDataException("Promotion base migration is missing");
        if (layout == Layout.Private77) SqliteMigrations.Execute(db, tx, UpgradeSql);
        Validate(db, tx);
    }

    internal static void Validate(SqliteConnection db, SqliteTransaction tx)
    {
        if (Classify(db, tx) != Layout.Private78)
            throw new InvalidDataException("Current promotion schema is incomplete");
    }

    static string Normalize(string sql, string name) => SqliteMigrations.NormalizeTable(sql, name)
        .Replace("IFNOTEXISTS", "", StringComparison.Ordinal);
}
