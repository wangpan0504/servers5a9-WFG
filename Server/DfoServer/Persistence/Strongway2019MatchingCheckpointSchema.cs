namespace DfoServer.Persistence;

internal static class Strongway2019MatchingCheckpointSchema
{
    internal const string Table = "character_strongway2019_match_facts";
    internal const string Definition = """
        CREATE TABLE character_strongway2019_match_facts(
          account TEXT NOT NULL,
          character_id INTEGER NOT NULL CHECK(typeof(character_id)='integer' AND character_id BETWEEN 1 AND 65535),
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          competition_id TEXT NOT NULL CHECK(length(competition_id)=32),
          bucket INTEGER NOT NULL CHECK(bucket=1),
          day INTEGER NOT NULL CHECK(typeof(day)='integer' AND day>0),
          week INTEGER NOT NULL CHECK(typeof(week)='integer' AND week>0),
          created_at INTEGER NOT NULL CHECK(typeof(created_at)='integer' AND created_at>=0),
          state INTEGER NOT NULL CHECK(typeof(state)='integer' AND state IN(0,2)),
          snapshot_json TEXT NOT NULL CHECK(json_valid(snapshot_json) AND json_type(snapshot_json)='object'),
          PRIMARY KEY(account,instance_id)
        );
        """;
    internal const string Index = "CREATE INDEX character_strongway2019_match_facts_owner ON character_strongway2019_match_facts(account,character_id,created_at);";
}
