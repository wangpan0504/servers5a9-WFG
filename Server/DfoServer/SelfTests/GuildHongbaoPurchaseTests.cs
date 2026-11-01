using DfoServer.GameWorld.Guilds;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildHongbaoPurchaseTests
{
    public static void RunCore()
    {
        int before = Count;
        const string source = """
            [hongbao]
            101 201 500 95
            [/hongbao]
            [min guild member count]
            30
            [max total cera per day]
            50000
            [max take count per day]
            5
            """;
        var catalog = new GuildHongbaoCatalog(source);
        That(catalog.MinimumMembers == 30 && catalog.DailyCeraLimit == 50000 && catalog.ByCommodity[201] == new GuildHongbaoDefinition(101, 201, 500, 95),
            "红包购买参数按资源读取，第四值只保存不解释为折扣");
        foreach (string invalid in new[] { source.Replace("30", "0"), source.Replace("50000", "-1"),
            source.Replace("[/hongbao]", "102 201 500 90\n[/hongbao]"), source.Replace("[/hongbao]", "101 202 500 90\n[/hongbao]") })
            Reject<InvalidDataException>(() => new GuildHongbaoCatalog(invalid), "拒绝非法购买限制、重复商品和模板");
        string root = Path.Combine(Path.GetTempPath(), "guild-hongbao-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            _ = new CharacterRepository(path);
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            void Sql(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            long Number(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
            That(Number("SELECT count(*) FROM character_guild_hongbao_purchases") == 0, "新库红包额度为空，不回填未知购买历史");
            Sql("DROP TABLE character_guild_hongbao_purchases; PRAGMA user_version=136");
            _ = new CharacterRepository(path);
            That(Number("PRAGMA user_version") == SqliteMigrations.CurrentVersion, "136库升级到角色购买额度结构");
            Sql("CREATE TRIGGER hongbao_reopen_probe BEFORE INSERT ON character_guild_hongbao_purchases BEGIN SELECT RAISE(ABORT,'probe'); END");
            _ = new CharacterRepository(path);
            That(Number("SELECT count(*) FROM sqlite_schema WHERE name='hongbao_reopen_probe'") == 1, "重开不覆盖额度表或其业务触发器");
            Sql($"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
            Reject<InvalidDataException>(() => new CharacterRepository(path), "拒绝未来版本红包数据库");
            Sql("PRAGMA user_version=136; DROP TABLE character_guild_hongbao_purchases; CREATE TABLE character_guild_hongbao_purchases(character_id INTEGER)");
            Reject<InvalidDataException>(() => new CharacterRepository(path), "同名未知额度表拒绝升级");
            That(Number("PRAGMA user_version") == 136 && Number("SELECT count(*) FROM pragma_table_info('character_guild_hongbao_purchases')") == 1,
                "未知表升级失败保留原版本及结构");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS guild hongbao purchase catalog/schema assertions {Count - before}");
    }
}
