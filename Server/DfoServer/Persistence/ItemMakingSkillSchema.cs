using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class ItemMakingSkillSchema
{
    internal const string Sql = """
        CREATE TABLE item_making_skill_info(
          charac_no INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          weapon INTEGER NOT NULL DEFAULT 0 CHECK(weapon BETWEEN 0 AND 1000),
          cloth INTEGER NOT NULL DEFAULT 0 CHECK(cloth BETWEEN 0 AND 1000),
          leather INTEGER NOT NULL DEFAULT 0 CHECK(leather BETWEEN 0 AND 1000),
          light_armor INTEGER NOT NULL DEFAULT 0 CHECK(light_armor BETWEEN 0 AND 1000),
          heavy_armor INTEGER NOT NULL DEFAULT 0 CHECK(heavy_armor BETWEEN 0 AND 1000),
          plate INTEGER NOT NULL DEFAULT 0 CHECK(plate BETWEEN 0 AND 1000),
          amulet INTEGER NOT NULL DEFAULT 0 CHECK(amulet BETWEEN 0 AND 1000),
          wrist INTEGER NOT NULL DEFAULT 0 CHECK(wrist BETWEEN 0 AND 1000),
          ring INTEGER NOT NULL DEFAULT 0 CHECK(ring BETWEEN 0 AND 1000),
          support INTEGER NOT NULL DEFAULT 0 CHECK(support BETWEEN 0 AND 1000),
          magic_stone INTEGER NOT NULL DEFAULT 0 CHECK(magic_stone BETWEEN 0 AND 1000),
          FOREIGN KEY(charac_no,account) REFERENCES characters(id,account) ON DELETE CASCADE
        ) STRICT
        """;

    internal static bool Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE name='item_making_skill_info' AND type='table'";
        if (command.ExecuteScalar() is string existing)
        {
            static string Normalize(string sql) => string.Concat(sql.Where(value => !char.IsWhiteSpace(value))).TrimEnd(';').ToLowerInvariant();
            if (Normalize(existing) != Normalize(Sql)) throw new InvalidDataException("未知制作熟练度表结构");
            return true;
        }
        command.CommandText = Sql;
        command.ExecuteNonQuery();
        return false;
    }
}
