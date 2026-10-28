namespace DfoServer.Persistence;

internal static class GuildHongbaoSchema
{
    public const string Records = """
        CREATE TABLE IF NOT EXISTS guild_hongbao_records(
          record_id INTEGER PRIMARY KEY AUTOINCREMENT CHECK(record_id BETWEEN 1 AND 4294967295),
          guild_id INTEGER NOT NULL CHECK(guild_id>0),sender_account TEXT NOT NULL,
          sender_name TEXT NOT NULL,item_id INTEGER NOT NULL CHECK(item_id>0),
          total INTEGER NOT NULL CHECK(total>0),remaining INTEGER NOT NULL CHECK(remaining BETWEEN 0 AND total),
          portions INTEGER NOT NULL CHECK(portions BETWEEN 5 AND 30),taken INTEGER NOT NULL CHECK(taken BETWEEN 0 AND portions),
          created_at INTEGER NOT NULL CHECK(created_at>0),expires_at INTEGER NOT NULL CHECK(expires_at>created_at),
          refunded INTEGER NOT NULL DEFAULT 0 CHECK(refunded IN(0,1))
        );
        """;
    public const string Claims = """
        CREATE TABLE IF NOT EXISTS guild_hongbao_claims(
          record_id INTEGER NOT NULL REFERENCES guild_hongbao_records(record_id),account TEXT NOT NULL,
          character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),name TEXT NOT NULL,
          level INTEGER NOT NULL CHECK(level BETWEEN 1 AND 255),amount INTEGER NOT NULL CHECK(amount>0),
          ordinal INTEGER NOT NULL CHECK(ordinal BETWEEN 0 AND 29),day_start_unix INTEGER NOT NULL,
          created_at INTEGER NOT NULL CHECK(created_at>0),PRIMARY KEY(record_id,account),UNIQUE(record_id,ordinal)
        );
        """;
    public const string Definition = Records + Claims + """
        CREATE INDEX IF NOT EXISTS guild_hongbao_list ON guild_hongbao_records(guild_id,expires_at,record_id);
        CREATE INDEX IF NOT EXISTS guild_hongbao_refund ON guild_hongbao_records(sender_account,refunded,expires_at);
        CREATE INDEX IF NOT EXISTS guild_hongbao_daily ON guild_hongbao_claims(account,day_start_unix);
        """;
}
