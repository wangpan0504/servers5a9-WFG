using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class TagEvildomChallengeSchema
{
    internal const string Table = "account_tag_evildom_challenges";
    // 不外键到角色：删角、换队、断线均不能清掉已经跨日的未完成尝试。
    internal const string Definition = """
        CREATE TABLE account_tag_evildom_challenges(
          account TEXT NOT NULL,
          day INTEGER NOT NULL CHECK(day>0),
          week INTEGER NOT NULL CHECK(week>0),
          team_index INTEGER NOT NULL CHECK(team_index BETWEEN 0 AND 2),
          maze_ordinal INTEGER NOT NULL CHECK(maze_ordinal BETWEEN 0 AND 4),
          dungeon_id INTEGER NOT NULL CHECK(dungeon_id IN(9608,400000095)),
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          first INTEGER NOT NULL CHECK(first BETWEEN 1 AND 65535),
          second INTEGER NOT NULL CHECK(second BETWEEN 1 AND 65535 AND second<>first),
          admitted_at INTEGER NOT NULL CHECK(admitted_at>=0),
          sequence INTEGER NOT NULL CHECK(sequence>0),
          state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 2),
          PRIMARY KEY(account,day,team_index)
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
            throw new InvalidDataException("再战挑战路线结构不兼容");
    }
}
