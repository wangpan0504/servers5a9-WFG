namespace DfoServer.Persistence;

internal static class EvildomWarRewardSchema
{
    public const string Table = "character_evildom_war_rewards";
    public const string Definition = """
        CREATE TABLE IF NOT EXISTS character_evildom_war_rewards(
          character_id INTEGER NOT NULL,account TEXT NOT NULL,battle_id TEXT NOT NULL CHECK(length(battle_id)=32),
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32),task_attempt INTEGER NOT NULL CHECK(task_attempt>0),
          dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),mode INTEGER NOT NULL CHECK(mode BETWEEN 0 AND 2),
          created_at INTEGER NOT NULL CHECK(created_at>=0),reward_json TEXT NOT NULL CHECK(json_valid(reward_json)),
          mail_ids TEXT NOT NULL CHECK(json_valid(mail_ids)),delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN(0,1)),
          PRIMARY KEY(character_id,battle_id),UNIQUE(character_id,instance_id),
          FOREIGN KEY(character_id,account) REFERENCES characters(id,account));
        """;
}
