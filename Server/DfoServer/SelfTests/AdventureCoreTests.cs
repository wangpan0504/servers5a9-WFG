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
    internal static AdventureCatalog SyntheticCatalog()
    {
        var strings = new List<string>();
        PvfToken Token(object value) => value switch
        {
            int integer => new(0, integer),
            float number => new(2, BitConverter.SingleToInt32Bits(number)),
            string text => new(text.StartsWith('[') && !text.StartsWith("[swordman]") && !text.StartsWith("[A]") ? (byte)3 : (byte)6, Add(text)),
            _ => throw new InvalidDataException()
        };
        int Add(string text) { strings.Add(text); return strings.Count - 1; }
        PvfToken[] Document(object[] values) => values.Select(Token).ToArray();
        var main = Document([
            "[max adventure level]", 3, "[adventure exp rate]", 0.3f,
            "[adventure exp table]", 1, "0", 2, "10", 3, "20", 4, "30", "[/adventure exp table]",
            "[attribute list]", "[attribute]", 1, "[/attribute]", "[attribute]", 2, "[stat]", 2, "str", 10, "+%d", 0,
            "[grow]", 2, "clear result exp up", 10f, "+%d", 0, "[/attribute]", "[attribute]", 3, "[/attribute]", "[/attribute list]",
            "[expert list]", "[group]", 0, "profession", "[info]", "[swordman]", "second awakening", 1, 0, 0, 1, "[/group]", "[/expert list]",
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
        That(catalog.Experience[3] == 20 && catalog.CapsuleCapacity == 200 && catalog.Professions.ContainsKey((0, 1)),
            "基础资源读取等级、64位经验表和职业，不加载远征定义");
        var members = ImmutableArray.Create(new AdventureMember(1, 0, "HeroA", 0, 33, 95, 7, 0),
            new AdventureMember(2, 1, "HeroB", 0, 33, 95, 9, 0), new AdventureMember(3, 2, "NewHero", 0, 0, 1, 0, 0));
        var snapshot = new AdventureSnapshot(state with { Level = 3, Experience = (1UL << 32) + 7, Name = "测试团" }, members);
        var representatives = snapshot.Representatives(catalog);
        That(representatives is [{ Id: 1 }], "同职业按当前名册首个合法角色展示，不创建代表或在线会话");
        byte[] info = AdventurePackets.Information(42, snapshot, catalog, representatives, 2);
        That(BinaryPrimitives.ReadUInt16LittleEndian(info) == 42 && BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(14)) == 7440 &&
            BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(26)) == (1UL << 32) + 7 &&
            BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(86)) == 3 && info[18 + 72 + 42] == 2 &&
            BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(18 + 7361)) == 2, "1343完整7440B、账号数量、地域、职业计数及u64经验");
        byte[] name = CharacterName.Encoding.GetBytes("测试团");
        That(BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(18 + 7440)) == name.Length &&
            info.AsSpan(22 + 7440).SequenceEqual(name), "账号名称使用CP936字节长度，不按UTF8字符数发送");
        var ack = AdventurePackets.Information(0, snapshot, catalog, representatives, 2, ack: true);
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
            Execute(db, "UPDATE account_adventures SET name='测试团',revision=7");
            using (var writer = db.BeginTransaction(deferred: false))
            {
                repo.InitializeAdventure(owner, catalog, now.AddMonths(1));
                That(repo.ReadAdventure(owner, catalog, now).State is { Name: "测试团", Revision: 7, Level: 1, Experience: 0 },
                    "既有初始化和查询可在外部writer下完成，不重置名字、进度或开启第二个writer");
                writer.Rollback();
            }
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
                    Execute(db, "DROP TABLE account_adventures; PRAGMA user_version=28; INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('legacy',0,'Legacy',9,38,1,450,234,0,123)");
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
