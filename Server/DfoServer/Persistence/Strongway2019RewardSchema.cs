namespace DfoServer.Persistence;

internal static class Strongway2019RewardSchema
{
    internal const string Table = "account_strongway2019_rewards";
    // 奖励回执永久保留；删除角色或邮件不能返还账号日周额度。
    internal const string Definition = """
        CREATE TABLE account_strongway2019_rewards(
          account TEXT NOT NULL,
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          character_id INTEGER NOT NULL CHECK(typeof(character_id)='integer' AND character_id BETWEEN 1 AND 65535),
          task_attempt INTEGER NOT NULL CHECK(typeof(task_attempt)='integer' AND task_attempt>0),
          run_epoch INTEGER NOT NULL CHECK(typeof(run_epoch)='integer' AND run_epoch>=0),
          dungeon_id INTEGER NOT NULL CHECK(typeof(dungeon_id)='integer' AND dungeon_id IN(5359,5363,5360)),
          bucket INTEGER NOT NULL CHECK(typeof(bucket)='integer' AND bucket IN(0,1)),
          day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day>0),
          week INTEGER NOT NULL CHECK(typeof(week)='integer' AND week>0),
          created_at INTEGER NOT NULL CHECK(typeof(created_at)='integer' AND created_at>=0),
          reward_json TEXT NOT NULL CHECK(json_valid(reward_json) AND json_type(reward_json)='object'),
          fact_json TEXT NOT NULL CHECK(json_valid(fact_json) AND json_type(fact_json)='object'),
          mail_id INTEGER NOT NULL CHECK(typeof(mail_id)='integer' AND mail_id>0),
          PRIMARY KEY(account,instance_id),
          UNIQUE(account,bucket,day),
          CHECK((bucket=0 AND dungeon_id IN(5359,5363)) OR (bucket=1 AND dungeon_id=5360))
        );
        """;
    internal const string WeekIndex = "CREATE INDEX account_strongway2019_rewards_week ON account_strongway2019_rewards(account,bucket,week);";
}
