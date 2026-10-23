using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Adventure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AdventureCoreTests
{
    // secondSwordman 为 true 时分类0声明两个转职，用于区分“已收集但未集齐”与“整类集齐”两种按钮状态。
    internal static AdventureCatalog SyntheticCatalog(bool secondSwordman = false)
    {
        var strings = new List<string>();
        object[] swordman = secondSwordman
            ? ["[info]", "[swordman]", "second awakening", 1, 0, 0, 1, "[info]", "[swordman]", "second awakening", 2, 0, 0, 1]
            : ["[info]", "[swordman]", "second awakening", 1, 0, 0, 1];
        PvfToken Token(object value) => value switch
        {
            int integer => new(0, integer),
            float number => new(2, BitConverter.SingleToInt32Bits(number)),
            string text => new(text.StartsWith('[') && !text.StartsWith("[swordman]") && !text.StartsWith("[fighter]") && !text.StartsWith("[A]") ? (byte)3 : (byte)6, Add(text)),
            _ => throw new InvalidDataException()
        };
        int Add(string text) { strings.Add(text); return strings.Count - 1; }
        PvfToken[] Document(object[] values) => values.Select(Token).ToArray();
        var main = Document([
            "[max adventure level]", 3, "[adventure exp rate]", 0.3f,
            "[adventure exp table]", 1, "0", 2, "10", 3, "20", 4, "30", "[/adventure exp table]",
            "[attribute list]", "[attribute]", 1, "[/attribute]", "[attribute]", 2, "[stat]", 2, "str", 10, "+%d", 0,
            "[grow]", 2, "clear result exp up", 10f, "+%d", 0,
            "[etc]", 2, "add tower of despair enter count", 1, "+%d", 1,
            "[item]", 20, "antonium drop", 60, "+%.2f%%", 1,
            "[/attribute]", "[attribute]", 3, "[/attribute]", "[/attribute list]",
            "[expert list]", "[group]", 0, "profession", .. swordman, "[/group]",
            "[group]", 1, "[info]", "[fighter]", "second awakening", 1, 0, 0, 1, "[/group]", "[/expert list]",
            "[adventurer shop list]", "[shop]", "[index]", 0, "[max shop point]", 250, "[exp to get shop point]", 5, 1,
            "[adventurer shop purchase info]", 1000, 1, 1, 2, 3, "[/adventurer shop purchase info]", "[/shop]",
            "[shop]", "[index]", 1, "[max shop point]", 9999, "[reset point]", "[adventurer shop purchase info]", "[/adventurer shop purchase info]", "[/shop]",
            "[/adventurer shop list]", "[capsule need minimum exp]", 10, "[capsule max exp]", 100, "[capsule max count]", 2,
            "[capsule limit level min]", 2, "[capsule limit level max]", 90, "[capsule give exp]", 50,
            "[capsule update date]", "2018-02-22 06:00:00"]);
        return new(_ => main, id => strings[id]);
    }

    public static void RunCore()
    {
        int before = Count;
        var catalog = SyntheticCatalog();
        var now = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        var state = AdventureState.Initial("adventure", now);
        state.Validate(catalog);
        CharacterRequests.HiddenRoster([]);
        Bytes(CharacterPackets.HiddenRoster(), [1, 0], "ACK645包含隐藏角色数量");
        Reject<InvalidDataException>(() => CharacterRequests.HiddenRoster([0]), "隐藏角色查询拒绝未知正文");
        That(AdventureRequests.Target([0x34, 0x12]) == 0x1234, "冒险团目标保留u16");
        Reject<InvalidDataException>(() => AdventureRequests.Target([0]), "冒险团查询不截断目标");
        AdventureRequests.Empty([]);
        Reject<InvalidDataException>(() => AdventureRequests.Empty([0]), "胶囊拒绝猜填附加字段");
        That(catalog.Experience[3] == 20 && catalog.CapsuleCapacity == 100 &&
            catalog.Capsule.MinimumExperience * catalog.Capsule.MaximumGauges <= catalog.CapsuleCapacity &&
            catalog.Professions.ContainsKey((0, 1)),
            "基础资源读取等级、64位经验表和职业，不加载远征定义");
        var benefit = catalog.Benefit(2);
        That(benefit.Stats["str"] == 10 && benefit.Growth[AdventureBenefitPolicy.ClearExperienceGrowth] == 10 &&
            benefit.Etc["add tower of despair enter count"] == 1 && benefit.Items["antonium drop"] == 60 &&
            catalog.Benefit(3).Growth.Count == 0,
            "属性行按名称读取四维、成长、功能与掉落，无子项等级不伪造加成");
        That(AdventureBenefitPolicy.ClearExperienceBonus(1000, 1, catalog) == 0 &&
            AdventureBenefitPolicy.ClearExperienceBonus(1000, 2, catalog) == 100 &&
            AdventureBenefitPolicy.ClearExperienceBonus(1009, 2, catalog) == 100 &&
            AdventureBenefitPolicy.ClearExperienceBonus(0, 2, catalog) == 0 &&
            AdventureBenefitPolicy.ClearExperienceBonus(1000, 4, catalog) == 0,
            "冒险团通关经验加成只取当前等级成长项，按百分比向下取整且缺级不臆造");
        That(AdventurePeriod.LoginDay(new DateTimeOffset(2026, 10, 12, 5, 59, 0, TimeSpan.FromHours(8))) == 20261011 &&
            AdventurePeriod.LoginDay(new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.FromHours(8))) == 20261012,
            "连续登录日期按UTC+8的06:00切换");
        That(AdventurePeriod.NextLoginStreak(20261031, 4, 20261101) == 5 &&
            AdventurePeriod.NextLoginStreak(20261011, 4, 20261013) == 1 &&
            AdventurePeriod.NextLoginStreak(20261011, 4, 20261011) == 4 &&
            AdventurePeriod.NextLoginStreak(0, 0, 20261011) == 1, "连续登录跨月递增、断天归零、同日不变、迁移后首次计1");
        var members = ImmutableArray.Create(new AdventureMember(1, 0, "HeroA", 0, 33, 95, 7, 0),
            new AdventureMember(2, 1, "HeroB", 0, 33, 95, 9, 0), new AdventureMember(3, 2, "NewHero", 0, 0, 1, 0, 0));
        var counts = new ushort[AdventureContent.Count]; counts[AdventureContent.RecommendedDungeon] = 12;
        var snapshot = new AdventureSnapshot(state with
        {
            Level = 3, Experience = (1UL << 32) + 7, Name = "测试团", LoginStreakDays = 6
        }, members) { ContentCounts = ImmutableArray.Create(counts) };
        var representatives = snapshot.Representatives(catalog);
        That(representatives is [{ Id: 2 }], "同职业默认展示熟练度最高的合法角色，不创建代表或在线会话");
        byte[] info = AdventurePackets.Information(42, snapshot, catalog, representatives);
        That(BinaryPrimitives.ReadUInt16LittleEndian(info) == 42 && BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(14)) == 7440 &&
            BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(26)) == (1UL << 32) + 7 &&
            BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(18 + 20)) == 6 &&
            BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(86)) == 3 && info[18 + 72 + 42] == 9 &&
            BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(18 + 72 + 44)) == 9 &&
            info[18 + 56] == 0 && info[18 + 57] == representatives[0].ProfessionGrow &&
            BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(18 + 60)) == 9 &&
            BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(18 + AdventurePackets.RecommendedClearOffset)) == 12 &&
            new[]
            {
                AdventurePackets.LukeRaidClearOffset, AdventurePackets.MetroRaidClearOffset,
                AdventurePackets.PreyRaidClearOffset, AdventurePackets.DuelWinOffset,
                AdventurePackets.DespairTowerClearOffset, AdventurePackets.LamentTowerClearOffset
            }.All(offset => BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(18 + offset)) == 0) &&
            info[18 + AdventurePackets.ContentCountsOffset] == 0 &&
            info[18 + AdventurePackets.ContentCountsOffset + 1] == 1 &&
            info[18 + AdventurePackets.ContentCountsOffset + 2] == 1 &&
            info[18 + AdventurePackets.ContentCountsOffset + 3] == 1 &&
            info[18 + AdventurePackets.ContentCountsOffset + 4] == 0 &&
            info[18 + AdventurePackets.ContentCountsOffset + 5] == 0,
            "1343完整7440B、账号数量、连续登录、七项内容熟练度u32、收藏分类按整类集齐下发（type:u8@0、count:u8@1、complete:u8@2）及u64经验");
        var sameBranch = snapshot with { Members = members.Add(new AdventureMember(4, 3, "HeroC", 0, 33, 95, 5, 0)) };
        That(sameBranch.Collections(catalog)[0] is { Count: 1, Complete: true },
            "收藏分类计数按不同转职去重，同一分支的多个角色只计一次，整类集齐时complete=1");
        var crossGroup = snapshot with { Members = members.Add(new AdventureMember(4, 3, "FighterC", 1, 33, 95, 5, 0)) };
        var crossCounts = crossGroup.Collections(catalog);
        That(crossCounts[0] is { Count: 1, Complete: true } && crossCounts[1] is { Count: 1, Complete: true } &&
            crossCounts[2] is { Count: 0, Complete: false },
            "收藏分类状态按PVF expert list分组，未收集的分类保持0且不点亮");
        // 分类声明两个转职时，只收集其中一个不能点亮；集齐整类才下发complete，对应客户端FUN_00D8F1C0的byte2判定。
        var wide = SyntheticCatalog(secondSwordman: true);
        var partial = snapshot.Collections(wide);
        var full = (snapshot with { Members = members.Add(new AdventureMember(5, 4, "HeroD", 0, 34, 95, 3, 0)) }).Collections(wide);
        That(partial[0] is { Count: 1, Complete: false } && full[0] is { Count: 2, Complete: true },
            "整类未集齐时按钮保持灰色，集齐全部转职后complete才为1");
        // 角标是u8：熟练等级超过255时客户端只能显示最高档，服务端按上限饱和，不能截断成回绕值。
        var capped = snapshot with { Members = ImmutableArray.Create(new AdventureMember(2, 1, "HeroB", 0, 33, 95, 300, 0)) };
        That(AdventurePackets.Information(42, capped, catalog, capped.Representatives(catalog))[18 + 72 + 42] == 255,
            "熟练等级超过255按最高档角标下发");
        byte[] name = CharacterName.Encoding.GetBytes("测试团");
        Bytes(AdventurePackets.AccountName("测试团"), [6, 0, 0, 0, 0xB2, 0xE2, 0xCA, 0xD4, 0xCD, 0xC5, 0],
            "1375使用独立CP936名称和尾部标志，不混入1343或固定Actor状态");
        var maximumName = AdventurePackets.AccountName(new string('测', 16));
        That(maximumName.Length == 37 && BinaryPrimitives.ReadInt32LittleEndian(maximumName) == 32 && maximumName[^1] == 0,
            "1375保留16个中文字符的32B边界及零标志");
        That(BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(18 + 7440)) == name.Length &&
            info.AsSpan(22 + 7440).SequenceEqual(name), "账号名称使用CP936字节长度，不按UTF8字符数发送");
        byte[] empty = AdventurePackets.Information(42, snapshot, catalog, []);
        That(BinaryPrimitives.ReadUInt32LittleEndian(empty.AsSpan(18 + AdventurePackets.RecommendedClearOffset)) == 12 &&
            BinaryPrimitives.ReadUInt32LittleEndian(empty.AsSpan(18 + 60)) == 0 &&
            Enumerable.Range(0, (AdventurePackets.ShopOffset - AdventurePackets.RosterOffset) / 52)
                .All(row => BinaryPrimitives.ReadUInt16LittleEndian(empty.AsSpan(18 + AdventurePackets.RosterOffset + row * 52 + 44)) == 0),
            "没有代表角色时内容熟练度仍按账号累计下发，最高熟练度卡片保持占位，名册行不伪造熟练度");
        var ack = AdventurePackets.Information(0, snapshot, catalog, representatives, ack: true);
        That(ack[0] == 1 && ack.AsSpan(1, 2).SequenceEqual(new byte[2]) && ack.Length == info.Length + 1,
            "ACK1403保留成功前缀与选角owner0");
        byte[] refresh = AdventurePackets.Refresh(42, snapshot.State, catalog);
        That(refresh.Length == 50 && BinaryPrimitives.ReadUInt32LittleEndian(refresh.AsSpan(2)) == 40 &&
            BinaryPrimitives.ReadUInt64LittleEndian(refresh.AsSpan(14)) == snapshot.State.Experience, "1349只使用40B实体状态");
        Persist(catalog, now);
        Migrate();
        Console.WriteLine($"PASS {Count - before} 冒险团基础资源、字节、账号查询与增量迁移断言");
    }

    static void Persist(AdventureCatalog catalog, DateTimeOffset now)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-adventure-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            new AuthenticationService(new AuthenticationRepository(path)).Create("adventure", "fixture-password");
            using var owner = new AccountOwners().Acquire("adventure", () => { });
            repo.Create(owner, new("Hero", new(1, 0, 10, 10), null));
            using var db = Open(path);
            var initial = repo.ReadAdventure(owner, catalog, now);
            That(initial.Members.Length == 1 && Query(db, "SELECT count(*) FROM account_adventures") is 0L,
                "缺状态的只读查询不取得writer或伪造历史进度");
            Execute(db, "CREATE TRIGGER reject_adventure BEFORE INSERT ON account_adventures BEGIN SELECT RAISE(ABORT,'fixture failure'); END");
            Reject<SqliteException>(() => repo.InitializeAdventure(owner, catalog, now), "账号初始化SQL失败整笔回滚");
            That(Query(db, "SELECT count(*) FROM account_adventures") is 0L, "失败不留下半份账号信息");
            Execute(db, "DROP TRIGGER reject_adventure");
            repo.InitializeAdventure(owner, catalog, now);
            var born = repo.ReadAdventure(owner, catalog, now).State;
            That(born.LoginStreakDays == 1 && born.LastLoginDay == AdventurePeriod.LoginDay(now),
                "首次登录在同一事务记录连续天数为1");
            repo.Create(owner, new("Hero2", new(2, 0, 10, 10), null));
            Execute(db, "INSERT INTO character_dungeon_state(character_id,account,next_object_key,last_attempt,capsule_experience,proficiency_level,proficiency_experience,recommended_dungeon_clears) " +
                "VALUES(1,'adventure',1,0,0,0,0,3),(2,'adventure',1,0,0,0,0,4)");
            var counted = repo.ReadAdventure(owner, catalog, now);
            That(counted.ContentCounts.Length == AdventureContent.Count && counted.ContentCounts[AdventureContent.RecommendedDungeon] == 7,
                "推荐地下城计数按角色累计后按账号求和");
            Execute(db, "UPDATE character_dungeon_state SET recommended_dungeon_clears=40000 WHERE character_id=1;" +
                "UPDATE character_dungeon_state SET recommended_dungeon_clears=30000 WHERE character_id=2");
            That(repo.ReadAdventure(owner, catalog, now).ContentCounts[AdventureContent.RecommendedDungeon] == ushort.MaxValue,
                "账号内容计数按u16饱和，不因累计溢出回绕");
            Execute(db, "UPDATE account_adventures SET name='测试团',revision=7");
            using (var writer = db.BeginTransaction(deferred: false))
            {
                repo.InitializeAdventure(owner, catalog, now);
                That(repo.ReadAdventure(owner, catalog, now).State is { Name: "测试团", Revision: 7, Level: 1, Experience: 0 },
                    "同日的既有初始化和查询可在外部writer下只读完成，不重置名字、进度或开启第二个writer");
                writer.Rollback();
            }
            repo.InitializeAdventure(owner, catalog, now.AddDays(1));
            That(repo.ReadAdventure(owner, catalog, now.AddDays(1)).State is { Revision: 8, LoginStreakDays: 2 },
                "跨06:00周期连续登录+1并推进账号版本");
            repo.InitializeAdventure(owner, catalog, now.AddDays(3));
            That(repo.ReadAdventure(owner, catalog, now.AddDays(3)).State is { Revision: 9, LoginStreakDays: 1 },
                "断一天以上归零重计1");
            That(new CharacterRepository(path).ReadAdventure(owner, catalog, now).State.Name == "测试团" &&
                Query(db, "SELECT revision FROM characters") is 0L && Query(db, "PRAGMA integrity_check") is "ok" &&
                Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L, "重开恢复名称，角色资产及外键不受初始化影响");
            using var current = new AccountOwners().Acquire("other", () => { });
            That(repo.ReadAdventure(current, catalog, now).State.Name == "other", "查询按账号隔离，不借用其他账号名称");
        }
        finally { Directory.Delete(root, true); }
    }

    static void Migrate()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-adventure-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool fail in new[] { false, true })
            {
                string path = Path.Combine(root, "legacy-" + fail + ".db");
                _ = new CharacterRepository(path);
                using (var db = Open(path))
                {
                    DatabaseSchemaTests.RemoveAdventureSchema(db);
                    Execute(db, "PRAGMA user_version=28; INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('legacy',0,'Legacy',9,38,1,450,234,0,123)");
                    if (fail) Execute(db, "CREATE VIEW account_adventures AS SELECT 1 AS collision");
                }
                if (fail)
                {
                    Reject<SqliteException>(() => new CharacterRepository(path), "v29撞表拒绝且保留旧版本");
                    using var db = Open(path);
                    That(Query(db, "PRAGMA user_version") is 28L && Query(db, "SELECT revision||':'||gold FROM characters") is "9:123",
                        "升级失败保留版本及角色余额");
                }
                else
                {
                    _ = new CharacterRepository(path);
                    using var db = Open(path);
                    That(Query(db, "PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                        Query(db, "SELECT count(*) FROM account_adventures") is 0L &&
                        Query(db, "SELECT revision||':'||gold FROM characters") is "9:123", "v28仅新增空账号进度表，不补发历史成长或奖励");
                    _ = new CharacterRepository(path);
                    That(Query(db, "SELECT count(*) FROM character_title_progress") is 0L, "重开保持原称号结构及空账号进度");
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True");
        db.Open(); return db;
    }
    static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    static object? Query(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
}
