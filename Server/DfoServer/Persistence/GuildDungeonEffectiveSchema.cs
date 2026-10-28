using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class GuildDungeonEffectiveSchema
{
    internal const string Definition = """
        CREATE TABLE IF NOT EXISTS guild_dungeon_effective_progress(
          guild_id INTEGER NOT NULL,
          dungeon_id INTEGER NOT NULL,
          effective_clear_units INTEGER NOT NULL CHECK(effective_clear_units BETWEEN 0 AND 4294967295),
          decay_anchor_unix INTEGER NOT NULL CHECK(decay_anchor_unix>0),
          PRIMARY KEY(guild_id,dungeon_id),
          FOREIGN KEY(guild_id,dungeon_id) REFERENCES guild_dungeon_progress(guild_id,dungeon_id) ON DELETE CASCADE
        );
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = Definition; command.ExecuteNonQuery();
        // 旧数据从启用时开始计时；资源上限在领域计算中夹取，升级不依赖PVF或追溯衰减。
        command.CommandText = """
            INSERT INTO guild_dungeon_effective_progress(guild_id,dungeon_id,effective_clear_units,decay_anchor_unix)
            SELECT guild_id,dungeon_id,clear_count,$now FROM guild_dungeon_progress WHERE true
            ON CONFLICT(guild_id,dungeon_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }
}
