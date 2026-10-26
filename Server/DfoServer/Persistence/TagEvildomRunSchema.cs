using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class TagEvildomRunSchema
{
    internal const string Table = "account_tag_evildom_runs";
    // mail_id是历史投递编号，不强引用可删除的邮箱；删角/删信不能返还已通关额度或删除永久幂等事实。
    internal const string Definition = """
        CREATE TABLE account_tag_evildom_runs(
          account TEXT NOT NULL,
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          first INTEGER NOT NULL CHECK(typeof(first)='integer' AND first BETWEEN 1 AND 65535),
          second INTEGER NOT NULL CHECK(typeof(second)='integer' AND second BETWEEN 1 AND 65535 AND second<>first),
          task_attempt INTEGER NOT NULL CHECK(typeof(task_attempt)='integer' AND task_attempt>0),
          configuration_revision INTEGER NOT NULL CHECK(typeof(configuration_revision)='integer' AND configuration_revision>0),
          team_index INTEGER NOT NULL CHECK(typeof(team_index)='integer' AND team_index BETWEEN 0 AND 2),
          dungeon_id INTEGER NOT NULL CHECK(dungeon_id IN(9608,400000095)),
          resource_key TEXT NOT NULL CHECK(length(resource_key)=64),
          admitted_at INTEGER NOT NULL CHECK(typeof(admitted_at)='integer' AND admitted_at>=0),
          clearance_receipt TEXT,
          clear_event TEXT CHECK(clear_event IS NULL OR length(clear_event)=32),
          reward_json TEXT CHECK(reward_json IS NULL OR json_valid(reward_json)),
          cleared_at INTEGER CHECK(cleared_at IS NULL OR (typeof(cleared_at)='integer' AND cleared_at>=0)),
          elapsed_ms INTEGER CHECK(elapsed_ms IS NULL OR (typeof(elapsed_ms)='integer' AND elapsed_ms>=0)),
          mail_id INTEGER CHECK(mail_id IS NULL OR (typeof(mail_id)='integer' AND mail_id>0)),
          PRIMARY KEY(account,instance_id),UNIQUE(first,task_attempt),
          CHECK((clearance_receipt IS NULL AND clear_event IS NULL AND reward_json IS NULL AND cleared_at IS NULL AND elapsed_ms IS NULL AND mail_id IS NULL)
            OR (clearance_receipt IS NOT NULL AND clearance_receipt=instance_id AND clear_event IS NOT NULL AND reward_json IS NOT NULL AND cleared_at IS NOT NULL AND elapsed_ms IS NOT NULL AND mail_id IS NOT NULL)),
          FOREIGN KEY(account,clearance_receipt) REFERENCES account_tag_evildom_clearances(account,receipt)
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
            throw new InvalidDataException("再战准入与领奖回执结构不兼容");
    }
}
