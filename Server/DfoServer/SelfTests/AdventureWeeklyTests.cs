using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AdventureWeeklyTests
{
    public static void RunCore()
    {
        int checks = Count;
        That(AdventureRequests.ServerCharacters([0]) == 0 && AdventureRequests.ServerCharacters([3]) == 3, "789保留服务器u8");
        Reject<InvalidDataException>(() => AdventureRequests.ServerCharacters([]), "789拒绝缺少服务器字节");
        Reject<InvalidDataException>(() => AdventureRequests.ServerCharacters([0, 1]), "789拒绝额外字节");
        That(AdventureRequests.WeeklyConfig([0, 1, 2, 3, 0xFF]).SequenceEqual(new byte[] { 0, 1, 2, 3, 0xFF }),
            "791保留5个槽位原字节");
        Reject<InvalidDataException>(() => AdventureRequests.WeeklyConfig([0, 1, 2, 3]), "791不截断4字节配置");
        Reject<InvalidDataException>(() => AdventureRequests.WeeklyConfig([0, 1, 2, 3, 4, 5]), "791拒绝6字节配置");
        That(AdventureWeekly.Default.SequenceEqual(new byte[] { 0, 1, 2, 3, 0xFF }), "未保存时默认排列与客户端抓包一致");

        var body = AdventurePackets.ServerCharacters(2, [(7u, "A角色"), (9u, "B_2")]);
        That(body.Length == 28 && body[0] == 2 && body[1] == 2 &&
            BitConverter.ToUInt32(body, 2) == 7 && BitConverter.ToUInt32(body, 6) == 5 && body[15] == 0 &&
            CharacterName.Encoding.GetString(body, 10, 5) == "A角色" &&
            BitConverter.ToUInt32(body, 16) == 9 && BitConverter.ToUInt32(body, 20) == 3 && body[27] == 0 &&
            CharacterName.Encoding.GetString(body, 24, 3) == "B_2",
            "718按server/count/u32角色/u32长度/CP936名称/尾0排列");
        var truncated = AdventurePackets.ServerCharacters(0, [(1u, new string('中', 15))]);
        That(BitConverter.ToUInt32(truncated, 6) == 28 && CharacterName.Encoding.GetString(truncated, 10, 28) == new string('中', 14),
            "旧角色名超过29B时按完整字符截断，不拆双字节字符");

        var catalog = AdventureCoreTests.SyntheticCatalog();
        var now = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        string root = Path.Combine(Path.GetTempPath(), "s5a9-adventure-weekly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var auth = new AuthenticationService(new AuthenticationRepository(path));
            auth.Create("weekly", "fixture-password");
            auth.Create("weekly-other", "fixture-password");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("weekly", () => { });
            using var other = owners.Acquire("weekly-other", () => { });
            var first = repo.Create(owner, new("WeeklyOne", new(1, 0, 10, 10), null));
            _ = repo.Create(owner, new("WeeklyTwo", new(1, 0, 10, 10), null));
            repo.InitializeAdventure(owner, catalog, now);
            repo.InitializeAdventure(other, catalog, now);
            That(repo.ReadAdventureWeeklyConfig(owner).SequenceEqual(AdventureWeekly.Default), "未保存时按客户端默认排列读取");
            var rows = repo.ReadAdventureCharacterList(owner);
            That(rows.Length == 2 && rows[0].Id == first.Id && rows[0].Name == "WeeklyOne" && rows[1].Name == "WeeklyTwo",
                "718名册只含本账号角色并按槽位顺序");
            Reject<InvalidDataException>(() => repo.WriteAdventureWeeklyConfig(owner, [1, 2, 3, 4]), "仓储拒绝非5槽位配置");
            using var db = Open(path);
            Execute(db, "CREATE TRIGGER reject_weekly_insert BEFORE INSERT ON account_adventure_weekly_config BEGIN SELECT RAISE(ABORT,'late failure'); END");
            Reject<SqliteException>(() => repo.WriteAdventureWeeklyConfig(owner, [1, 2, 3, 4, 0xFF]), "首次保存失败不留下配置");
            Execute(db, "DROP TRIGGER reject_weekly_insert");
            repo.WriteAdventureWeeklyConfig(owner, [1, 2, 3, 4, 0xFF]);
            repo.WriteAdventureWeeklyConfig(owner, [3, 2, 1, 0, 0xFF]);
            That(Query(db, "SELECT hex(slots) FROM account_adventure_weekly_config WHERE account='weekly'") is "03020100FF" &&
                Query(db, "SELECT count(*) FROM account_adventure_weekly_config") is 1L, "重复保存覆盖同一账号行");
            Execute(db, "CREATE TRIGGER reject_weekly_update BEFORE UPDATE ON account_adventure_weekly_config BEGIN SELECT RAISE(ABORT,'late failure'); END");
            Reject<SqliteException>(() => repo.WriteAdventureWeeklyConfig(owner, [0, 1, 2, 3, 0xFF]), "更新失败保留上次提交的排列");
            Execute(db, "DROP TRIGGER reject_weekly_update");
            That(repo.ReadAdventureWeeklyConfig(owner).SequenceEqual(new byte[] { 3, 2, 1, 0, 0xFF }) &&
                Query(db, "SELECT revision FROM account_adventures WHERE account='weekly'") is 0L,
                "保存配置不推进账号revision");
            That(repo.ReadAdventureWeeklyConfig(other).SequenceEqual(AdventureWeekly.Default) &&
                Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L, "配置按账号隔离且外键一致");
            That(new CharacterRepository(path).ReadAdventureWeeklyConfig(owner).SequenceEqual(new byte[] { 3, 2, 1, 0, 0xFF }),
                "重开库保留已提交排列");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} 每周地下城信息解析、718名册与账号配置持久化断言");
    }

    static SqliteConnection Open(string path) { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open(); return db; }
    static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    static object? Query(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
}
