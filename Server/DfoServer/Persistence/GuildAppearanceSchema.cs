namespace DfoServer.Persistence;

internal static class GuildAppearanceSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_appearance(
          guild_id INTEGER PRIMARY KEY REFERENCES guilds(guild_id) ON DELETE CASCADE,
          mark INTEGER NOT NULL DEFAULT 0 CHECK(mark BETWEEN 0 AND 4294967295),
          aura_mark INTEGER NOT NULL DEFAULT 4294967295 CHECK(aura_mark BETWEEN 0 AND 4294967295),
          aura_frame INTEGER NOT NULL DEFAULT 4294967295 CHECK(aura_frame BETWEEN 0 AND 4294967295),
          CHECK((aura_mark=4294967295)=(aura_frame=4294967295))
        );
        """;
}
