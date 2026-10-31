using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class AntonNormalProgressSchema
{
    internal const string Table = "character_anton_normal_progress";
    internal const string Definition = """
        CREATE TABLE character_anton_normal_progress(
          character_id INTEGER PRIMARY KEY,
          account TEXT NOT NULL,
          day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day BETWEEN 10000101 AND 99991231),
          branch INTEGER NOT NULL CHECK(typeof(branch)='integer' AND branch BETWEEN 1 AND 4),
          progress INTEGER NOT NULL CHECK(typeof(progress)='integer' AND progress BETWEEN 0 AND 6),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        command.Parameters.AddWithValue("$table", Table);
        if (command.ExecuteScalar() is not { } actual) SqliteMigrations.Execute(db, tx, Definition);
        else if (actual is not string sql || SqliteMigrations.NormalizeTable(sql, Table) != SqliteMigrations.NormalizeTable(Definition, Table))
            throw new InvalidDataException("普通安徒恩每日进度结构不兼容");
    }
}
