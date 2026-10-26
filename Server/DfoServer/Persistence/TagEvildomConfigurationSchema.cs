using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class TagEvildomConfigurationSchema
{
    internal const string Table = "account_tag_evildom_configuration";
    internal const string ClearanceTable = "account_tag_evildom_clearances";
    // 仅服务端正式领奖可创建事实；配置/初始化不能写入、改写或删除它。无记录即本服尚未通关。
    internal const string ClearanceDefinition = """
        CREATE TABLE account_tag_evildom_clearances(
          receipt TEXT NOT NULL CHECK(length(receipt)>0),
          account TEXT NOT NULL,
          day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day>0),
          week INTEGER NOT NULL CHECK(typeof(week)='integer' AND week>0),
          first INTEGER NOT NULL CHECK(typeof(first)='integer' AND first BETWEEN 1 AND 65535),
          second INTEGER NOT NULL CHECK(typeof(second)='integer' AND second BETWEEN 1 AND 65535 AND second<>first),
          PRIMARY KEY(account,receipt)
        );
        """;
    internal const string Definition = """
        CREATE TABLE account_tag_evildom_configuration(
          account TEXT PRIMARY KEY,
          revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),
          selected_team INTEGER NOT NULL CHECK(typeof(selected_team)='integer' AND selected_team BETWEEN 0 AND 2),
          first0 INTEGER NOT NULL CHECK(typeof(first0)='integer' AND first0 BETWEEN 0 AND 65535),
          second0 INTEGER NOT NULL CHECK(typeof(second0)='integer' AND second0 BETWEEN 0 AND 65535),
          first1 INTEGER NOT NULL CHECK(typeof(first1)='integer' AND first1 BETWEEN 0 AND 65535),
          second1 INTEGER NOT NULL CHECK(typeof(second1)='integer' AND second1 BETWEEN 0 AND 65535),
          first2 INTEGER NOT NULL CHECK(typeof(first2)='integer' AND first2 BETWEEN 0 AND 65535),
          second2 INTEGER NOT NULL CHECK(typeof(second2)='integer' AND second2 BETWEEN 0 AND 65535),
          CHECK((first0=0 AND second0=0) OR (first0>0 AND second0>0 AND first0<>second0)),
          CHECK((first1=0 AND second1=0) OR (first1>0 AND second1>0 AND first1<>second1)),
          CHECK((first2=0 AND second2=0) OR (first2>0 AND second2>0 AND first2<>second2))
        );
        """;

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var (table, definition) in new[] { (Table, Definition), (ClearanceTable, ClearanceDefinition) })
        {
            using var query = db.CreateCommand();
            query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            query.Parameters.AddWithValue("$table", table);
            var actual = query.ExecuteScalar();
            if (actual is null) SqliteMigrations.Execute(db, tx, definition);
            else if (actual is not string sql || SqliteMigrations.NormalizeTable(sql, table) != SqliteMigrations.NormalizeTable(definition, table))
                throw new InvalidDataException("再战魔界编队或通关事实表结构不兼容");
        }
    }
}
