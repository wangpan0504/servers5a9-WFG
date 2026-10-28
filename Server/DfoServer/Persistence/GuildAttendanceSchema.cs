namespace DfoServer.Persistence;

internal static class GuildAttendanceSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_attendance(
          character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
          guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
          joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
          business_day_unix INTEGER NOT NULL,
          observed_at_unix INTEGER NOT NULL CHECK(observed_at_unix>0 AND observed_at_unix>=joined_at_unix),
          PRIMARY KEY(guild_id,business_day_unix,character_id,joined_at_unix)
        );
        """;
}
