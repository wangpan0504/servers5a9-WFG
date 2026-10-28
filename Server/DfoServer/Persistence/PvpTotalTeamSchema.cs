using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class PvpTotalTeamSchema
{
    internal const string Table = "account_pvp_total_teams";
    internal const string Definition = """
        CREATE TABLE account_pvp_total_teams(
            account TEXT NOT NULL PRIMARY KEY,
            team_id TEXT NOT NULL UNIQUE CHECK(length(team_id)=32 AND team_id NOT GLOB '*[^0-9a-f]*' AND team_id<>'00000000000000000000000000000000'),
            revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),
            name TEXT NOT NULL UNIQUE CHECK(length(name) BETWEEN 2 AND 12),
            first_id INTEGER,
            second_id INTEGER,
            third_id INTEGER,
            CHECK(first_id IS NULL OR second_id IS NULL OR first_id<>second_id),
            CHECK(first_id IS NULL OR third_id IS NULL OR first_id<>third_id),
            CHECK(second_id IS NULL OR third_id IS NULL OR second_id<>third_id),
            FOREIGN KEY(first_id,account) REFERENCES characters(id,account),
            FOREIGN KEY(second_id,account) REFERENCES characters(id,account),
            FOREIGN KEY(third_id,account) REFERENCES characters(id,account)
        );
        """;

    public static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
        query.Parameters.AddWithValue("$table", Table);
        var existing = query.ExecuteScalar();
        if (existing is null) SqliteMigrations.Execute(db, tx, Definition);
        else if (existing is not string sql || SqliteMigrations.NormalizeTable(sql, Table) !=
            SqliteMigrations.NormalizeTable(Definition, Table))
            throw new InvalidDataException("总力队伍结构不兼容");
    }
}
