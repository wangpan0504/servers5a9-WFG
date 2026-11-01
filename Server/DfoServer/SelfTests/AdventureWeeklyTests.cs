using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
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
        That(AdventureWeekly.Default.SequenceEqual(new byte[] { 0, 1, 3, 4, 6 }),
            "未保存时按PVF [first weekly dungeon info config] = 0 1 3 4 6");

        // 714：u8 server、u8 count，每行 u32角色/u32名称长度/CP936名称/u8标志/u16等级/u8职业/u8分支/u8二觉。
        var none = ImmutableArray.Create(new int[WeeklyDungeonPolicy.SlotCount]);
        var body = AdventurePackets.ServerCharacters(2,
        [
            new AdventureWeeklyCharacter(7, "A角色", 95, 0, 0x20, none),
            new AdventureWeeklyCharacter(9, "B_2", 95, 9, 0, none),
        ]);
        That(body.Length == 38 && body[0] == 2 && body[1] == 2 &&
            BitConverter.ToUInt32(body, 2) == 7 && BitConverter.ToUInt32(body, 6) == 5 && body[15] == 0 &&
            BitConverter.ToUInt16(body, 16) == 95 && body[18] == 0 && body[19] == 0 && body[20] == 2 &&
            CharacterName.Encoding.GetString(body, 10, 5) == "A角色" &&
            BitConverter.ToUInt32(body, 21) == 9 && BitConverter.ToUInt32(body, 25) == 3 && body[32] == 0 &&
            BitConverter.ToUInt16(body, 33) == 95 && body[35] == 9 && body[36] == 0 && body[37] == 0 &&
            CharacterName.Encoding.GetString(body, 29, 3) == "B_2",
            "718按server/count/角色/名称/标志/等级/职业/分支/二觉排列，外传职业发0/0");
        var truncated = AdventurePackets.ServerCharacters(0,
            [new AdventureWeeklyCharacter(1, new string('中', 15), 95, 0, 0, none)]);
        That(BitConverter.ToUInt32(truncated, 6) == 28 && CharacterName.Encoding.GetString(truncated, 10, 28) == new string('中', 14),
            "旧角色名超过29B时按完整字符截断，不拆双字节字符");

        // 719：u8 key、u8 count，每行 u32名称长度/CP936名称/0x51记录（A[9]u32@0、B[9]u32@0x24、C[9]u8@0x48）。
        var used = ImmutableArray.Create(0, 2, -1, 1, 0, -1, 0, 0, 0);
        var info = AdventurePackets.WeeklyDungeonInfo(2, [new AdventureWeeklyCharacter(7, "A角色", 95, 0, 0x20, used)]);
        That(info.Length == 92 && info[0] == 2 && info[1] == 1 && BitConverter.ToUInt32(info, 2) == 5 &&
            CharacterName.Encoding.GetString(info, 6, 5) == "A角色" &&
            BitConverter.ToUInt32(info, 11) == 0 && BitConverter.ToUInt32(info, 11 + 4) == 2 &&
            BitConverter.ToUInt32(info, 11 + 8) == uint.MaxValue && BitConverter.ToUInt32(info, 11 + 8 * 4) == 0 &&
            BitConverter.ToUInt32(info, 11 + 0x24) == 0 && info[11 + 0x48] == 1 && info[11 + 0x48 + 5] == 1,
            "719按本周已用次数回包，A=-1原样表示无来源，B为0，C恒为1");
        var filtered = AdventurePackets.WeeklyDungeonInfo(3,
            [new AdventureWeeklyCharacter(7, "A角色", 95, 0, 0x20, used), new AdventureWeeklyCharacter(9, "B_2", 95, 9, 0, used)],
            ["B_2", "Missing", "B_2"]);
        That(filtered.Length == 90 && filtered[0] == 3 && filtered[1] == 1 &&
            BitConverter.ToUInt32(filtered, 2) == 3 && CharacterName.Encoding.GetString(filtered, 6, 3) == "B_2",
            "790回包只带请求名、同名去重、未知名字跳过");

        // 列归属：只按PVF副本路径前缀判断，未列入的副本不消耗每周次数。
        That(WeeklyDungeonPolicy.SlotOf(new DungeonDefinition(1, "dungeon/gcontents/190307_prey_raid/prey.dgn", null, null, [], [])) == 0 &&
            WeeklyDungeonPolicy.SlotOf(new DungeonDefinition(2, "dungeon/fiendwar_kr/fiendwar.dgn", null, null, [], [])) == 1 &&
            WeeklyDungeonPolicy.SlotOf(new DungeonDefinition(3, "dungeon/luke_normal/luke.dgn", null, null, [], [])) == 2 &&
            WeeklyDungeonPolicy.SlotOf(new DungeonDefinition(4, "dungeon/legend/tayberrs/tayberrs.dgn", null, null, [], [])) == 3 &&
            WeeklyDungeonPolicy.SlotOf(new DungeonDefinition(5, "dungeon/op_hope/op_hope.dgn", null, null, [], [])) == 4 &&
            WeeklyDungeonPolicy.SlotOf(new DungeonDefinition(6, "dungeon/towers/towerofdazzlement/1.dgn", null, null, [], [])) == 6 &&
            WeeklyDungeonPolicy.SlotOf(new DungeonDefinition(7, "dungeon/kcontents2/theevildomwar/x.dgn", null, null, [], [])) == 7 &&
            WeeklyDungeonPolicy.SlotOf(new DungeonDefinition(8, "dungeon/guilddungeon/guildnomal1.dgn", null, null, [], [])) == 8 &&
            WeeklyDungeonPolicy.SlotOf(new DungeonDefinition(9, "dungeon/assault/x.dgn", null, null, [], [])) is null,
            "每周地下城列按PVF副本路径归属，普通副本与强袭不计入");
        var anchor = new DateTimeOffset(2018, 2, 22, 6, 0, 0, TimeSpan.FromHours(8));
        That(WeeklyDungeonPolicy.WeekKey(anchor) == 20180222 && WeeklyDungeonPolicy.WeekKey(anchor.AddDays(7)) == 20180301 &&
            WeeklyDungeonPolicy.WeekKey(anchor.AddDays(7).AddTicks(-1)) == 20180222 && WeeklyDungeonPolicy.WeekKey(anchor.AddDays(-1)) == 0 &&
            WeeklyDungeonPolicy.WeekKey(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(8))) == 20261022,
            "每周地下城周四06:00重置，锚点前为0");

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
            var first = repo.Create(owner, new("WeeklyOne", new(1, 0, 10, 10), null)
                { Level = 95, Job = 9, DungeonState = CharacterDungeonState.NewOrdinary });
            _ = repo.Create(owner, new("WeeklyTwo", new(1, 0, 10, 10), null) { GrowType = 0x20 });
            repo.InitializeAdventure(owner, catalog, now);
            repo.InitializeAdventure(other, catalog, now);
            That(repo.ReadAdventureWeeklyConfig(owner).SequenceEqual(AdventureWeekly.Default), "未保存时按PVF默认排列读取");
            var roster = repo.ReadAdventureWeeklyRoster(owner, 20261022);
            That(roster.Length == 2 && roster[0].Id == first.Id && roster[0].Name == "WeeklyOne" && roster[0].Level == 95 &&
                roster[0].Job == 9 && roster[0].Grow == 0 && roster[0].UsedAt(0) == 0 && roster[0].UsedAt(5) == -1,
                "718名册只含本账号角色并按槽位顺序，未入场时已用次数为0，5号列无来源为-1");
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

            // 每周计数与尝试号同一事务：成功累加、按周隔离；计数写入失败整笔回滚。
            var reserved = repo.ReserveDungeonAttempt(owner, first, first.DungeonState!.NextObjectKey, new WeeklyDungeonEntry(20261022, 3));
            var once = repo.ReadAdventureWeeklyRoster(owner, 20261022);
            That(reserved.DungeonState!.LastAttempt == first.DungeonState.LastAttempt + 1 &&
                once[0].UsedAt(3) == 1 && once[1].UsedAt(3) == 0,
                "入场预留同一事务累加本周次数，只影响该角色该列");
            var second = repo.ReserveDungeonAttempt(owner, reserved, reserved.DungeonState!.NextObjectKey, new WeeklyDungeonEntry(20261022, 3));
            That(repo.ReadAdventureWeeklyRoster(owner, 20261022)[0].UsedAt(3) == 2, "同一周同一列重复入场继续累加");
            _ = repo.ReserveDungeonAttempt(owner, second, second.DungeonState!.NextObjectKey, new WeeklyDungeonEntry(20261029, 3));
            That(repo.ReadAdventureWeeklyRoster(owner, 20261022)[0].UsedAt(3) == 2 &&
                repo.ReadAdventureWeeklyRoster(owner, 20261029)[0].UsedAt(3) == 1, "不同周分别计数");
            var latest = repo.LoadCharacter(owner, first.Slot);
            Execute(db, "CREATE TRIGGER reject_weekly_usage BEFORE INSERT ON character_weekly_dungeon BEGIN SELECT RAISE(ABORT,'late failure'); END");
            Reject<SqliteException>(() => repo.ReserveDungeonAttempt(owner, latest, latest.DungeonState!.NextObjectKey,
                new WeeklyDungeonEntry(20261022, 0)), "每周计数写入失败整笔回滚");
            Execute(db, "DROP TRIGGER reject_weekly_usage");
            var unchanged = repo.LoadCharacter(owner, first.Slot);
            That(unchanged.Revision == latest.Revision && unchanged.DungeonState == latest.DungeonState &&
                repo.ReadAdventureWeeklyRoster(owner, 20261022)[0].UsedAt(0) == 0 && repo.ReadAdventureWeeklyRoster(other, 20261022).IsEmpty,
                "计数失败不推进revision或尝试号，其他账号不受影响");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - checks} 每周地下城信息解析、718/719构包、列归属与计数持久化断言");
    }

    static SqliteConnection Open(string path) { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open(); return db; }
    static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    static object? Query(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
}
