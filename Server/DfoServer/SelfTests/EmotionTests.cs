using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EmotionTests
{
    public static void RunCore()
    {
        int start = Count;
        EmotionSelectionTests.RunCore();
        Dictionary<int, string> strings = new() { [1] = "[EMOTION INFO]", [2] = "[/EMOTION INFO]", [3] = "", [4] = "name", [5] = "-name", [6] = "$END" };
        PvfToken[] row = [new(0, 300), new(0, 1), new(0, 1), new(6, 3), new(6, 3), new(0, 0), new(0, 0), new(0, 1), new(6, 4), new(6, 5), new(6, 5), new(6, 6)];
        PvfToken[] source = [new(3, 1), .. row, new(3, 2)];
        var definitions = EmotionExpressionFile.Read(source, id => strings[id]);
        That(definitions is [{ Id: 300, Kind: 1, Visible: true }], "当前心情格式保留第三列与三项布尔，允许多条快捷指令及u16编号");
        Reject<InvalidDataException>(() => EmotionExpressionFile.Read([new(3, 1), .. row[..^1], new(3, 2)], id => strings[id]), "缺失END不能截断注册心情");
        Reject<InvalidDataException>(() => EmotionExpressionFile.Read([new(3, 1), .. row, .. row, new(3, 2)], id => strings[id]), "重复心情编号拒绝");
        var catalog = new CharacterEmotionCatalog([new(0, 0, true), .. definitions, new(9, 3, true), new(301, 1, false)]);
        That(catalog.CanSelect(0) && catalog.CanSelect(300) && !catalog.CanSelect(9) && !catalog.CanSelect(301) && !catalog.CanSelect(65535),
            "只开放已注册且可选的基础心情，不开放脚本锁定或隐藏资源");
        Bytes(SettingsPackets.Emotion(300), [1, 44, 1], "ACK254成功为flag和完整u16");
        Bytes(SettingsPackets.Emotion(3, success: false), [0, 3, 0], "ACK254失败使用本版u16错误码");
        var character = new Character(1, 0, "Emotion", 0, new(38, 1, 450, 234), null);
        byte[] baseline = TownPackets.Actor(character, 0), actor = TownPackets.Actor(character with { Emotion = 300 }, 0);
        Bytes(actor.AsSpan(119, 2), [44, 1], "Actor116B状态末尾承载心情，不使用A4的包尾偏移");
        actor[119] = actor[120] = 0;
        Bytes(actor, baseline, "心情投影只改变两个已确认字节，其余资产和身份字段不变");

        string root = Path.Combine(Path.GetTempPath(), "S5A9-emotion-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "game.db");
        try
        {
            var repo = new CharacterRepository(path);
            var auth = new DfoServer.Game.Auth.AuthenticationService(new AuthenticationRepository(path));
            auth.Create("mood-a", NativeLoginWire.Digest);
            auth.Create("mood-b", NativeLoginWire.Digest);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("mood-a", () => { });
            using var other = owners.Acquire("mood-b", () => { });
            Character first = repo.Create(owner, new("MoodA", character.Position, null));
            Character second = repo.Create(owner, new("MoodB", character.Position, null));
            var store = new ClientSettingsRepository(path);
            That(store.SaveEmotion(owner, first.Id, 300), "角色心情实际变化提交");
            That(JsonSerializer.Serialize(repo.LoadCharacter(owner, 0)) == JsonSerializer.Serialize(first with { Emotion = 300 }) &&
                JsonSerializer.Serialize(repo.LoadCharacter(owner, 1)) == JsonSerializer.Serialize(second), "保存仅修改本人当前角色心情，不改变revision或其他资产及角色");
            Reject<InvalidDataException>(() => store.SaveEmotion(other, first.Id, 1), "跨账号心情保存拒绝");
            Execute("CREATE TRIGGER fail_mood BEFORE UPDATE OF emotion ON characters BEGIN SELECT RAISE(ABORT,'mood'); END");
            That(!store.SaveEmotion(owner, first.Id, 300), "同值不申请writer，保留零变化操作");
            using (var external = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                external.Open(); using var writer = external.BeginTransaction(deferred: false);
                That(!store.SaveEmotion(owner, first.Id, 300), "外部writer占用时同值心情仍只读，不等待写锁");
                writer.Rollback();
            }
            Reject<SqliteException>(() => store.SaveEmotion(owner, first.Id, 0), "SQL失败不伪造保存成功");
            That(repo.LoadCharacter(owner, 0).Emotion == 300, "失败回滚后仍为原心情");
            Execute("DROP TRIGGER fail_mood");
            That(new CharacterRepository(path).LoadCharacter(owner, 0).Emotion == 300, "重开数据库读取完整u16心情");
            using var replacement = owners.Acquire("mood-a", () => { });
            Reject<InvalidOperationException>(() => store.SaveEmotion(owner, first.Id, 0), "过期generation不能保存心情");
            DatabaseSchemaTests.RemoveAdventureSchema(path);
            DatabaseSchemaTests.RewindRaidLifecycle(path);
            Execute("ALTER TABLE characters DROP COLUMN emotion; PRAGMA user_version=27");
            Execute("INSERT INTO character_title_progress VALUES(1,'mood-a',6501,1,2,3,0); INSERT INTO character_title_sections VALUES(1,'mood-a',0,1)");
            Execute("INSERT INTO character_tutorial_state VALUES(1,'mood-a',7115,0,0); INSERT INTO account_hotkey_settings VALUES('mood-a',0,X'4930310937370A')");
            // 必需列检查位于ALTER之后，缺失旧账号列应回滚新列与版本。
            Execute("ALTER TABLE accounts DROP COLUMN disabled");
            Reject<SqliteException>(() => new CharacterRepository(path), "v28末段必需列失败回滚新心情列");
            That(Scalar("PRAGMA user_version") is 27L && Scalar("SELECT count(*) FROM pragma_table_info('characters') WHERE name='emotion'") is 0L,
                "心情迁移失败保留真实v27，不留下半份新结构");
            Execute("ALTER TABLE accounts ADD COLUMN disabled INTEGER NOT NULL DEFAULT 0 CHECK(disabled IN(0,1))");
            _ = new CharacterRepository(path);
            That(Scalar("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion && Scalar("SELECT emotion FROM characters WHERE id=1") is 0L &&
                Scalar("SELECT count(*) FROM characters") is 2L, "v27只补心情字段和缺省0，不删除旧角色或回填其他资产");
            That(Scalar("SELECT remaining1||':'||remaining2||':'||remaining3 FROM character_title_progress") is "1:2:3" &&
                Scalar("SELECT revision FROM character_title_sections") is 1L && Scalar("SELECT completed||':'||reward_received FROM character_tutorial_state") is "0:0" &&
                Scalar("SELECT hex(hotkeys) FROM account_hotkey_settings") is "4930310937370A", "v28迁移保留v25称号、v26训练及v27键位原记录");
            Reject<SqliteException>(() => Execute("UPDATE characters SET emotion=32768"), "数据库约束拒绝越界心情");
        }
        finally { if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, recursive: true); }
        Console.WriteLine($"PASS {Count - start} 心情原生字段、资源、角色隔离、SQL回滚及增量升级断言");

        void Execute(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        object? Scalar(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }
    }
}
