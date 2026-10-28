namespace DfoServer.Persistence;

internal static class GuildApplicationsSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_applications(
          guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
          character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
          message TEXT NOT NULL DEFAULT '',
          region INTEGER NOT NULL CHECK(region BETWEEN 0 AND 249),
          created_at_unix INTEGER NOT NULL CHECK(created_at_unix>=0),
          PRIMARY KEY(guild_id,character_id)
        );
        CREATE INDEX IF NOT EXISTS guild_applications_character ON guild_applications(character_id,guild_id);
        """;
}
