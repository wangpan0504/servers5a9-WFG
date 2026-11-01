using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildHallWaitTests
{
    const string Resource = "[guild member ranking factor]\n[guild agit wait]\n72 3 300\n[/guild member ranking factor]\n";

    public static void RunCore()
    {
        int before = Count;
        ClockRules();
        Transactions();
        SessionBoundaries();
        Console.WriteLine($"PASS {Count - before} 公会大厅自然等待、业务日边界、会籍与持久日上限断言");
    }

    sealed class Clock(DateTimeOffset origin) : TimeProvider
    {
        long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => origin.AddTicks(ticks);
        public void Advance(int seconds) => ticks += seconds * TimeSpan.TicksPerSecond;
    }

    static void ClockRules()
    {
        var rules = new GuildHallWaitCatalog(Resource);
        That(rules is { DailyMaximum: 72, Points: 3, IntervalSeconds: 300 }, "等待规则从本版资源结构实际读取72/3/300");
        foreach (string bad in new[] { "", Resource + Resource, Resource.Replace("72 3 300", "72 3 300 1"),
            Resource.Replace("72 3 300", "0 3 300"), Resource.Replace("72 3 300", "72 0 300"),
            Resource.Replace("72 3 300", "72 3 0"), Resource.Replace("72 3 300", "72 3 nope"),
            Resource.Replace("[guild agit wait]", "[guild agit wait]\n72 3 300\n[guild agit wait]") })
            Reject<InvalidDataException>(() => new GuildHallWaitCatalog(bad), "等待配置缺失、重复和非法值不得默认放行");
        var time = new Clock(new(2026, 10, 29, 5, 55, 0, TimeSpan.FromHours(8)));
        var state = new GuildHallWaitClock(new(1, 2, 1000), time);
        That(state.Prepare(rules) is null, "首次自然观察不立即发贡献");
        time.Advance(299);
        That(state.Prepare(rules) is null, "实际299秒不足一个300秒周期");
        time.Advance(1);
        That(state.Prepare(rules) is null, "六点边界不得把旧日300秒挪到新日发奖");
        time.Advance(300);
        var first = state.Prepare(rules)!;
        That(first is { Periods: 1 } && first.BusinessDay == GuildAttendanceDay.Start(time.GetUtcNow().ToUnixTimeSeconds()),
            "新业务日满300秒才生成当前日等待事件");
        time.Advance(300);
        That(ReferenceEquals(first, state.Prepare(rules)), "SQL失败后的重试保持原事件身份和原周期数");
        state.Complete(first, new(new(3), 0), rules);
        That(ReferenceEquals(first, state.Prepare(rules)), "失败回执不能消费已实际等待周期");
        state.Complete(first, new(new(0, Points: 3), 3), rules);
        var next = state.Prepare(rules)!;
        That(next.Periods == 1 && next.EventId != first.EventId, "成功提交后只消费原周期，后续实际等待独立去重");
        state.Complete(next, new(new(0, Points: 3), 72), rules);
        time.Advance(10000);
        That(state.Prepare(rules) is null, "达到资源日上限后停止生成写入请求");
        time.Advance(86400);
        That(state.Prepare(rules) is { BusinessDay: var day } && day != first.BusinessDay,
            "持续在线跨日只按新日实际经过的时间重新计时");
        var shortTime = new Clock(new(2026, 10, 29, 5, 59, 50, TimeSpan.FromHours(8)));
        var shortState = new GuildHallWaitClock(new(1, 2, 1000), shortTime);
        shortTime.Advance(309);
        That(shortState.Prepare(rules) is null, "跨日不足300秒部分不借旧日10秒补齐");
        shortTime.Advance(1);
        That(shortState.Prepare(rules) is { Periods: 1 }, "六点起完整300秒可正常记贡献");
    }

    sealed class Fixture : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), "guild-wait-" + Guid.NewGuid().ToString("N") + ".db");
        public CharacterRepository Repository { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public Character Member { get; }
        public SqliteConnection Database { get; }
        public Location Hall { get; } = new(70, 0, 100, 100);
        public Location Outside { get; } = new(1, 0, 100, 100);
        public TownCatalog Towns { get; }
        public Clock Time { get; } = new(new(2026, 10, 29, 12, 0, 0, TimeSpan.FromHours(8)));
        public GuildHallWaitCatalog Rules { get; } = new(Resource);
        public GuildManagementIdentity Actor { get; }
        public Fixture()
        {
            Repository = new(path);
            Owner = Owners.Acquire("guild-wait", static () => { });
            Member = Repository.Create(Owner, new("WaitMember", Hall, Outside));
            var bounds = new[] { new WalkRegion(new(0, 0, 1000, 1000), [], []) };
            TownArea Area(Location place, string map, Location target) => new(place.Town, place.Area, 0, [], [], [], null,
                new(map, bounds, [new(new(0, 0, 1000, 1000), target.Town, target.Area, [], [])], false, []), [], []);
            Towns = new(new Dictionary<(byte, byte), TownArea>
            {
                [(Hall.Town, Hall.Area)] = Area(Hall, "map/guildagit_2019/guildagit_inside.map", Outside),
                [(Outside.Town, Outside.Area)] = Area(Outside, "map/village/normal.map", Hall)
            }, Outside, Outside, new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            Database = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
            Database.Open();
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                VALUES(1,'Wait guild','wait',{Member.Id},1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{Member.Id},1,1000)
                """);
            Actor = new(new GameSession(Repository, Towns, Owner), Owner, Member.Id, Member.Name, 1, new(2, 10), () => Owner.IsCurrent);
        }
        public long Now => Time.GetUtcNow().ToUnixTimeSeconds();
        public GuildHallWaitCredit Credit(string suffix, int periods = 1) => new(new(Member.Id, 1, 1000),
            $"hall:{Member.Id}:{suffix}", GuildAttendanceDay.Start(Now), periods);
        public void Sql(string sql) { using var c = Database.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        public long Number(string sql) { using var c = Database.CreateCommand(); c.CommandText = sql; return Convert.ToInt64(c.ExecuteScalar()); }
        public long Points => Number("SELECT COALESCE(SUM(points),0) FROM guild_contribution_events WHERE source=7");
        public void Dispose() { Database.Dispose(); Owner.Dispose(); SqliteConnection.ClearAllPools(); TestFiles.DeleteFile(path); }
    }

    static void Transactions()
    {
        using var f = new Fixture();
        var repository = f.Repository;
        var first = f.Credit("first");
        That(repository.ReadGuildHallWaitMembership(f.Actor) == first.Membership, "计时只冻结存储中当前真实公会与入会时间");
        f.Sql("CREATE TRIGGER wait_abort AFTER INSERT ON guild_contribution_events BEGIN SELECT RAISE(ABORT,'wait rollback'); END");
        Reject<SqliteException>(() => repository.CreditGuildHallWait(f.Actor, first, f.Rules, f.Now), "等待事件写失败真实抛出异常");
        That(f.Points == 0 && f.Number("SELECT contribution FROM guild_members") == 0,
            "等待账本失败连同累计贡献更新一起回滚");
        f.Sql("DROP TRIGGER wait_abort");
        var paid = repository.CreditGuildHallWait(f.Actor, first, f.Rules, f.Now);
        That(paid.Contribution.Points == 3 && repository.CreditGuildHallWait(f.Actor, first, f.Rules, f.Now).Contribution.Points == 0 &&
            f.Points == 3, "相同永久事件重试只到账一次");
        int currentCalls = 0;
        var stale = f.Actor with { IsCurrent = () => ++currentCalls == 1 };
        That(repository.CreditGuildHallWait(stale, f.Credit("stale"), f.Rules, f.Now).Contribution.Error == 3 && f.Points == 3,
            "提交末端失去会话身份时整笔等待贡献回滚");
        var wrongJoined = f.Credit("wrong-join") with { Membership = new(f.Member.Id, 1, 999) };
        That(!repository.CreditGuildHallWait(f.Actor, wrongJoined, f.Rules, f.Now).MembershipCurrent && f.Points == 3,
            "旧会籍计时不能给重入后的会籍发贡献");
        var capped = repository.CreditGuildHallWait(f.Actor, f.Credit("to-cap", 24), f.Rules, f.Now);
        That(capped is { DailyPoints: 72, Contribution.Points: 69 } && f.Points == 72,
            "批量到期仍按已持久化日余额钉住72点，不能超发");
        f.Sql($"UPDATE guild_members SET joined_at_unix=2000,contribution=0 WHERE character_id={f.Member.Id}");
        var rejoined = f.Credit("rejoin") with { Membership = new(f.Member.Id, 1, 2000) };
        That(repository.CreditGuildHallWait(f.Actor, rejoined, f.Rules, f.Now).DailyPoints == 72 &&
            f.Number("SELECT contribution FROM guild_members") == 0, "退会重入不重置角色业务日72点上限");
        f.Sql($"""
            DELETE FROM guilds WHERE guild_id=1;
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
            VALUES(2,'Wait guild two','wait2',{f.Member.Id},2000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(2,{f.Member.Id},1,2000)
            """);
        var recreated = rejoined with { Membership = new(f.Member.Id, 2, 2000), EventId = $"hall:{f.Member.Id}:recreated" };
        That(repository.CreditGuildHallWait(f.Actor, recreated, f.Rules, f.Now).DailyPoints == 72 && f.Points == 72 &&
            f.Number("SELECT COUNT(*) FROM guild_contribution_events WHERE guild_id IS NULL AND source=7") == 2,
            "解散后零公会外键事件仍保留角色日额度，重建公会不能刷上限");
        f.Time.Advance(86400);
        var newDay = recreated with { BusinessDay = GuildAttendanceDay.Start(f.Now), EventId = $"hall:{f.Member.Id}:next-day" };
        That(repository.CreditGuildHallWait(f.Actor, newDay, f.Rules, f.Now).Contribution.Points == 3 && f.Points == 75,
            "六点业务日更新后独立获得新日贡献");
        Reject<InvalidDataException>(() => repository.CreditGuildHallWait(f.Actor, recreated, f.Rules, f.Now),
            "旧日pending请求不能跨日重放成新日收入");
    }

    static void SessionBoundaries()
    {
        using var f = new Fixture();
        var session = new GameSession(f.Repository, f.Towns, f.Owner, timeProvider: f.Time);
        session.Select(f.Member.Slot, resumeTown: true);
        session.MarkReady();
        GuildContributionCommit? Tick() => session.TickGuildHallWait(f.Repository, f.Rules, f.Time);
        That(Tick() is null && f.Points == 0, "真实大厅就绪后首次自然观察只开始计时，不由登录或查询给分");
        f.Time.Advance(299);
        That(session.Move(new(110, 110, 0, 0, 0, 0)) && Tick() is null, "大厅内正常走动不重置连续等待，也不提前发分");
        f.Time.Advance(1);
        That(Tick() is { Points: 3 } && f.Points == 3, "真实大厅满300秒自然到账3点");
        f.Time.Advance(299);
        That(session.Travel(new(1, 0, 100, 100, 0, 70, 0, 0)), "测试通过实际有向城镇路线离开公会大厅");
        f.Time.Advance(600);
        That(Tick() is null && f.Points == 3 && session.Travel(new(70, 0, 100, 100, 0, 1, 0, 0)),
            "普通城镇停留不算公会等待，正常返回大厅不补离厅时间");
        Tick(); f.Time.Advance(299);
        That(Tick() is null, "离厅前299秒不能带回大厅补足新周期");
        f.Time.Advance(1);
        That(Tick() is { Points: 3 } && f.Points == 6, "重新进入大厅需要新一轮实际300秒");
        f.Time.Advance(299);
        session.ReturnToRoster();
        f.Time.Advance(600);
        That(Tick() is null && f.Points == 6, "选角与离线阶段不积累等待时间");
        session.Select(f.Member.Slot, resumeTown: true); session.MarkReady(); Tick();
        f.Time.Advance(299);
        That(Tick() is null, "重登不恢复上一次未满的等待周期");
        f.Time.Advance(1);
        That(Tick() is { Points: 3 } && f.Points == 9, "重登后的新连续周期只发一次贡献");
        f.Time.Advance(300);
        f.Sql($"UPDATE guild_members SET joined_at_unix={f.Now},contribution=0 WHERE character_id={f.Member.Id}");
        That(Tick() is { Points: 0 } && f.Points == 9, "同会重入的joinedAt改变也会拒绝旧连续区间");
        Tick(); f.Time.Advance(300);
        That(Tick() is { Points: 3 } && f.Points == 12, "新会籍从新的自然观察重新计时");
        using var replacement = f.Owners.Acquire(f.Owner.Account, static () => { });
        f.Time.Advance(300);
        That(Tick() is null && f.Points == 12, "owner被接管后旧会话计时不能写库");
    }
}
