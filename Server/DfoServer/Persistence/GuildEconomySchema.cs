namespace DfoServer.Persistence;

internal static class GuildEconomySchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_economy(
            guild_id INTEGER PRIMARY KEY REFERENCES guilds(guild_id) ON DELETE CASCADE,
            member_limit INTEGER NOT NULL DEFAULT 300 CHECK(member_limit IN (300,500,700,1000))
        );
        CREATE TABLE IF NOT EXISTS guild_benefits(
            guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
            type INTEGER NOT NULL CHECK(type IN (1,3,5,7)),
            value INTEGER NOT NULL CHECK(value BETWEEN 0 AND 4294967295),
            expires_unix INTEGER NOT NULL CHECK(expires_unix BETWEEN 0 AND 2147483647),
            PRIMARY KEY(guild_id,type)
        );
        CREATE TABLE IF NOT EXISTS guild_economy_receipts(
            request_id TEXT PRIMARY KEY CHECK(length(request_id)=64),
            account TEXT NOT NULL,
            character_id INTEGER NOT NULL,
            guild_id INTEGER NOT NULL,
            operation INTEGER NOT NULL CHECK(operation IN (1,2)),
            content_id INTEGER NOT NULL,
            source_uid INTEGER NOT NULL DEFAULT 0,
            source_slot INTEGER NOT NULL DEFAULT 0,
            amount INTEGER NOT NULL CHECK(amount>0),
            created_at_unix INTEGER NOT NULL CHECK(created_at_unix BETWEEN 1 AND 2147483647)
        );
        CREATE TABLE IF NOT EXISTS guild_mileage_history(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            guild_id INTEGER NOT NULL REFERENCES guilds(guild_id) ON DELETE CASCADE,
            character_id INTEGER NOT NULL,
            character_name TEXT NOT NULL,
            kind INTEGER NOT NULL CHECK(kind IN (3,5,8)),
            content_id INTEGER NOT NULL,
            amount INTEGER NOT NULL CHECK(amount BETWEEN 1 AND 2147483647),
            created_at_unix INTEGER NOT NULL CHECK(created_at_unix BETWEEN 1 AND 2147483647)
        );
        CREATE INDEX IF NOT EXISTS guild_mileage_history_recent ON guild_mileage_history(guild_id,id DESC);
        CREATE TABLE IF NOT EXISTS guild_economy_events(
            event_id TEXT PRIMARY KEY,
            character_id INTEGER NOT NULL,
            guild_id INTEGER NOT NULL,
            amount INTEGER NOT NULL CHECK(amount BETWEEN 0 AND 2147483647)
        );
        """;
}
