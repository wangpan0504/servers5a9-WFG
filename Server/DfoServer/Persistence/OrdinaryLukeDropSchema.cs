using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class OrdinaryLukeDropSchema
{
    internal const string Table = "character_luke_normal_drop_claims";
    internal const string Definition = """
        CREATE TABLE character_luke_normal_drop_claims(
          character_id INTEGER NOT NULL,
          account TEXT NOT NULL,
          day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day BETWEEN 10000101 AND 99991231),
          source_monster_id INTEGER NOT NULL CHECK(typeof(source_monster_id)='integer' AND source_monster_id>0),
          first_event_id TEXT NOT NULL CHECK(length(first_event_id)=32),
          first_instance_id TEXT NOT NULL CHECK(length(first_instance_id)=32),
          PRIMARY KEY(character_id,day,source_monster_id),
          UNIQUE(character_id,first_event_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        command.Parameters.AddWithValue("$table", Table);
        var actual = command.ExecuteScalar();
        if (actual is null) SqliteMigrations.Execute(db, tx, Definition);
        else if (actual is not string sql || SqliteMigrations.NormalizeTable(sql, Table) != SqliteMigrations.NormalizeTable(Definition, Table))
            throw new InvalidDataException("普通卢克每日掉落资格结构不兼容");
    }
}
