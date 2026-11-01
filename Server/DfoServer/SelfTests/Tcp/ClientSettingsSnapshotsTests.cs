using System.Text.Json;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ClientSettingsSnapshotsTests
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-settings-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "fixture.db");
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            db.Open();
            void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            Sql("""
                CREATE TABLE assets(id INTEGER PRIMARY KEY, quantity INTEGER, real_value REAL, name TEXT, payload BLOB, optional TEXT);
                INSERT INTO assets VALUES(9,9223372036854775807,1.25,'中文',x'00FF',NULL),(2,-7,0.0,'',x'', 'x');
                CREATE TABLE empty_asset(id INTEGER PRIMARY KEY);
                CREATE TABLE account_client_settings(account TEXT, payload BLOB);
                INSERT INTO account_client_settings VALUES('z',x'02'),('a',x'01');
                CREATE TABLE character_client_settings(character_id INTEGER, payload BLOB);
                CREATE TABLE character_game_settings(character_id INTEGER, payload BLOB);
                CREATE TABLE account_hotkey_settings(account TEXT,profile INTEGER,payload BLOB);
                INSERT INTO account_hotkey_settings VALUES('a',1,x'FF'),('a',0,x'00');
                CREATE TABLE account_quick_chat(account TEXT,bank INTEGER,payload BLOB);
                CREATE TABLE account_chat_emoticons(account TEXT,emoticon_id INTEGER,macro BLOB);
                INSERT INTO account_chat_emoticons VALUES('a',1,x'6869');
                CREATE TABLE account_recent_emoticons(account TEXT,entries BLOB);
                INSERT INTO account_recent_emoticons VALUES('a',zeroblob(72));
                CREATE TABLE character_quest_notify(character_id INTEGER,account TEXT,entries BLOB);
                INSERT INTO character_quest_notify VALUES(7,'a',x'02000000010000000000000000000000');
                """);
            string LegacyAssets() => JsonSerializer.Serialize(TcpTestFixture.Rows(database,
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN ('account_client_settings','character_client_settings','character_game_settings','account_hotkey_settings','account_quick_chat','account_chat_emoticons','account_recent_emoticons','character_quest_notify') ORDER BY name")
                .Select(row => TcpTestFixture.Rows(database, $"SELECT * FROM [{row[0]}] ORDER BY rowid")));
            string expectedSettings = string.Concat(new[]
            {
                "SELECT * FROM account_client_settings ORDER BY account", "SELECT * FROM character_client_settings ORDER BY character_id",
                "SELECT * FROM character_game_settings ORDER BY character_id", "SELECT * FROM account_hotkey_settings ORDER BY account,profile",
                "SELECT * FROM account_quick_chat ORDER BY account,bank",
                "SELECT * FROM account_chat_emoticons ORDER BY account,emoticon_id",
                "SELECT * FROM account_recent_emoticons ORDER BY account",
                "SELECT * FROM character_quest_notify ORDER BY character_id"
            }.Select(sql => JsonSerializer.Serialize(TcpTestFixture.Rows(database, sql))));
            That(ClientSettingsSnapshots.Capture(database, true) == LegacyAssets(), "单连接全资产快照与旧逐表路径逐字一致：排序、整数、real、文本、NULL、blob和空表");
            That(ClientSettingsSnapshots.Capture(database, false) == expectedSettings, "设置八表顺序、聊天指令、最近列表及任务书签与逐表串接JSON一致");
            string before = ClientSettingsSnapshots.Capture(database, true);
            Sql("CREATE TABLE new_asset(id INTEGER PRIMARY KEY); INSERT INTO new_asset VALUES(1); UPDATE assets SET quantity=42 WHERE id=2;");
            That(ClientSettingsSnapshots.Capture(database, true) == LegacyAssets() && ClientSettingsSnapshots.Capture(database, true) != before,
                "下一快照重新读schema和资产，不缓存前次表名或值");
            Sql("DROP TABLE account_quick_chat;");
            try { ClientSettingsSnapshots.Capture(database, false); throw new InvalidOperationException("expected missing table"); }
            catch (SqliteException) { That(true, "快照SQL错误不被吞成空结果"); }
            Sql("CREATE TABLE account_quick_chat(account TEXT,bank INTEGER,payload BLOB);");
            That(ClientSettingsSnapshots.Capture(database, false) == expectedSettings, "失败后释放连接事务，后续独立快照仍可读取");
            var timings = new ClientSettingsTimings();
            var sample = timings.Measure("snapshot"); sample.Dispose(); sample.Dispose();
            using (timings.Measure("parent"))
            {
                using (timings.Measure("child")) { }
                using (timings.Measure("child")) { }
            }
            timings.Write(root, completed: false);
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "settings.phases.json")));
            That(!report.RootElement.GetProperty("Completed").GetBoolean() && report.RootElement.GetProperty("Stages")[0].GetProperty("Calls").GetInt32() == 1,
                "失败计时不冒成功，scope重复Dispose不累计两次");
            var stages = report.RootElement.GetProperty("Stages").EnumerateArray().ToArray();
            var parent = stages.Single(stage => stage.GetProperty("Stage").GetString() == "parent");
            var child = stages.Single(stage => stage.GetProperty("Stage").GetString() == "child");
            double childTotal = child.GetProperty("ElapsedMs").GetDouble();
            That(child.GetProperty("Calls").GetInt32() == 2 && child.GetProperty("MinMs").GetDouble() >= 0 &&
                child.GetProperty("MinMs").GetDouble() <= child.GetProperty("MaxMs").GetDouble() &&
                child.GetProperty("MaxMs").GetDouble() <= childTotal &&
                parent.GetProperty("ElapsedMs").GetDouble() >= childTotal,
                "子阶段按调用累计min/max，父阶段包含子阶段而非独占时间，不按样本数量保存无界明细");
        }
        finally { if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, true); }
    }
}
