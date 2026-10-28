namespace DfoServer.Persistence;

internal static class GuildDungeonProgressSchema
{
    internal const string Definition = """
        CREATE TABLE IF NOT EXISTS guild_dungeon_progress(
          guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
          dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 1 AND 4294967295),
          clear_count INTEGER NOT NULL CHECK(clear_count BETWEEN 0 AND 4294967295),
          last_clear_unix INTEGER NOT NULL CHECK(last_clear_unix>0),
          PRIMARY KEY(guild_id,dungeon_id)
        );
        CREATE TABLE IF NOT EXISTS guild_dungeon_clear_receipts(
          guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32 AND instance_id NOT GLOB '*[^0-9a-f]*'),
          dungeon_id INTEGER NOT NULL CHECK(dungeon_id BETWEEN 1 AND 4294967295),
          character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
          joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
          committed_at_unix INTEGER NOT NULL CHECK(committed_at_unix>0),
          PRIMARY KEY(guild_id,instance_id)
        );
        """;
}
