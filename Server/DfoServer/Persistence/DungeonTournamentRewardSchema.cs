namespace DfoServer.Persistence;

internal static class DungeonTournamentRewardSchema
{
    internal const string Definition = """
        CREATE TABLE character_dungeon_tournament_rewards(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,instance_id TEXT NOT NULL,
          plan_json TEXT NOT NULL CHECK(json_valid(plan_json) AND json_type(plan_json)='object'),
          first_choice INTEGER NOT NULL CHECK(first_choice BETWEEN 0 AND 1),
          second_choice INTEGER NOT NULL CHECK(second_choice BETWEEN 0 AND 1),
          rewards_json TEXT NOT NULL CHECK(json_valid(rewards_json) AND json_type(rewards_json)='array' AND json_array_length(rewards_json)=2),
          mail_id INTEGER,revision INTEGER NOT NULL CHECK(revision>=0),
          PRIMARY KEY(character_id,instance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account),
          FOREIGN KEY(character_id,instance_id) REFERENCES character_dungeon_settlement_receipts(character_id,instance_id),
          FOREIGN KEY(mail_id) REFERENCES mailbox_messages(mail_id));
        """;
}
