using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class ImperialArenaSchema
{
    internal const string Routes = """
        CREATE TABLE imperial_arena_daily_routes(
          day INTEGER NOT NULL PRIMARY KEY CHECK(day>=20000101),
          route_json TEXT NOT NULL CHECK(json_valid(route_json) AND json_type(route_json)='object')
        );
        """;
    internal const string Configuration = """
        CREATE TABLE account_imperial_arena_configuration(
          account TEXT NOT NULL PRIMARY KEY,
          revision INTEGER NOT NULL CHECK(typeof(revision)='integer' AND revision>0),
          first INTEGER NOT NULL CHECK(first BETWEEN 0 AND 65535),
          second INTEGER NOT NULL CHECK(second BETWEEN 0 AND 65535),
          CHECK((first=0 AND second=0) OR (first>0 AND second>0 AND first<>second))
        );
        """;
    internal const string Days = """
        CREATE TABLE account_imperial_arena_days(
          account TEXT NOT NULL,day INTEGER NOT NULL CHECK(day>=20000101),
          mode INTEGER NOT NULL CHECK(mode IN(1,2)),
          hard_round INTEGER NOT NULL DEFAULT 0 CHECK(hard_round BETWEEN 0 AND 7),
          PRIMARY KEY(account,day)
        );
        """;
    internal const string Runs = """
        CREATE TABLE account_imperial_arena_runs(
          account TEXT NOT NULL,instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          day INTEGER NOT NULL,week INTEGER NOT NULL,mode INTEGER NOT NULL CHECK(mode IN(1,2)),
          first INTEGER NOT NULL CHECK(first BETWEEN 1 AND 65535),
          second INTEGER NOT NULL CHECK(second BETWEEN 1 AND 65535 AND second<>first),
          configuration_revision INTEGER NOT NULL CHECK(configuration_revision>0),
          task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
          admitted_at INTEGER NOT NULL CHECK(admitted_at>=0),
          minimum_clear_ms INTEGER NOT NULL CHECK(minimum_clear_ms>0),
          team_json TEXT NOT NULL CHECK(json_valid(team_json) AND json_array_length(team_json)=4),
          route_json TEXT NOT NULL CHECK(json_valid(route_json)),
          cleared_round INTEGER NOT NULL DEFAULT 0 CHECK(cleared_round BETWEEN 0 AND 7),
          events_json TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(events_json)),
          elapsed_ms INTEGER CHECK(elapsed_ms IS NULL OR elapsed_ms>=0),
          closed INTEGER NOT NULL DEFAULT 0 CHECK(closed IN(0,1)),
          ranked_week INTEGER,
          PRIMARY KEY(account,instance_id),UNIQUE(first,task_attempt),
          FOREIGN KEY(account,day) REFERENCES account_imperial_arena_days(account,day)
        );
        """;
    internal const string Challenges = """
        CREATE TABLE account_imperial_arena_challenge_claims(
          account TEXT NOT NULL,week INTEGER NOT NULL CHECK(week>=20000101),
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          mail_id INTEGER NOT NULL CHECK(mail_id>0),
          reward_json TEXT NOT NULL CHECK(json_valid(reward_json)),
          PRIMARY KEY(account,week),
          FOREIGN KEY(account,instance_id) REFERENCES account_imperial_arena_runs(account,instance_id)
        );
        """;
    // 奖励事实不引用可删邮件和角色，删信/删角不会重置账号本日奖励资格。
    internal const string Rewards = """
        CREATE TABLE account_imperial_arena_rewards(
          account TEXT NOT NULL,day INTEGER NOT NULL,monster INTEGER NOT NULL CHECK(monster>0),
          instance_id TEXT NOT NULL,event_id TEXT NOT NULL CHECK(length(event_id)=32),
          reward_json TEXT NOT NULL CHECK(json_valid(reward_json)),
          mail_id INTEGER NOT NULL CHECK(mail_id>=0),
          PRIMARY KEY(account,day,monster),
          FOREIGN KEY(account,instance_id) REFERENCES account_imperial_arena_runs(account,instance_id)
        );
        """;
    internal static readonly (string Table, string Definition)[] Tables =
    [
        ("imperial_arena_daily_routes", Routes),
        ("account_imperial_arena_configuration", Configuration),
        ("account_imperial_arena_days", Days),
        ("account_imperial_arena_runs", Runs),
        ("account_imperial_arena_rewards", Rewards),
        ("account_imperial_arena_challenge_claims", Challenges)
    ];

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (var (table, definition) in Tables)
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table";
            command.Parameters.AddWithValue("$table", table);
            var actual = command.ExecuteScalar();
            if (actual is null) SqliteMigrations.Execute(db, tx, definition);
            else if (actual is not string sql || SqliteMigrations.NormalizeTable(sql, table) != SqliteMigrations.NormalizeTable(definition, table))
                throw new InvalidDataException("帝国竞技场持久结构不兼容: " + table);
        }
    }
}
