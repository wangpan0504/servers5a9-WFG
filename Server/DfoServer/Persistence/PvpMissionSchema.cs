using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class PvpMissionSchema
{
    internal const string FactTable = "character_pvp_mission_facts";
    internal const string ClaimTable = "character_pvp_mission_rewards";
    internal const string Facts = """
        CREATE TABLE character_pvp_mission_facts(
            character_id INTEGER NOT NULL,account TEXT NOT NULL,
            kind INTEGER NOT NULL CHECK(typeof(kind)='integer' AND kind IN(0,1)),
            job INTEGER NOT NULL CHECK(typeof(job)='integer' AND job BETWEEN 0 AND 15),
            parameter INTEGER NOT NULL CHECK(typeof(parameter)='integer' AND
                (kind=0 AND parameter BETWEEN 0 AND 11 OR kind=1 AND parameter BETWEEN 0 AND 2)),
            instance_id TEXT NOT NULL CHECK(length(instance_id)=32 AND instance_id NOT GLOB '*[^0-9a-f]*' AND instance_id<>'00000000000000000000000000000000'),
            PRIMARY KEY(character_id,kind,job,parameter),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;
    internal const string Claims = """
        CREATE TABLE character_pvp_mission_rewards(
            character_id INTEGER NOT NULL,account TEXT NOT NULL,
            mission_id INTEGER NOT NULL CHECK(typeof(mission_id)='integer' AND mission_id BETWEEN 1 AND 32767),
            rewards_json TEXT NOT NULL CHECK(json_valid(rewards_json) AND json_type(rewards_json)='array'),
            PRIMARY KEY(character_id,mission_id),
            FOREIGN KEY(character_id,account) REFERENCES characters(id,account)
        );
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var (table, definition) in new[] { (FactTable, Facts), (ClaimTable, Claims) })
        {
            using var query = db.CreateCommand();
            query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            query.Parameters.AddWithValue("$table", table);
            if (query.ExecuteScalar() is not { } existing) SqliteMigrations.Execute(db, tx, definition);
            else if (existing is not string sql || SqliteMigrations.NormalizeTable(sql, table) !=
                SqliteMigrations.NormalizeTable(definition, table)) throw new InvalidDataException("决斗任务账本结构不兼容：" + table);
        }
    }
}
