using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class PvpDailyMissionSchema
{
    internal const string Table = "character_pvp_daily_missions";
    internal const string Sql = """
        CREATE TABLE character_pvp_daily_missions(
            character_id INTEGER NOT NULL,account TEXT NOT NULL,
            service_day INTEGER NOT NULL CHECK(typeof(service_day)='integer' AND service_day BETWEEN 1 AND 3652058),
            state_json TEXT NOT NULL CHECK(json_valid(state_json) AND json_type(state_json)='object'),
            PRIMARY KEY(character_id,service_day),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        // 与训练账本一致：只接受已经完全匹配的结构，不覆盖未知表或已有数据。
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        query.Parameters.AddWithValue("$table", Table);
        if (query.ExecuteScalar() is not { } existing) SqliteMigrations.Execute(db, tx, Sql);
        else if (existing is not string sql || SqliteMigrations.NormalizeTable(sql, Table) != SqliteMigrations.NormalizeTable(Sql, Table))
            throw new InvalidDataException("决斗日常账本结构不兼容");
    }
}
