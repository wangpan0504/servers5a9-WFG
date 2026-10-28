namespace DfoServer.Persistence;

internal static class EvildomWarGuideSchema
{
    // 删除角色不返还本周绑定名额；角色ID为单调自增，不随槽位或名字复用。
    public const string Table = """
        CREATE TABLE IF NOT EXISTS account_evildom_war_guides(
          account TEXT NOT NULL,slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 1),
          week INTEGER NOT NULL CHECK(week>0),character_id INTEGER NOT NULL CHECK(character_id BETWEEN 1 AND 65535),
          PRIMARY KEY(account,slot),UNIQUE(account,character_id),
          FOREIGN KEY(account) REFERENCES accounts(account_id));
        """;
}
