using System.Collections.Immutable;
using DfoServer.Game.Adventure;
using DfoServer.Game.Accounts;
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

internal static class AdventureExpeditionTests
{
    internal static AdventureExpeditionCatalog SyntheticCatalog()
    {
        var strings = new List<string>();
        int Add(string text) { strings.Add(text); return strings.Count - 1; }
        PvfToken Token(object value) => value switch
        {
            int number => new(0, number),
            float number => new(2, BitConverter.SingleToInt32Bits(number)),
            string text => new(text.StartsWith('[') && text != "[A]" ? (byte)3 : (byte)6, Add(text)),
            _ => throw new InvalidDataException()
        };
        PvfToken[] Document(object[] values) => values.Select(Token).ToArray();
        var expedition = Document([
            "[area info]", "[area]", "[index]", 1, "[required level]", 1, "[designated attribute]", "[A]", "[/designated attribute]",
            "[reward info]", 6, 1.9f, "[/reward info]", "[/area]", "[/area info]",
            "[reward bonus info]", "[extra rate]", 0.5f, "[party member rate]", 1, 1, 1, 1, 1,
            "[attribute achieve rate]", 1, 1, 1.4f, 1.8f, 2.2f, "[/reward bonus info]",
            "[area attribute rotation period]", 1, "[attribute data]", 1, "[A]", "", 0, 1, "Alias", "group", 50,
            "[attribute group]", "[group]", "group", "[A]", "[/group]", "[/attribute group]",
            "[character attribute]", 0, 1, 2, "[A]", "[/character attribute]"]);
        return new(_ => expedition, id => strings[id]);
    }
    public static void RunCore()
    {
        int before = Count;
        var catalog = AdventureCoreTests.SyntheticCatalog();
        var expeditions = SyntheticCatalog();
        var service = new AdventureExpeditionService(catalog, expeditions);
        var now = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
        var member = new AdventureMember(1, 0, "Hero", 0, 33, 95, 7, 0);
        var snapshot = new AdventureSnapshot(AdventureState.Initial("exp", now), [member]);
        var started = service.Start(snapshot, 1, 21600, [(0, 0, 1)], now);
        That(started is { Plan: not null, Error: 0 } && started.Plan.After.Expeditions.Single().Reward == 1,
            "远征按float32四舍五入再乘extra rate，不按角色等级放大");
        var run = started.Plan!.After.Expeditions.Single();
        That(run.FrozenMembers is [{ Id: 1, Selector: 0, Name: "Hero" }] && run.Attributes is [1],
            "出发冻结真实UID、selector、名字及属性");
        var running = snapshot with { State = started.Plan.After };
        That(service.Start(running, 1, 21600, [(0, 0, 1)], now).Error == 14 &&
            service.Start(snapshot, 1, 21600, [(1, 0, 1)], now).Error == 22 &&
            service.Finish(running, 1, true, now).Error == 3, "运行地区、错误视图索引及未到期领取拒绝");
        That(new AdventureRepresentativeService(catalog).Busy(running, 0, 1) &&
            new AdventureRepresentativeService(catalog).Prepare(running, 1, 0, 1, null) is null, "远征中的代表不能解除或更换");
        var finish = service.Finish(running, 1, true, now.AddHours(6));
        That(finish.Reward == 1 && finish.Plan!.After.Expeditions.IsEmpty && finish.Plan.After.Points[1] == 1,
            "到期仅领取冻结结晶，成员释放同一计划");
        var previousMonth = snapshot.State with { Points = [20, 40, 0, 0, 0] };
        var nextMonth = AdventurePeriod.Apply(previousMonth, catalog, now.AddMonths(1));
        That(nextMonth.Points is [20, 0, 0, 0, 0] && nextMonth.Month == 202611 && nextMonth.Revision == previousMonth.Revision &&
            ReferenceEquals(AdventurePeriod.Apply(nextMonth, catalog, now), nextMonth), "月重置只投影声明的结晶，不改revision或倒退月份");
        AdventureRequests.Information([1, 1]);
        Reject<InvalidDataException>(() => AdventureRequests.Information([2, 1, 1]), "重复地区查询拒绝");
        var empty = AdventureRequests.Start([1, 0x80, 0x51, 1, 0, 0, 0]);
        That(empty is { Area: 1, Duration: 86400 } && empty.Members.IsEmpty &&
            service.Start(snapshot, empty.Area, 21600, empty.Members, now) is { Plan: null, Error: 22 },
            "原生空成员请求结构合法，由远征业务拒绝，不发放奖励或创建运行记录");
        Reject<InvalidDataException>(() => AdventureRequests.Start([1, 0x80, 0x51, 1, 0, 1, 0]), "成员行缺失仍是包结构错误");
        Reject<InvalidDataException>(() => AdventureRequests.Start([1, 0x80, 0x51, 1, 0, 0, 0, 0]), "空成员请求不接受额外行字节");
        Reject<InvalidDataException>(() => AdventureRequests.Start([1, 0, 0, 0, 0, 5, 0]), "远征成员超过四人拒绝");
        byte[] body = AdventurePackets.Expedition(running, expeditions, running.Representatives(catalog),
            expeditions.Areas.Keys.ToDictionary(area => area, area => service.AreaAttributes(area, now).ToArray()), now);
        That(body[0] == 1 && body[1] == 2 && body[2] == 0 && BitConverter.ToUInt16(body, 13) == 0 &&
            body[15] == 1 && body[16] == 1, "远征表必须带area0空闲组，运行成员不重复列入空闲表");
        Persist(catalog, service, now);
        Migrate();
        Console.WriteLine($"PASS {Count - before} 远征冻结、公式、资格、SQL原子性及领取断言");
    }

    static void Persist(AdventureCatalog catalog, AdventureExpeditionService service, DateTimeOffset now)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-expedition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            new AuthenticationService(new AuthenticationRepository(path)).Create("exp", "fixture-password");
            using var owner = new AccountOwners().Acquire("exp", () => { });
            var member = repo.Create(owner, new("Hero", new(1, 0, 10, 10), null) { GrowType = 33, Level = 95 });
            repo.InitializeAdventure(owner, catalog, now);
            var snapshot = repo.ReadAdventure(owner, catalog, now);
            var plan = service.Start(snapshot, 1, 21600, [(0, 0, 1)], now).Plan! with { ActorId = member.Id };
            using var db = Open(path);
            Execute(db, "CREATE TRIGGER reject_exp_member BEFORE INSERT ON account_adventure_expedition_members BEGIN SELECT RAISE(ABORT,'late failure'); END");
            Reject<SqliteException>(() => repo.CommitAdventureExpedition(owner, plan, catalog, now), "冻结成员晚期插入失败整笔回滚");
            That(Query(db, "SELECT revision FROM account_adventures") is 0L && Query(db, "SELECT count(*) FROM account_adventure_expeditions") is 0L,
                "失败不留下账号版本或无成员远征");
            Execute(db, "DROP TRIGGER reject_exp_member");
            var running = repo.CommitAdventureExpedition(owner, plan, catalog, now);
            Reject<InvalidOperationException>(() => repo.CommitAdventureExpedition(owner, plan, catalog, now), "旧出发计划不能重复登记");
            That(Query(db, "SELECT character_id FROM account_adventure_expedition_members") is long id && id == member.Id,
                "冻结成员外键使用本账号真实角色");
            var busy = repo.LoadDeletionTarget(owner, member.Slot);
            That(busy.AdventureBusy, "删除名册身份携带真实远征占用，不依据角色等级猜测忙碌");
            Reject<InvalidDataException>(() => repo.Delete(owner, busy), "正式角色删除拒绝尚未释放的远征成员");
            Reject<SqliteException>(() => Execute(db, "DELETE FROM characters WHERE id=" + member.Id), "远征成员不能被删掉归属外键");
            var finish = service.Finish(running, 1, true, now.AddHours(6)).Plan! with { ActorId = member.Id };
            var saved = repo.CommitAdventureExpedition(owner, finish, catalog, now.AddHours(6));
            That(saved.State.Points[1] == 1 && saved.State.Expeditions.IsEmpty &&
                Query(db, "SELECT count(*) FROM account_adventure_receipts WHERE action='expedition_claim'") is 1L,
                "奖励、唯一领取凭据及成员释放同一事务");
            Reject<InvalidOperationException>(() => repo.CommitAdventureExpedition(owner, finish, catalog, now.AddHours(6)), "旧领取不能再发一份");
            That(new CharacterRepository(path).ReadAdventure(owner, catalog, now.AddHours(6)).State.Points[1] == 1 &&
                Query(db, "PRAGMA integrity_check") is "ok" && Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "重开保留一次奖励，外键完整");
            long receipts = (long)Query(db, "SELECT count(*) FROM account_adventure_receipts")!;
            repo.Delete(owner, repo.LoadDeletionTarget(owner, member.Slot));
            That(Query(db, "SELECT count(*) FROM characters") is 0L &&
                Query(db, "SELECT count(*) FROM account_adventure_receipts") is long remaining && remaining == receipts &&
                Query(db, "SELECT count(*) FROM account_adventure_receipts WHERE character_id IS NOT NULL") is 0L &&
                new CharacterRepository(path).ReadAdventure(owner, catalog, now.AddHours(6)).State.Points[1] == 1 &&
                Query(db, "SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "远征已领取后允许删除角色，保留账号奖励与全部凭据身份，仅解除角色来源");
        }
        finally { Directory.Delete(root, true); }
    }
    static void Migrate()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-exp-migration-" + Guid.NewGuid().ToString("N"));
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
                    DatabaseSchemaTests.RemoveAdventureExpeditions(db);
                    Execute(db, "PRAGMA user_version=30; INSERT INTO account_adventures VALUES('legacy',7,'LegacyGroup',20261011,1,0,'0',0,0,zeroblob(20),202610); " +
                        "INSERT INTO account_adventure_representatives VALUES('legacy',0,1,NULL)");
                    if (fail) Execute(db, "CREATE VIEW account_adventure_receipts AS SELECT 1 AS collision");
                }
                if (fail)
                {
                    Reject<SqliteException>(() => new CharacterRepository(path), "v31最后凭据表撞名回滚全部远征结构");
                    using var db = Open(path);
                    That(Query(db, "PRAGMA user_version") is 30L && Query(db,
                        "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN('account_adventure_expeditions','account_adventure_expedition_members')") is 0L,
                        "迁移失败不留下前置远征表或推进版本");
                }
                else _ = new CharacterRepository(path);
                using var reopened = Open(path);
                That(Query(reopened, "SELECT revision||':'||name FROM account_adventures") is "7:LegacyGroup" &&
                    Query(reopened, "SELECT count(*) FROM account_adventure_representatives WHERE character_id IS NULL") is 1L,
                    "v31增量或回滚均保留账号名称版本和已解除代表");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    static SqliteConnection Open(string path) { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open(); return db; }
    static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    static object? Query(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
}
