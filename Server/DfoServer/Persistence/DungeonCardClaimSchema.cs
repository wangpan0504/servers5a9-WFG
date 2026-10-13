using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DfoServer.Persistence;

internal static class DungeonCardClaimSchema
{
    internal const string EventsSql = """
        CREATE TABLE server_events(
          event_id INTEGER NOT NULL PRIMARY KEY CHECK(event_id BETWEEN 1 AND 65535),
          revision INTEGER NOT NULL CHECK(revision>0),starts_at INTEGER,ends_at INTEGER,
          override_active INTEGER CHECK(override_active IS NULL OR override_active IN (0,1)),
          CHECK((starts_at IS NULL AND ends_at IS NULL) OR
            (starts_at IS NOT NULL AND ends_at IS NOT NULL AND starts_at>=0 AND ends_at>starts_at AND ends_at<=253402300799)));
        """;

    public static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        int present = (HasColumn(db,tx,"character_dungeon_card_rewards","claimed") ? 1 : 0) +
            (TableSql(db,tx,"server_events") is null ? 0 : 1);
        if (present == 0)
        {
            // 旧回执已在通关时发奖，缺省为已领取，不能升级后再发一次。
            SqliteMigrations.Execute(db, tx, "ALTER TABLE character_dungeon_card_rewards ADD COLUMN claimed INTEGER NOT NULL DEFAULT 1 CHECK(claimed IN(0,1))");
            SqliteMigrations.Execute(db, tx, EventsSql);
        }
        else if (!IsPreviousLayout(db,tx))
            throw new InvalidDataException("未知翻牌领取或活动库表形状");
        TransferLegacyContracts(db,tx);
    }

    internal static bool IsPreviousLayout(SqliteConnection db, SqliteTransaction tx) =>
        ColumnMatches(db,tx,"character_dungeon_card_rewards","claimed","1","CHECK(CLAIMEDIN(0,1))") &&
        (!HasColumn(db,tx,"account_card_memberships","returning_player_expires") ||
            ColumnMatches(db,tx,"account_card_memberships","returning_player_expires","0","CHECK(RETURNING_PLAYER_EXPIRES>=0)")) &&
        TableSql(db,tx,"server_events") is { } events && Normalize(events) == Normalize(EventsSql);

    static bool HasColumn(SqliteConnection db, SqliteTransaction tx, string table, string column)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info($table) WHERE name=$column";
        query.Parameters.AddWithValue("$table",table);
        query.Parameters.AddWithValue("$column",column);
        return query.ExecuteScalar() is 1L;
    }

    static bool ColumnMatches(SqliteConnection db, SqliteTransaction tx, string table, string column, string defaultValue, string check)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = """SELECT count(*) FROM pragma_table_info($table) WHERE name=$column AND upper(type)='INTEGER' AND "notnull"=1 AND dflt_value=$default""";
        query.Parameters.AddWithValue("$table",table);
        query.Parameters.AddWithValue("$column",column);
        query.Parameters.AddWithValue("$default",defaultValue);
        return query.ExecuteScalar() is 1L && TableSql(db,tx,table) is { } sql && Normalize(sql).Contains(check,StringComparison.Ordinal);
    }

    static string? TableSql(SqliteConnection db, SqliteTransaction tx, string table)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        query.Parameters.AddWithValue("$table",table);
        return query.ExecuteScalar() as string;
    }

    static string Normalize(string sql) => Regex.Replace(sql,"\\s+","").TrimEnd(';').ToUpperInvariant();

    static void TransferLegacyContracts(SqliteConnection db, SqliteTransaction tx)
    {
        using var legacy = db.CreateCommand();
        legacy.Transaction = tx;
        legacy.CommandText = "SELECT account,black_diamond_expires,gold_card_expires FROM account_card_memberships WHERE black_diamond_expires>0 OR gold_card_expires>0";
        using var rows = legacy.ExecuteReader();
        while (rows.Read())
        {
            string account = rows.GetString(0);
            using var query = db.CreateCommand();
            query.Transaction = tx;
            query.Parameters.AddWithValue("$account",account);
            query.CommandText = "SELECT revision,premium_contracts FROM account_wallets WHERE account=$account";
            long revision = 0;
            bool exists = false;
            var contracts = new SortedDictionary<int,long>();
            using (var wallet = query.ExecuteReader())
            {
                if (wallet.Read())
                {
                    exists = true;
                    revision = wallet.GetInt64(0);
                    try
                    {
                        contracts = JsonSerializer.Deserialize<SortedDictionary<int,long>>(wallet.GetString(1))
                            ?? throw new InvalidDataException("已有契约账本不是对象");
                    }
                    catch (JsonException error)
                    {
                        throw new InvalidDataException("已有契约账本不是合法的类型/期限对象", error);
                    }
                }
            }
            if (contracts.Any(pair => pair.Key <= 0 || pair.Value <= 0))
                throw new InvalidDataException("已有契约类型或期限非法");
            bool changed = false;
            for (int index = 0; index < 2; index++)
            {
                int type = index == 0 ? 56 : 58;
                long expires = rows.GetInt64(index + 1);
                if (expires <= contracts.GetValueOrDefault(type)) continue;
                contracts[type] = expires;
                changed = true;
            }
            if (!changed) continue;
            if (revision == long.MaxValue) throw new InvalidDataException("已有账号钱包版本已耗尽");
            query.Parameters.AddWithValue("$revision",revision);
            query.Parameters.AddWithValue("$next",checked(revision + 1));
            query.Parameters.AddWithValue("$contracts",JsonSerializer.Serialize(contracts));
            query.CommandText = exists
                ? "UPDATE account_wallets SET revision=$next,premium_contracts=$contracts WHERE account=$account AND revision=$revision"
                : "INSERT INTO account_wallets(account,revision,premium_contracts) VALUES($account,1,$contracts)";
            if (query.ExecuteNonQuery() != 1) throw new InvalidOperationException("旧会员契约接管版本冲突");
        }
    }
}
