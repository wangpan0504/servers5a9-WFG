using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class RaidGuildContributionSchema
{
    internal const string Guild = "guild_id INTEGER CHECK(guild_id BETWEEN 0 AND 4294967295)";
    internal const string Joined = "guild_joined_at INTEGER CHECK((guild_id IS NULL AND guild_joined_at IS NULL) OR (guild_id IS NOT NULL AND guild_joined_at IS NOT NULL AND ((guild_id=0 AND guild_joined_at=0) OR (guild_id>0 AND guild_joined_at>=0))))";
    internal static string Definition => RaidLifecycleSchema.FiendwarDefinition.Replace("clear_week INTEGER,",
        $"clear_week INTEGER,\n{Guild},\n{Joined},", StringComparison.Ordinal);

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var (name, declaration) in new[] { ("guild_id", Guild), ("guild_joined_at", Joined) })
        {
            using var query = db.CreateCommand();
            query.Transaction = tx;
            query.CommandText = "SELECT count(*) FROM pragma_table_info('character_raid_runs') WHERE name=$name";
            query.Parameters.AddWithValue("$name", name);
            if (query.ExecuteScalar() is 0L)
                SqliteMigrations.Execute(db, tx, "ALTER TABLE character_raid_runs ADD COLUMN " + declaration);
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_raid_runs'";
            if (query.ExecuteScalar() is not string sql || !sql.Contains(declaration, StringComparison.Ordinal))
                throw new InvalidDataException("攻坚入场会籍列结构不兼容");
        }
    }
}
