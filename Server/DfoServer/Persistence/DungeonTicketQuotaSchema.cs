using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

// 券购买的额度独立于已用次数，周期失效不改写历史入场和领奖事实。
internal static class DungeonTicketQuotaSchema
{
    internal const string AccountDefinition = """
        CREATE TABLE account_dungeon_incount_extra(
          account TEXT NOT NULL,dungeon INTEGER NOT NULL CHECK(dungeon>0),
          period INTEGER NOT NULL,extra INTEGER NOT NULL CHECK(extra BETWEEN 0 AND 1000000),
          PRIMARY KEY(account,dungeon),FOREIGN KEY(account) REFERENCES accounts(account_id));
        """;
    internal const string RewardDefinition = """
        CREATE TABLE character_dungeon_reward_extra(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,kind INTEGER NOT NULL CHECK(kind IN(0,1)),
          target INTEGER NOT NULL CHECK(target>0),day INTEGER NOT NULL,
          daily_extra INTEGER NOT NULL CHECK(daily_extra BETWEEN 0 AND 1000000),week INTEGER NOT NULL,
          weekly_extra INTEGER NOT NULL CHECK(weekly_extra BETWEEN 0 AND 1000000),
          PRIMARY KEY(character_id,kind,target),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account) ON DELETE CASCADE);
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        SqliteMigrations.Execute(db, tx, AccountDefinition.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal));
        SqliteMigrations.Execute(db, tx, RewardDefinition.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal));
        SqliteMigrations.Execute(db, tx, SingleDefinition.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.Ordinal));
    }
    internal const string SingleDefinition = """
        CREATE TABLE account_single_dungeon_bindings(
          account TEXT NOT NULL,group_id INTEGER NOT NULL CHECK(group_id IN(1,3)),
          period INTEGER NOT NULL,character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
          PRIMARY KEY(account,group_id,character_id),
          FOREIGN KEY(account) REFERENCES accounts(account_id));
        """;
}
