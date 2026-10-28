namespace DfoServer.Persistence;

internal static class GuildHongbaoPurchaseSchema
{
    internal const string Definition = """
        CREATE TABLE IF NOT EXISTS character_guild_hongbao_purchases(
          character_id INTEGER NOT NULL REFERENCES characters(id) ON DELETE CASCADE,
          day_start_unix INTEGER NOT NULL CHECK(day_start_unix>=0),
          cera INTEGER NOT NULL CHECK(cera BETWEEN 1 AND 2147483647),
          PRIMARY KEY(character_id,day_start_unix)
        );
        """;
}
