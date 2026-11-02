namespace DfoServer.Persistence;

internal static class Strongway2019CompletionSchema
{
    internal const string Table = "character_strongway2019_completions";
    // state=0已预留待交付，1已交付，2冻结时额度已满。旧奖励表及其唯一约束原样保留。
    internal const string Definition = """
        CREATE TABLE character_strongway2019_completions(
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
          state INTEGER NOT NULL CHECK(typeof(state)='integer' AND state IN(0,1,2)),
          mail_id INTEGER CHECK(mail_id IS NULL OR (typeof(mail_id)='integer' AND mail_id>0)),
          PRIMARY KEY(account,instance_id),
          CHECK((state=1 AND mail_id IS NOT NULL) OR (state<>1 AND mail_id IS NULL)),
          CHECK((bucket=0 AND dungeon_id IN(5359,5363)) OR (bucket=1 AND dungeon_id=5360))
        );
        """;
    internal const string Index = "CREATE INDEX character_strongway2019_completions_pending ON character_strongway2019_completions(account,state,character_id,created_at);";
}
