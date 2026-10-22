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
            "[area attribute rotation period]", 1, "[attribute data]", 1, "[A]", "", 0, 255, "Alias", "group", 50,
            "[attribute group]", "[group]", "group", "[A]", "[/group]", "[/attribute group]",
            "[character attribute]", 0, 1, 2, "[A]", "[/character attribute]"]);
        return new(_ => expedition, id => strings[id]);
    }

    // 轮换用例：地区1的指定属性是组别名，地区2是具体属性，地区3用于冒险团等级门禁。
    static AdventureExpeditionCatalog RotationCatalog()
    {
        var strings = new List<string>();
        int Add(string text) { strings.Add(text); return strings.Count - 1; }
        // 真实资源里属性名与组名是字符串token；这里只把未列入文本的[]标签当作块标记。
        bool Text(string text) => text is "[C]" or "[D]" or "[group C]" or "[GROUP C ALLOW]";
        PvfToken Token(object value) => value switch
        {
            int number => new(0, number),
            float number => new(2, BitConverter.SingleToInt32Bits(number)),
            string text => new(text.StartsWith('[') && !Text(text) ? (byte)3 : (byte)6, Add(text)),
            _ => throw new InvalidDataException()
        };
        PvfToken[] Document(object[] values) => values.Select(Token).ToArray();
        var expedition = Document([
            "[area info]",
            "[area]", "[index]", 1, "[required level]", 1, "[designated attribute]", "[GROUP C ALLOW]", "[/designated attribute]",
            "[reward info]", 6, 1.9f, "[/reward info]", "[/area]",
            "[area]", "[index]", 2, "[required level]", 1, "[designated attribute]", "[C]", "[/designated attribute]",
            "[reward info]", 6, 1.9f, "[/reward info]", "[/area]",
            "[area]", "[index]", 3, "[required level]", 10, "[designated attribute]", "[C]", "[/designated attribute]",
            "[reward info]", 6, 1.9f, "[/reward info]", "[/area]",
            "[/area info]",
            "[reward bonus info]", "[extra rate]", 0.5f, "[party member rate]", 1, 1, 1, 1, 1,
            "[attribute achieve rate]", 1, 1, 1.4f, 1.8f, 2.2f, "[/reward bonus info]",
            "[area attribute rotation period]", 1,
            "[attribute data]", 20, "[C]", "", 0, 21, "[D]", "", 0, 255, "[GROUP C ALLOW]", "[group C]", 50,
            "[attribute group]", "[group]", "[group C]", "[C]", "[D]", "[/group]", "[/attribute group]",
            "[character attribute]", 0, 1, 2, "[C]", "[/character attribute]"]);
        return new(_ => expedition, id => strings[id]);
    }

    // 并行用例：两名不同职业的代表，用于两个地区同时出发与落库。
    static AdventureCatalog ParallelCatalog()
    {
        var strings = new List<string>();
        int Add(string text) { strings.Add(text); return strings.Count - 1; }
        PvfToken Token(object value) => value switch
        {
            int integer => new(0, integer),
            float number => new(2, BitConverter.SingleToInt32Bits(number)),
            string text => new(text.StartsWith('[') && text is not ("[swordman]" or "[fighter]") ? (byte)3 : (byte)6, Add(text)),
            _ => throw new InvalidDataException()
        };
        PvfToken[] Document(object[] values) => values.Select(Token).ToArray();
        var main = Document([
            "[max adventure level]", 3, "[adventure exp rate]", 0.3f,
            "[adventure exp table]", 1, "0", 2, "10", 3, "20", 4, "30", "[/adventure exp table]",
            "[attribute list]", "[/attribute list]",
            "[expert list]", "[group]", 0, "profession",
            "[info]", "[swordman]", "second awakening", 1, 0, 0, 1,
            "[info]", "[fighter]", "second awakening", 1, 0, 0, 1,
            "[/group]", "[/expert list]",
            "[adventurer shop list]", "[shop]", "[index]", 0, "[max shop point]", 250, "[exp to get shop point]", 5, 1,
            "[adventurer shop purchase info]", "[/adventurer shop purchase info]", "[/shop]",
            "[shop]", "[index]", 1, "[max shop point]", 9999, "[reset point]", "[adventurer shop purchase info]", "[/adventurer shop purchase info]", "[/shop]",
            "[/adventurer shop list]", "[capsule need minimum exp]", 10, "[capsule max exp]", 100, "[capsule max count]", 2,
            "[capsule limit level min]", 2, "[capsule limit level max]", 90, "[capsule give exp]", 50,
            "[capsule update date]", "2018-02-22 06:00:00"]);
        return new(_ => main, id => strings[id]);
    }

    public static void RunCore()
    {
        int before = Count;
        var catalog = AdventureCoreTests.SyntheticCatalog();
        var expeditions = SyntheticCatalog();
        var service = new AdventureExpeditionService(catalog, expeditions);
        That(expeditions.Attributes["[A]"] == new ExpeditionAttribute("[A]", "", 1) &&
            expeditions.Attributes["Alias"] == new ExpeditionAttribute("Alias", "group", 255),
            "远征属性按线上ID、名称、组名、图片四项解析，组别名保留255");
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
        Rotation(catalog);
        Parallel();
        ParallelPersist();
        Console.WriteLine($"PASS {Count - before} 远征冻结、公式、资格、属性轮换、并行地区、SQL原子性及领取断言");
    }

    static void Rotation(AdventureCatalog catalog)
    {
        var service = new AdventureExpeditionService(catalog, RotationCatalog());
        var noon = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(8));
        var before = new DateTimeOffset(2026, 10, 11, 5, 59, 0, TimeSpan.FromHours(8));
        var boundary = new DateTimeOffset(2026, 10, 11, 6, 0, 0, TimeSpan.FromHours(8));
        var previous = service.AreaAttributes(1, noon);
        // 06:00前仍属于上一周期，属性与前一天中午一致；06:00切换后候选翻转，组别名从不直接下发255。
        That(previous is [>= 20 and <= 21] && service.AreaAttributes(1, before).SequenceEqual(previous) &&
            service.AreaAttributes(1, boundary) is [>= 20 and <= 21] &&
            !service.AreaAttributes(1, boundary).SequenceEqual(previous),
            "远征属性按UTC+8的06:00切换，组别名展开为具体ID");
        That(service.AreaAttributes(2, boundary).SequenceEqual(ImmutableArray.Create((byte)20)),
            "普通指定属性直接下发线上ID");
        var snapshot = new AdventureSnapshot(AdventureState.Initial("exp", boundary),
            [new AdventureMember(1, 0, "Hero", 0, 33, 95, 0, 0)]);
        That(service.Start(snapshot, 3, 21600, [(0, 0, 1)], boundary).Error == 7,
            "远征地区按冒险团等级解锁，不足时拒绝出发");
    }

    static void Parallel()
    {
        var catalog = ParallelCatalog();
        var expeditions = RotationCatalog();
        var service = new AdventureExpeditionService(catalog, expeditions);
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(8));
        var members = ImmutableArray.Create(new AdventureMember(1, 0, "Sword", 0, 33, 95, 0, 0),
            new AdventureMember(2, 1, "Fighter", 1, 33, 95, 0, 0));
        var snapshot = new AdventureSnapshot(AdventureState.Initial("exp", now), members);
        var first = service.Start(snapshot, 1, 21600, [(0, 0, 1)], now);
        That(first is { Plan: not null, Error: 0 } && first.Plan.After.Expeditions is [{ Area: 1 }],
            "地区1由剑士代表出发");
        var running = snapshot with { State = first.Plan!.After };
        That(service.Start(running, 2, 21600, [(0, 0, 1)], now) is { Plan: null, Error: 22 } &&
            service.Start(running, 1, 21600, [(0, 0, 1)], now).Error == 14,
            "同一成员不能同时派往第二个地区，同一地区不能重复出发");
        var second = service.Start(running, 2, 21600, [(1, 1, 1)], now);
        That(second is { Plan: not null, Error: 0 } && second.Plan.After.Expeditions is [{ Area: 1 }, { Area: 2 }],
            "两名代表可分别派往两个地区并行远征");
        var both = running with { State = second.Plan!.After };
        var claim = service.Finish(both, 1, true, now.AddHours(6));
        That(claim is { Plan: not null, Reward: 1 } && claim.Plan.After.Expeditions is [{ Area: 2 }] && claim.Plan.After.Points[1] == 1,
            "领取地区1只结算该地区，地区2保持运行");
        var other = service.Finish(both with { State = claim.Plan!.After }, 2, true, now.AddHours(6));
        That(other is { Plan: not null, Reward: 1 } && other.Plan.After.Expeditions.IsEmpty && other.Plan.After.Points[1] == 2,
            "地区2按自己的冻结奖励独立领取");
    }

    static void ParallelPersist()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-exp-parallel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var catalog = ParallelCatalog();
            var service = new AdventureExpeditionService(catalog, RotationCatalog());
            var repo = new CharacterRepository(path);
            new AuthenticationService(new AuthenticationRepository(path)).Create("parallel", "fixture-password");
            using var owner = new AccountOwners().Acquire("parallel", () => { });
            var sword = repo.Create(owner, new("Sword", new(1, 0, 10, 10), null) { GrowType = 33, Level = 95 });
            var fighter = repo.Create(owner, new("Fighter", new(1, 0, 10, 10), null) { Job = 1, GrowType = 33, Level = 95 });
            var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(8));
            repo.InitializeAdventure(owner, catalog, now);
            var snapshot = repo.ReadAdventure(owner, catalog, now);
            var first = service.Start(snapshot, 1, 21600, [(0, 0, 1)], now).Plan! with { ActorId = sword.Id };
            var saved = repo.CommitAdventureExpedition(owner, first, catalog, now);
            var second = service.Start(saved, 2, 21600, [(1, 1, 1)], now).Plan! with { ActorId = fighter.Id };
            saved = repo.CommitAdventureExpedition(owner, second, catalog, now);
            using var db = Open(path);
            That(saved.State.Expeditions.Length == 2 &&
                Query(db, "SELECT count(*) FROM account_adventure_expeditions") is 2L &&
                Query(db, "SELECT count(DISTINCT character_id) FROM account_adventure_expedition_members") is 2L,
                "两个地区并行落库，各自保留冻结成员");
            var reopened = new CharacterRepository(path).ReadAdventure(owner, catalog, now);
            That(reopened.State.Expeditions.Select(run => run.Area).SequenceEqual(new byte[] { 1, 2 }) &&
                reopened.State.Expeditions.SelectMany(run => run.Members).Distinct().Count() == 2,
                "重开库恢复两个并行地区");
        }
        finally { Directory.Delete(root, true); }
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
