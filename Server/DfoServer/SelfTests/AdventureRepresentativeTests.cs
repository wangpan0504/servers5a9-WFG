using System.Collections.Immutable;
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

internal static class AdventureRepresentativeTests
{
    public static void RunCore()
    {
        int before = Count;
        var catalog = AdventureCoreTests.SyntheticCatalog();
        var service = new AdventureRepresentativeService(catalog);
        var now = DateTimeOffset.UtcNow;
        var first = new AdventureMember(1, 0, "HeroA", 0, 33, 95, 7, 0);
        var second = first with { Id = 2, Slot = 1, Name = "HeroB" };
        var snapshot = new AdventureSnapshot(AdventureState.Initial("rep", now), [first, second]);
        var plan = service.Prepare(snapshot, 1, 0, 1, 2)!;
        That(plan.Changed && (snapshot with { State = snapshot.State with { Representatives = plan.After } }).Representatives(catalog) is [{ Id: 2 }],
            "指定同账号真实UID，不按槽位或名册首项重新选择");
        That(service.Prepare(snapshot, 1, 0, 1, 999) is null && service.Prepare(snapshot, 1, 0, 2, 2) is null &&
            service.Prepare(snapshot with { Members = [second with { Level = 89 }] }, 1, 0, 1, 2) is null,
            "拒绝别的账号/不存在角色、错误职业和Lv90以下资格");
        That(service.Prepare(snapshot with { Members = [second with { Level = 90 }] }, 1, 0, 1, 2) is not null,
            "Lv90（含）起满足客户端帮助的收藏等级资格");
        var removed = snapshot with { State = snapshot.State with { Representatives = [new(0, 1, null)] } };
        That(removed.Representatives(catalog).IsEmpty && !service.Prepare(removed, 1, 0, 1, null)!.Changed,
            "显式解除保留职业空位，重复解除为合法零变化");
        That(AdventureRequests.Representative([2, 2, 0, 0, 0, 0, 33]) == (2, 2, 0, 1),
            "代表设置保留region、UID32及二觉打包成长字段");
        That(AdventureRequests.Profession([9, 0x20]) == (9, 1) && AdventureRequests.Profession([10, 0x20]) == (10, 1) &&
            AdventureRequests.Representative([2, 2, 0, 0, 0, 9, 0x20]) == (2, 2, 9, 1),
            "外传职业低半字节0归一化为收藏分支1，不再因猜填拒绝而断线");
        Reject<InvalidDataException>(() => AdventureRequests.Profession([0, 1]), "不把普通成长字节当二觉");
        var candidates = AdventurePackets.Candidates([first, second], 2);
        That(candidates.Length == 108 && candidates[0] == 1 && candidates[1] == 1 && candidates[2] == 2 && candidates[3] == 2 &&
            BitConverter.ToUInt32(candidates, 16) == 1 && BitConverter.ToUInt32(candidates, 68) == 2,
            "1476为52B候选，不误用56B客户端扩展记录");
        var set = AdventurePackets.Representative(catalog.Professions[(0, 1)], new AdventureCollection(2, true), second, 2);
        That(set.Length == 64 && BitConverter.ToUInt32(set, 1) == 3 && set[5] == catalog.Professions[(0, 1)].Group &&
            set[6] == 2 && set[7] == 1 && BitConverter.ToUInt32(set, 8) == 52 && set[54] == 2 && BitConverter.ToUInt32(set, 16) == 7 && BitConverter.ToUInt16(set, 56) == 7,
            "1477成功含3B分类结构（type:u8@0、count:u8@1、complete:u8@2）及52B收藏RAW，服务器在收藏42偏移，熟练度仍独立在4及44");
        var information = AdventurePackets.Information(1, snapshot, catalog, [second], 2);
        Bytes(set.AsSpan(12, 52).ToArray(), information.AsSpan(18 + 72, 52).ToArray(),
            "1477收藏行与1343对同一代表使用完全相同的服务器、熟练度及身份字段");
        Persist(service, now);
        Console.WriteLine($"PASS {Count - before} 代表职业资格、字节、零变化与SQLite原子断言");
    }

    static void Persist(AdventureRepresentativeService service, DateTimeOffset now)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-adventure-rep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            new AuthenticationService(new AuthenticationRepository(path)).Create("rep", "fixture-password");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("rep", () => { });
            var actor = repo.Create(owner, new("Actor", new(1, 0, 10, 10), null));
            var member = repo.Create(owner, new("RepHero", new(1, 0, 10, 10), null) { Job = 0, GrowType = 33, Level = 95 });
            repo.InitializeAdventure(owner, service.Catalog, now);
            var snapshot = repo.ReadAdventure(owner, service.Catalog, now);
            var plan = service.Prepare(snapshot, actor.Id, 0, 1, member.Id)!;
            using var db = Open(path);
            Execute(db, "CREATE TRIGGER reject_rep BEFORE INSERT ON account_adventure_representatives BEGIN SELECT RAISE(ABORT,'late failure'); END");
            Reject<SqliteException>(() => repo.CommitAdventureRepresentative(owner, plan, service.Catalog, now), "代表晚期插入失败整笔回滚");
            That(Query(db, "SELECT revision FROM account_adventures") is 0L && Query(db, "SELECT count(*) FROM account_adventure_representatives") is 0L,
                "失败不留下账号revision或半份代表绑定");
            Execute(db, "DROP TRIGGER reject_rep");
            var committed = repo.CommitAdventureRepresentative(owner, plan, service.Catalog, now);
            That(committed.State.Revision == 1 && committed.Representatives(service.Catalog).Single().Id == member.Id &&
                Query(db, "SELECT sum(revision) FROM characters") is 0L, "仅提交代表与账号版本，不改角色资产");
            Reject<InvalidOperationException>(() => repo.CommitAdventureRepresentative(owner, plan, service.Catalog, now), "旧账号revision不能再次覆盖代表");
            var repeat = service.Prepare(committed, actor.Id, 0, 1, member.Id)!;
            using (var writer = db.BeginTransaction(deferred: false))
            {
                That(repo.CommitAdventureRepresentative(owner, repeat, service.Catalog, now).State.Revision == 1,
                    "相同设置不取得writer，外部writer存在时也能只读验证");
                writer.Rollback();
            }
            var removed = repo.CommitAdventureRepresentative(owner, service.Prepare(committed, actor.Id, 0, 1, null)!, service.Catalog, now);
            That(removed.State.Revision == 2 && removed.Representatives(service.Catalog).IsEmpty &&
                Query(db, "SELECT character_id FROM account_adventure_representatives") is DBNull, "解除保存显式NULL职业格，重连不会恢复默认候选");
            That(new CharacterRepository(path).ReadAdventure(owner, service.Catalog, now).State.Representatives.SequenceEqual(removed.State.Representatives),
                "重开保留解除与完整账号状态");
            using var current = owners.Acquire("rep", () => { });
            Reject<InvalidOperationException>(() => repo.CommitAdventureRepresentative(owner, repeat, service.Catalog, now), "旧generation不能提交给新owner");
            That(Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "账号与真实代表UID外键完整");
            var latest = repo.ReadAdventure(current, service.Catalog, now);
            var assigned = repo.CommitAdventureRepresentative(current, service.Prepare(latest, actor.Id, 0, 1, member.Id)!, service.Catalog, now);
            repo.Delete(current, repo.LoadDeletionTarget(current, member.Slot));
            var afterDelete = repo.ReadAdventure(current, service.Catalog, now);
            That(afterDelete.State.Revision == assigned.State.Revision + 1 && afterDelete.State.Representatives.Single().CharacterId is null &&
                afterDelete.State.Name == assigned.State.Name && Query(db, "SELECT count(*) FROM characters") is 1L &&
                Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "删除代表角色保留显式NULL职业格与账号进度，只推进一次账号版本，不级联清空冒险团");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Migrate();
    }

    static void Migrate()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-rep-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool fail in new[] { false, true })
            {
                string path = Path.Combine(root, fail + ".db");
                _ = new CharacterRepository(path);
                new AuthenticationService(new AuthenticationRepository(path)).Create("legacy", "fixture-password");
                using (var db = Open(path))
                {
                    DatabaseSchemaTests.RemoveAdventureRepresentatives(db);
                    DatabaseSchemaTests.RewindRaidLifecycle(db);
                    Execute(db, "PRAGMA user_version=29; " +
                        "INSERT INTO account_adventures VALUES('legacy',7,'LegacyGroup',20261011,1,0,'0',0,0,zeroblob(20),202610)");
                    if (fail) Execute(db, "CREATE VIEW account_adventure_representatives AS SELECT 1 AS collision");
                }
                if (fail)
                {
                    Reject<SqliteException>(() => new CharacterRepository(path), "v30撞表失败回滚");
                    using var db = Open(path);
                    That(Query(db, "PRAGMA user_version") is 29L && Query(db, "SELECT revision||':'||name FROM account_adventures") is "7:LegacyGroup",
                        "失败保留v29和账号原名称版本");
                }
                else
                {
                    _ = new CharacterRepository(path);
                    using var db = Open(path);
                    That(Query(db, "SELECT count(*) FROM account_adventure_representatives") is 0L &&
                        Query(db, "SELECT revision||':'||name FROM account_adventures") is "7:LegacyGroup", "只追加空代表表，不伪造旧角色选择");
                }
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }
    static SqliteConnection Open(string path) { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open(); return db; }
    static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    static object? Query(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
}
