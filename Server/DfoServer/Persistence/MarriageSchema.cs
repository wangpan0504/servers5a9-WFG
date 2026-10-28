using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class MarriageSchema
{
    internal const string RecordsSql = """
        CREATE TABLE marriage_records(
          marriage_id TEXT PRIMARY KEY CHECK(length(marriage_id)=32 AND marriage_id NOT GLOB '*[^0-9a-f]*'),
          context_id INTEGER NOT NULL UNIQUE CHECK(typeof(context_id)='integer' AND context_id BETWEEN 1 AND 4294967295),
          revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),
          first_id INTEGER NOT NULL CHECK(typeof(first_id)='integer' AND first_id BETWEEN 1 AND 65535),
          second_id INTEGER NOT NULL CHECK(typeof(second_id)='integer' AND second_id BETWEEN 1 AND 65535 AND second_id<>first_id),
          first_account TEXT NOT NULL CHECK(length(first_account)>0),
          second_account TEXT NOT NULL CHECK(length(second_account)>0 AND second_account<>first_account),
          state INTEGER NOT NULL CHECK(typeof(state)='integer' AND state BETWEEN 0 AND 4),
          created_at INTEGER NOT NULL CHECK(typeof(created_at)='integer' AND created_at>=0),
          ended_at INTEGER NOT NULL CHECK(typeof(ended_at)='integer' AND ended_at>=0),
          aggregate_json TEXT NOT NULL CHECK(json_valid(aggregate_json) AND json_type(aggregate_json)='object'),
          CHECK((state=0 AND ended_at>=created_at) OR (state<>0 AND ended_at=0))
        );
        """;
    // 历史关系不外键绑定角色；仅有效成员行阻止删角，结束关系后允许正常删除角色。
    internal const string ActiveMembersSql = """
        CREATE TABLE marriage_active_members(
          character_id INTEGER PRIMARY KEY REFERENCES characters(id),
          marriage_id TEXT NOT NULL REFERENCES marriage_records(marriage_id)
        );
        """;
    internal const string IndexesSql = """
        CREATE INDEX marriage_active_relation ON marriage_active_members(marriage_id);
        CREATE INDEX marriage_first_history ON marriage_records(first_id,created_at DESC,marriage_id);
        CREATE INDEX marriage_second_history ON marriage_records(second_id,created_at DESC,marriage_id);
        """;
    internal const string Sql = RecordsSql + ActiveMembersSql + IndexesSql;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var (name, definition) in new[] { ("marriage_records", RecordsSql), ("marriage_active_members", ActiveMembersSql) })
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$name";
            command.Parameters.AddWithValue("$name", name);
            var actual = command.ExecuteScalar();
            if (actual is null) SqliteMigrations.Execute(db, tx, definition);
            else if (actual is not string sql || SqliteMigrations.NormalizeTable(sql, name) != SqliteMigrations.NormalizeTable(definition, name))
                throw new InvalidDataException("婚姻关系表结构不兼容: " + name);
        }
        SqliteMigrations.Execute(db, tx, IndexesSql.Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.Ordinal));
    }
}
