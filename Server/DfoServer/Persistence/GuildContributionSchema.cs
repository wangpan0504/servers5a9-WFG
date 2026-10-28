namespace DfoServer.Persistence;

internal static class GuildContributionSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_contribution_events(
          event_id TEXT PRIMARY KEY,
          character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
          guild_id INTEGER REFERENCES guilds(guild_id) ON DELETE SET NULL,
          joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
          source INTEGER NOT NULL CHECK(source BETWEEN 1 AND 7),
          business_day_unix INTEGER NOT NULL,
          week_start_unix INTEGER NOT NULL,
          points INTEGER NOT NULL CHECK(points>=0),
          created_at_unix INTEGER NOT NULL CHECK(created_at_unix>0)
        );
        CREATE INDEX IF NOT EXISTS ix_guild_contribution_week ON guild_contribution_events(guild_id,character_id,joined_at_unix,week_start_unix);
        CREATE INDEX IF NOT EXISTS ix_guild_contribution_daily_cap ON guild_contribution_events(character_id,business_day_unix,source);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_guild_contribution_daily ON guild_contribution_events(character_id,business_day_unix,source) WHERE source IN (1,2);
        CREATE TABLE IF NOT EXISTS guild_contribution_settlements(
          guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
          character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
          joined_at_unix INTEGER NOT NULL CHECK(joined_at_unix>=0),
          week_start_unix INTEGER NOT NULL,
          points INTEGER NOT NULL CHECK(points>0),
          coins INTEGER NOT NULL CHECK(coins>0),
          settled_at_unix INTEGER NOT NULL CHECK(settled_at_unix>0),
          PRIMARY KEY(guild_id,character_id,joined_at_unix,week_start_unix)
        );
        """;
}
