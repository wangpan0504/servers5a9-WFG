namespace DfoServer.Persistence;

internal static class EvildomWarHiddenRewardSchema
{
    public const string Table = "character_evildom_war_hidden_rewards";
    public const string Definition = """
        CREATE TABLE IF NOT EXISTS character_evildom_war_hidden_rewards(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,battle_id TEXT NOT NULL CHECK(length(battle_id)=32),
          reward_json TEXT NOT NULL CHECK(json_valid(reward_json)),mail_ids TEXT NOT NULL CHECK(json_valid(mail_ids)),
          PRIMARY KEY(character_id,battle_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
}
