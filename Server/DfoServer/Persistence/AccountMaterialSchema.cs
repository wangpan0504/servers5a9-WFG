namespace DfoServer.Persistence;

internal static class AccountMaterialSchema
{
    internal const string RewardIndex = "CREATE INDEX IF NOT EXISTS character_raid_rewards_account_time ON character_raid_reward_receipts(account,created_at);";
    internal static void ValidateIndex(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='index' AND name='character_raid_rewards_account_time'";
        if (command.ExecuteScalar() is not string actual ||
            SqliteMigrations.NormalizeTable(actual, "character_raid_reward_receipts") !=
            SqliteMigrations.NormalizeTable(RewardIndex.Replace(" IF NOT EXISTS", "", StringComparison.Ordinal), "character_raid_reward_receipts"))
            throw new InvalidDataException("攻坚材料历史获得索引结构不兼容");
    }
    internal const string Balances = """
        CREATE TABLE account_material_balances(
          account TEXT NOT NULL REFERENCES account_wallets(account),
          item_id INTEGER NOT NULL CHECK(item_id>0),
          balance INTEGER NOT NULL CHECK(balance BETWEEN 0 AND 2147483647),
          raid_week INTEGER NOT NULL CHECK(raid_week BETWEEN 0 AND 3652058),
          raid_earned INTEGER NOT NULL CHECK(raid_earned BETWEEN 0 AND 2147483647),
          character_earned TEXT NOT NULL CHECK(json_valid(character_earned) AND json_type(character_earned)='object'),
          PRIMARY KEY(account,item_id)
        );
        """;
    internal const string Audit = """
        CREATE TABLE account_material_audit(
          audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
          account TEXT NOT NULL,
          character_id INTEGER NOT NULL CHECK(character_id BETWEEN 0 AND 65535),
          generation INTEGER NOT NULL CHECK(generation>=0),
          action_name TEXT NOT NULL,
          item_id INTEGER NOT NULL CHECK(item_id>0),
          before_state TEXT NOT NULL CHECK(json_valid(before_state) AND json_type(before_state)='object'),
          after_state TEXT NOT NULL CHECK(json_valid(after_state) AND json_type(after_state)='object')
        );
        """;
}
