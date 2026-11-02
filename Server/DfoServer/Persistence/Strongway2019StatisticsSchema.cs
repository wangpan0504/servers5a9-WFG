namespace DfoServer.Persistence;

internal static class Strongway2019StatisticsSchema
{
    internal const string Table = "character_strongway2019_statistics";
    // 每次原Run只记录一次；统计不引用奖励额度或邮件领取状态。
    internal const string Definition = """
        CREATE TABLE character_strongway2019_statistics(
          account TEXT NOT NULL,
          character_id INTEGER NOT NULL CHECK(typeof(character_id)='integer' AND character_id BETWEEN 1 AND 65535),
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          task_attempt INTEGER NOT NULL CHECK(typeof(task_attempt)='integer' AND task_attempt>0),
          run_epoch INTEGER NOT NULL CHECK(typeof(run_epoch)='integer' AND run_epoch>=0),
          dungeon_id INTEGER NOT NULL CHECK(typeof(dungeon_id)='integer' AND dungeon_id IN(5359,5363,5360)),
          mode INTEGER NOT NULL CHECK(typeof(mode)='integer' AND mode IN(0,2,3)),
          elapsed_ms INTEGER CHECK(elapsed_ms IS NULL OR (typeof(elapsed_ms)='integer' AND elapsed_ms BETWEEN 0 AND 4294967295)),
          fact_json TEXT NOT NULL CHECK(json_valid(fact_json) AND json_type(fact_json)='object'),
          PRIMARY KEY(character_id,instance_id),
          CHECK((dungeon_id=5359 AND mode=0 AND elapsed_ms IS NOT NULL) OR
            (dungeon_id=5363 AND mode=2 AND elapsed_ms IS NOT NULL) OR (dungeon_id=5360 AND mode=3))
        );
        """;
}
