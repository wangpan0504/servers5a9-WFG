namespace DfoServer.Persistence;

internal static class GuildRankingSchema
{
    public const string Parties = """
        CREATE TABLE IF NOT EXISTS guild_ranking_party_clears(
          guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
          instance_id TEXT NOT NULL CHECK(length(instance_id)=32),dungeon_id INTEGER NOT NULL CHECK(dungeon_id>0),
          members INTEGER NOT NULL CHECK(members BETWEEN 2 AND 4),created_at INTEGER NOT NULL CHECK(created_at>0),
          PRIMARY KEY(guild_id,instance_id)
        );
        """;
    public const string Archives = """
        CREATE TABLE IF NOT EXISTS guild_ranking_months(
          month INTEGER PRIMARY KEY CHECK(month BETWEEN 190001 AND 999912),
          observed_at INTEGER NOT NULL CHECK(observed_at>0),rule_version TEXT NOT NULL,
          entries TEXT NOT NULL CHECK(json_valid(entries) AND json_type(entries)='array')
        );
        """;
    public const string Definition = Parties + Archives + """
        CREATE INDEX IF NOT EXISTS guild_ranking_party_period ON guild_ranking_party_clears(created_at,guild_id);
        """;
}
