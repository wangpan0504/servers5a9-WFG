namespace DfoServer.Persistence;

internal static class GuildManagementSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_settings(
          guild_id INTEGER PRIMARY KEY REFERENCES guilds(guild_id) ON DELETE CASCADE,
          revision INTEGER NOT NULL DEFAULT 0 CHECK(revision>=0),
          promotion TEXT NOT NULL DEFAULT '',
          tags INTEGER NOT NULL DEFAULT 0 CHECK(tags>=0 AND tags<=4294967295),
          join_mode INTEGER NOT NULL DEFAULT 0 CHECK(join_mode IN(0,1)),
          webpage INTEGER NOT NULL DEFAULT 0 CHECK(webpage IN(0,1))
        );
        CREATE TABLE IF NOT EXISTS guild_roles(
          guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
          grade INTEGER NOT NULL CHECK(grade BETWEEN 1 AND 5),
          permissions INTEGER NOT NULL CHECK(permissions BETWEEN 0 AND 524287),
          name TEXT NOT NULL,
          PRIMARY KEY(guild_id,grade)
        );
        CREATE TABLE IF NOT EXISTS guild_membership_cooldowns(
          character_id INTEGER PRIMARY KEY REFERENCES characters(id) ON DELETE CASCADE,
          until_unix INTEGER NOT NULL CHECK(until_unix>=0)
        );
        """;
}
