using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class PvpArenaResultSchema
{
    internal const string Table = "pvp_arena_match_results";
    internal const string Definition = """
        CREATE TABLE pvp_arena_match_results(
            match_id TEXT NOT NULL PRIMARY KEY CHECK(length(match_id)=32 AND match_id NOT GLOB '*[^0-9a-f]*' AND match_id<>'00000000000000000000000000000000'),
            result_json TEXT NOT NULL CHECK(json_valid(result_json) AND json_type(result_json)='object'),
            finished_at INTEGER NOT NULL CHECK(finished_at>=0)
        );
        """;

    public static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        command.Parameters.AddWithValue("$table", Table);
        var actual = command.ExecuteScalar();
        if (actual is null) SqliteMigrations.Execute(db, tx, Definition);
        else if (actual is not string sql || SqliteMigrations.NormalizeTable(sql, Table) !=
            SqliteMigrations.NormalizeTable(Definition, Table))
            throw new InvalidDataException("整局决斗结果回执结构不兼容");
    }
}
