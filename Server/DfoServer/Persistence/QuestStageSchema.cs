namespace DfoServer.Persistence;

internal static class QuestStageSchema
{
    internal const string WitnessSql = """
        CREATE TABLE character_quest_stage_witnesses(
          character_id INTEGER NOT NULL,
          account TEXT NOT NULL,
          source_acceptance_id TEXT NOT NULL CHECK(length(source_acceptance_id)=32 AND source_acceptance_id NOT GLOB '*[^0-9a-f]*'),
          source_quest_id INTEGER NOT NULL CHECK(source_quest_id BETWEEN 1 AND 29999),
          source_revision INTEGER NOT NULL CHECK(source_revision>0),
          target_level INTEGER NOT NULL CHECK(target_level BETWEEN 1 AND 95),
          rule_version INTEGER NOT NULL CHECK(rule_version>0),
          status TEXT NOT NULL CHECK(status IN('Deferred','Granted','Satisfied','Invalidated')),
          proof TEXT NOT NULL CHECK(json_valid(proof) AND json_type(proof)='object'),
          PRIMARY KEY(character_id,source_acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
          FOREIGN KEY(character_id,source_acceptance_id) REFERENCES character_quest_completion_receipts(character_id,acceptance_id)
        ) STRICT;
        """;
    internal const string ClaimSql = """
        CREATE TABLE character_quest_stage_claims(
          character_id INTEGER NOT NULL,
          account TEXT NOT NULL,
          source_acceptance_id TEXT NOT NULL,
          trigger_acceptance_id TEXT NOT NULL,
          target_level INTEGER NOT NULL CHECK(target_level BETWEEN 1 AND 95),
          before_experience INTEGER NOT NULL CHECK(before_experience>=0),
          after_experience INTEGER NOT NULL CHECK(after_experience>=before_experience),
          amount INTEGER NOT NULL CHECK(amount BETWEEN 0 AND 2147483647 AND after_experience-before_experience=amount),
          revision INTEGER NOT NULL CHECK(revision>0),
          UNIQUE(character_id,target_level),
          UNIQUE(character_id,source_acceptance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
          FOREIGN KEY(character_id,source_acceptance_id) REFERENCES character_quest_stage_witnesses(character_id,source_acceptance_id),
          FOREIGN KEY(character_id,trigger_acceptance_id) REFERENCES character_quest_completion_receipts(character_id,acceptance_id)
        ) STRICT;
        """;
}
