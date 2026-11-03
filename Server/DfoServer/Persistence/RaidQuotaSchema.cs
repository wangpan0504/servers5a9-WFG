using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class RaidQuotaSchema
{
    internal const string EpochColumns = "entry_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(entry_epoch)='integer' AND entry_epoch>=0),\nreward_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(reward_epoch)='integer' AND reward_epoch>=0),";
    internal static string RunsDefinition => RaidGuildContributionSchema.Definition.Replace("PRIMARY KEY(character_id,instance_id),", EpochColumns + "PRIMARY KEY(character_id,instance_id),", StringComparison.Ordinal);
    internal static string RewardDefinition => RaidLifecycleSchema.FiendwarRewardDefinition.Replace(
        "PRIMARY KEY(character_id,instance_id,phase,kind)",
        "reward_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(reward_epoch)='integer' AND reward_epoch>=0),\nPRIMARY KEY(character_id,instance_id,phase,kind)", StringComparison.Ordinal);
    internal const string EpochTable = """
        CREATE TABLE account_raid_quota_epochs(
          account TEXT NOT NULL,raid_type INTEGER NOT NULL CHECK(raid_type BETWEEN 0 AND 3),
          entry_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(entry_epoch)='integer' AND entry_epoch>=0),
          reward_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(reward_epoch)='integer' AND reward_epoch>=0),
          PRIMARY KEY(account,raid_type),FOREIGN KEY(account) REFERENCES accounts(account_id));
        """;
    static readonly (string Name, string Sql)[] Indexes =
    [
        ("character_raid_entry_clear", "CREATE INDEX character_raid_entry_clear ON character_raid_runs(account,raid_type,entry_epoch,clear_week) WHERE state=2"),
        ("character_raid_reward_epoch", "CREATE INDEX character_raid_reward_epoch ON character_raid_reward_receipts(account,raid_type,reward_epoch,week_key,final_phase,instance_id)")
    ];

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        AddColumnIfMissing(db, tx, "character_raid_runs", "entry_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(entry_epoch)='integer' AND entry_epoch>=0)");
        AddColumnIfMissing(db, tx, "character_raid_runs", "reward_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(reward_epoch)='integer' AND reward_epoch>=0)");
        AddColumnIfMissing(db, tx, "character_raid_reward_receipts", "reward_epoch INTEGER NOT NULL DEFAULT 0 CHECK(typeof(reward_epoch)='integer' AND reward_epoch>=0)");
        EnsureGuide(db, tx);
        SqliteMigrations.Execute(db, tx, EpochTable.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal));
        foreach (var (_, sql) in Indexes)
            SqliteMigrations.Execute(db, tx, sql.Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal));
        ValidateIndexes(db, tx);
    }

    internal static void ValidateIndexes(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='index' AND name=$name";
        query.Parameters.Add("$name", SqliteType.Text);
        foreach (var (name, expected) in Indexes)
        {
            query.Parameters["$name"].Value = name;
            if (query.ExecuteScalar() is not string actual || SqliteMigrations.NormalizeTable(actual, name) != SqliteMigrations.NormalizeTable(expected, name))
                throw new InvalidDataException("团本额度索引结构不兼容: " + name);
        }
    }

    static void AddColumnIfMissing(SqliteConnection db, SqliteTransaction tx, string table, string declaration)
    {
        string name = declaration[..declaration.IndexOf(' ')];
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info($table) WHERE name=$name";
        query.Parameters.AddWithValue("$table", table); query.Parameters.AddWithValue("$name", name);
        if (Convert.ToInt32(query.ExecuteScalar()) == 0)
            SqliteMigrations.Execute(db, tx, "ALTER TABLE " + table + " ADD COLUMN " + declaration);
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        if (query.ExecuteScalar() is not string sql || !sql.Contains(declaration, StringComparison.Ordinal))
            throw new InvalidDataException("攻坚额度世代列结构不兼容: " + table + "." + name);
    }

    static void EnsureGuide(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='account_fiendwar_guides'";
        if (query.ExecuteScalar() is not string actual) { SqliteMigrations.Execute(db, tx, RaidLifecycleSchema.CurrentFiendwarGuideBindings); return; }
        if (SqliteMigrations.NormalizeTable(actual, "account_fiendwar_guides") ==
            SqliteMigrations.NormalizeTable(RaidLifecycleSchema.CurrentFiendwarGuideBindings, "account_fiendwar_guides")) return;
        if (SqliteMigrations.NormalizeTable(actual, "account_fiendwar_guides") !=
            SqliteMigrations.NormalizeTable(RaidLifecycleSchema.FiendwarGuideBindings, "account_fiendwar_guides"))
            throw new InvalidDataException("漩涡引导绑定表结构不兼容");
        SqliteMigrations.Execute(db, tx, RaidLifecycleSchema.CurrentFiendwarGuideBindings.Replace("account_fiendwar_guides", "account_fiendwar_guides_new", StringComparison.Ordinal));
        SqliteMigrations.Execute(db, tx, "INSERT INTO account_fiendwar_guides_new(account,slot,week,entry_epoch,character_id) SELECT account,slot,week,0,character_id FROM account_fiendwar_guides;");
        query.CommandText = """
            SELECT (SELECT count(*) FROM account_fiendwar_guides)=(SELECT count(*) FROM account_fiendwar_guides_new)
              AND NOT EXISTS(SELECT account,slot,week,character_id FROM account_fiendwar_guides
                EXCEPT SELECT account,slot,week,character_id FROM account_fiendwar_guides_new WHERE entry_epoch=0)
            """;
        if (Convert.ToInt32(query.ExecuteScalar()) != 1) throw new InvalidDataException("漩涡引导绑定迁移校验失败");
        SqliteMigrations.Execute(db, tx, "DROP TABLE account_fiendwar_guides;");
        SqliteMigrations.Execute(db, tx, "ALTER TABLE account_fiendwar_guides_new RENAME TO account_fiendwar_guides;");
    }
}
