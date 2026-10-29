namespace DfoServer.Persistence;

internal static class GuildNpcBenefitSchema
{
    internal const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_npc_common_benefits(
          guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
          effect TEXT NOT NULL,benefit_id INTEGER NOT NULL CHECK(benefit_id>0),
          remaining INTEGER NOT NULL CHECK(remaining BETWEEN 0 AND 32767),
          deadline INTEGER NOT NULL CHECK(deadline BETWEEN 1 AND 2147483647),
          PRIMARY KEY(guild_id,effect)
        );
        CREATE TABLE IF NOT EXISTS character_guild_npc_benefits(
          character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
          guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
          joined_at INTEGER NOT NULL CHECK(joined_at>=0),
          effect TEXT NOT NULL,benefit_id INTEGER NOT NULL CHECK(benefit_id>0),
          remaining INTEGER NOT NULL CHECK(remaining BETWEEN 0 AND 32767),
          deadline INTEGER NOT NULL CHECK(deadline BETWEEN 0 AND 2147483647),
          selected INTEGER NOT NULL DEFAULT 0 CHECK(selected IN(0,1)),
          PRIMARY KEY(character_id,guild_id,joined_at,benefit_id)
        );
        CREATE TABLE IF NOT EXISTS guild_npc_training_receipts(
          request_id TEXT PRIMARY KEY REFERENCES guild_npc_receipts(request_id),
          selection TEXT NOT NULL CHECK(json_valid(selection) AND json_type(selection)='array')
        );
        """;
}
