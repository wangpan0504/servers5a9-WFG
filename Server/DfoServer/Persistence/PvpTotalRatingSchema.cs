using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class PvpTotalRatingSchema
{
    internal const string StateTable = "account_pvp_total_ratings";
    internal const string ClaimTable = "account_pvp_total_rating_claims";
    internal const string StateDefinition = """
        CREATE TABLE account_pvp_total_ratings(
            account TEXT NOT NULL PRIMARY KEY,
            team_id TEXT NOT NULL UNIQUE REFERENCES account_pvp_total_teams(team_id),
            revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),
            state_json TEXT NOT NULL CHECK(json_valid(state_json) AND json_type(state_json)='object')
        );
        """;
    internal const string ClaimDefinition = """
        CREATE TABLE account_pvp_total_rating_claims(
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            match_id TEXT NOT NULL REFERENCES pvp_arena_match_results(match_id),
            account TEXT NOT NULL,
            team_id TEXT NOT NULL REFERENCES account_pvp_total_teams(team_id),
            outcome INTEGER NOT NULL CHECK(typeof(outcome)='integer' AND outcome BETWEEN 0 AND 3),
            receipt_json TEXT CHECK(receipt_json IS NULL OR json_valid(receipt_json) AND json_type(receipt_json)='object'),
            UNIQUE(match_id,account)
        );
        """;
    internal const string PendingIndex = "CREATE INDEX ix_pvp_total_rating_pending ON account_pvp_total_rating_claims(account,sequence) WHERE receipt_json IS NULL";

    public static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var (table, definition) in new[] { (StateTable, StateDefinition), (ClaimTable, ClaimDefinition) })
        {
            using var query = db.CreateCommand();
            query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            query.Parameters.AddWithValue("$table", table);
            var existing = query.ExecuteScalar();
            if (existing is null) SqliteMigrations.Execute(db, tx, definition);
            else if (existing is not string sql || SqliteMigrations.NormalizeTable(sql, table) !=
                SqliteMigrations.NormalizeTable(definition, table)) throw new InvalidDataException("总力评分账本结构不兼容：" + table);
        }
        using var index = db.CreateCommand();
        index.Transaction = tx;
        index.CommandText = "SELECT sql FROM sqlite_schema WHERE type='index' AND name='ix_pvp_total_rating_pending'";
        if (index.ExecuteScalar() is null) SqliteMigrations.Execute(db, tx, PendingIndex);
        ValidateIndex(db, tx);
    }

    internal static void ValidateIndex(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='index' AND name='ix_pvp_total_rating_pending'";
        if (query.ExecuteScalar() is not string sql || SqliteMigrations.NormalizeTable(sql, ClaimTable) !=
            SqliteMigrations.NormalizeTable(PendingIndex, ClaimTable)) throw new InvalidDataException("总力待结算索引结构不兼容");
    }
}
