namespace DfoServer.Persistence;

internal static class GuildDungeonRewardSchema
{
    public const string Table = """
        CREATE TABLE IF NOT EXISTS guild_dungeon_reward_receipts(
          character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),account TEXT NOT NULL,
          guild_id INTEGER NOT NULL CHECK(guild_id>0),instance_id TEXT NOT NULL CHECK(length(instance_id)=32),
          event_id TEXT NOT NULL CHECK(length(event_id)=32),dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),
          reward_json TEXT NOT NULL CHECK(json_valid(reward_json) AND json_type(reward_json)='object'),
          mail_id INTEGER NOT NULL CHECK(mail_id>0),PRIMARY KEY(character_id,instance_id)
        );
        """;
    public const string Definition = Table + """
        CREATE INDEX IF NOT EXISTS guild_dungeon_reward_instance ON guild_dungeon_reward_receipts(guild_id,instance_id);
        """;
}
