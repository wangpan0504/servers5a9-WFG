using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Events;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ServerEventTests
{
    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VerifyEpicRoad(Path.Combine(root, "epic-road.db"));
            string path = Path.Combine(root, "game.db");
            var clock = new ConsumableUseSessionTests.FakeClock();
            var now = clock.GetUtcNow();
            var store = new ServerEventRepository(path);
            var events = new ServerEventService(store, clock);
            const ushort strongway = 312;
            VerifyRegisteredDefaults(root, clock);
            VerifyStrongwayDefault(root);
            VerifyBattlePassDefault(root);
            foreach (ushort id in new ushort[] { 2037, 2378, 2395, 2257, 228, 2370, 2208 })
            {
                var defaultState = events.Read(id);
                That(defaultState.Active, "新增独立开关沿用原默认开启状态");
                var off = events.Override(id, false);
                That(!off.Active && !new DfoServer.Game.Inventory.Models.InventoryEventPrecondition(defaultState.Schedule, clock).Matches(off.Schedule),
                    "关闭使原库存规划失效");
                That(new ServerEventService(new ServerEventRepository(path), clock).Read(id) == off,
                    "独立关闭在重开仓储后保持");
                That(events.Override(id, null).Active, "自动模式恢复原默认开启");
            }
            var goldBeforeStrongway = store.Read(64);
            ServerEventState? strongwayInitial = null;
            try { strongwayInitial = events.Read(strongway); }
            catch (InvalidDataException) { }
            That(strongwayInitial is { Active: true, Schedule: { EventId: strongway, Revision: 0, StartsAt: null, EndsAt: null, OverrideActive: null } } &&
                store.Read(strongway) == new ServerEventSchedule(strongway),
                "强者之路312已登记且未配置默认开启，只读不补写活动版本");
            var strongwayOn = DevelopmentRequest.Parse("event on 312", "10000").Event!.Execute(events);
            That(strongwayOn is { Active: true, Schedule: { Revision: 1, OverrideActive: true } } &&
                DevelopmentRequest.Parse("event on 312", "10000").Event!.Execute(events) == strongwayOn,
                "312 GM on立即生效，同值开启不增加revision");
            var strongwayOff = DevelopmentRequest.Parse("event off 312", "10000").Event!.Execute(events);
            That(strongwayOff is { Active: false, Schedule: { Revision: 2, OverrideActive: false } },
                "312 GM off关闭活动并提交独立revision");
            var strongwayAuto = DevelopmentRequest.Parse("event auto 312", "10000").Event!.Execute(events);
            That(strongwayAuto is { Active: true, Schedule: { Revision: 3, OverrideActive: null } },
                "312没有时间表时auto恢复默认开启");
            var strongwayScheduled = DevelopmentRequest.Parse($"event schedule 312 {now.AddSeconds(1):yyyy-MM-ddTHH:mm:sszzz} {now.AddSeconds(3):yyyy-MM-ddTHH:mm:sszzz}", "10000").Event!.Execute(events);
            That(!strongwayScheduled.Active && strongwayScheduled.Schedule.Revision == 4,
                "312未来时间表持久提交revision，开始前不激活");
            clock.Advance(TimeSpan.FromSeconds(1));
            That(events.Read(strongway).Active, "312时间表开始端点有效");
            clock.Advance(TimeSpan.FromSeconds(2));
            That(!events.Read(strongway).Active, "312时间表结束端点关闭");
            var strongwayReopened = new ServerEventRepository(path);
            That(new ServerEventService(strongwayReopened, clock).Read(strongway) == strongwayScheduled &&
                store.Read(64) == goldBeforeStrongway,
                "312服务与仓储重建恢复时间表及revision，不修改黄金牌64");
            Reject<InvalidOperationException>(() => strongwayReopened.Save(strongwayOn.Schedule,
                strongwayOn.Schedule with { OverrideActive = false }), "312旧revision不能覆盖已提交时间表");
            now = clock.GetUtcNow();
            string mileagePath = Path.Combine(root, "mileage-permanent.db");
            var mileageClock = new ConsumableUseSessionTests.FakeClock();
            var mileageStore = new ServerEventRepository(mileagePath);
            var mileageEvents = new ServerEventService(mileageStore, mileageClock);
            var permanent = mileageEvents.Read(ServerEventService.MileageShopId);
            That(permanent is { Active: true, Schedule: { Revision: 0, OverrideActive: null, StartsAt: null, EndsAt: null } } &&
                permanent.Schedule.IsActive(mileageClock.GetUtcNow()), "积分商城无配置默认常驻，服务与库存事务使用同一有效状态");
            var permanentGuard = new DfoServer.Game.Inventory.Models.InventoryEventPrecondition(permanent.Schedule, mileageClock);
            mileageClock.Advance(TimeSpan.FromDays(800));
            That(mileageEvents.Read(266) == permanent && permanentGuard.Matches(mileageStore.Read(266)),
                "默认积分商城跨日跨年仍有效，不追加时段或活动版本");
            var mileageOff = mileageEvents.Override(266, false);
            That(!mileageOff.Active && !permanentGuard.Matches(mileageOff.Schedule) &&
                new ServerEventService(new ServerEventRepository(mileagePath), mileageClock).Read(266) == mileageOff,
                "显式关闭持久恢复且旧活动前置不能因默认常驻绕过版本和off");
            var mileageAuto = mileageEvents.Override(266, null);
            That(mileageAuto.Active && new ServerEventService(new ServerEventRepository(mileagePath), mileageClock).Read(266) == mileageAuto,
                "无时间表的auto恢复积分商城默认常驻并保存原版本");
            var mileageNow = mileageClock.GetUtcNow();
            mileageEvents.Schedule(266, mileageNow.AddSeconds(1), mileageNow.AddSeconds(3));
            That(!mileageEvents.Read(266).Active, "显式积分时间表尚未开始时保持关闭");
            mileageClock.Advance(TimeSpan.FromSeconds(1));
            That(mileageEvents.Read(266).Active, "显式积分时间表开始端点有效");
            var scheduledGuard = new DfoServer.Game.Inventory.Models.InventoryEventPrecondition(mileageStore.Read(266), mileageClock);
            mileageClock.Advance(TimeSpan.FromSeconds(2));
            That(!mileageEvents.Read(266).Active && !scheduledGuard.Matches(mileageStore.Read(266)),
                "显式时间表结束端点关闭，事务前置不恢复默认常驻");
            That(mileageEvents.Override(266, true).Active && !mileageEvents.Override(266, false).Active &&
                !mileageEvents.Override(266, null).Active, "显式on和off覆盖时段，auto保留已结束的显式时段");
            That(mileageEvents.Read(64).Active && mileageEvents.Read(ServerEventService.NolanVisibilityId).Active,
                "积分默认策略保留黄金牌及诺兰默认开启");
            That(events.Read(64) is { Active: true, Schedule.Revision: 0 }, "未配置已登记活动默认开启，不将PVF2011文案当作时间表");
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            void Sql(string text)
            {
                using var cmd = db.CreateCommand(); cmd.CommandText = text; cmd.ExecuteNonQuery();
            }
            var request = DevelopmentRequest.Parse($"event schedule 64 {now.AddSeconds(1):yyyy-MM-ddTHH:mm:sszzz} {now.AddSeconds(3):yyyy-MM-ddTHH:mm:sszzz}", "10000");
            That(request.Action == DevelopmentAction.Event && request.Event!.Start == now.AddSeconds(1) &&
                request.Event.Execute(events) is { Active: false, Schedule.Revision: 1 }, "GM时间表要求显式时区，提交后才返回实际版本");
            clock.Advance(TimeSpan.FromSeconds(1)); That(events.Read(64).Active, "活动在开始端点立即生效，无后台计时或第二份活动缓存");
            clock.Advance(TimeSpan.FromSeconds(2)); That(!events.Read(64).Active, "活动结束端点不再生效，使用半开时间区间");
            var active = events.Override(64, true);
            That(active.Active && active.Schedule.Revision == 2 && events.Override(64, true) == active,
                "GM立即开启覆盖已结束的时间表，同值写入不增加版本");
            That(events.Override(64, false) is { Active: false } && !events.Override(64, null).Active,
                "GM立即关闭及恢复auto保持原时间表，不重新启用过期活动");
            var expected = events.Read(64).Schedule;
            Sql("CREATE TRIGGER event_fail AFTER UPDATE ON server_events BEGIN SELECT RAISE(ABORT,'event rollback'); END");
            Reject<SqliteException>(() => events.Override(64, true), "活动配置晚期SQL失败不返回成功或半份revision");
            That(store.Read(64) == expected, "活动配置写入失败保持原开关、时间表和版本");
            Sql("DROP TRIGGER event_fail");
            var reopened = new ServerEventRepository(path);
            events.Override(64, true);
            Reject<InvalidOperationException>(() => reopened.Save(expected, expected with { OverrideActive = false }), "旧活动版本不能覆盖较新GM开关");
            That(new ServerEventService(reopened, clock).Read(64).Active, "服务及仓储重建恢复已提交覆盖模式");
            using (var writer = db.BeginTransaction())
            {
                using var held = db.CreateCommand(); held.Transaction = writer; held.CommandText = "UPDATE server_events SET revision=revision"; held.ExecuteNonQuery();
                That(events.Read(64).Active && events.Override(64, true).Active,
                    "只读查询和同值开关不申请writer，另一连接持写事务时仍可完成");
                writer.Rollback();
            }
            foreach (string command in new[] { "return-user Character 30", "event on 74", "account user event on 64", "event schedule 64 2026-10-10T12:00:00 2026-10-10T13:00:00",
                "event schedule 64 2026-10-10T13:00:00Z 2026-10-10T12:00:00Z", "event on 64 extra" })
                Reject<InvalidDataException>(() => DevelopmentRequest.Parse(command, "10000"), "错误活动、账号范围、时区、区间或尾字段拒绝");
            foreach (string sql in new[] { "UPDATE server_events SET starts_at=2,ends_at=1", "UPDATE server_events SET starts_at=1,ends_at=NULL",
                "UPDATE server_events SET override_active=2", "UPDATE server_events SET revision=0" })
                Reject<SqliteException>(() => Sql(sql), "数据库保留活动时间、覆盖模式及版本约束");
            That(JsonSerializer.Serialize(store.Read(64)) == JsonSerializer.Serialize(reopened.Read(64)), "独立读库与内存决策来自同一持久活动配置");

            const ushort nolan = 2532;
            var goldBefore = events.Read(64);
            ServerEventState? initial = null;
            try { initial = events.Read(nolan); }
            catch (InvalidDataException) { }
            That(initial is { Active: true, Schedule: { EventId: nolan, Revision: 0, StartsAt: null, EndsAt: null, OverrideActive: null } },
                "2532未配置时默认显示诺兰，不要求部署后手动开启");
            That(new ServerEventService(new ServerEventRepository(path), clock).Read(nolan) == initial &&
                store.Read(nolan) == new ServerEventSchedule(nolan) && events.Read(64) == goldBefore,
                "2532默认可见在服务及仓储重建后保持，只读不补写覆盖或修改64");
            var nolanOn = events.Override(nolan, true);
            That(nolanOn is { Active: true, Schedule: { Revision: 1, StartsAt: null, EndsAt: null, OverrideActive: true } } &&
                events.Override(nolan, true) == nolanOn, "2532显式常驻开启且同值不增加版本");
            That(events.Read(64) == goldBefore, "2532开启不修改黄金牌64的状态、时间表和版本");
            Reject<InvalidDataException>(() => events.Schedule(nolan, clock.GetUtcNow().AddSeconds(1), clock.GetUtcNow().AddSeconds(3)),
                "2532常驻状态拒绝时间表，不恢复历史日期自动开关");
            That(events.Read(nolan) == nolanOn && events.Read(64) == goldBefore, "2532时间表拒绝不改两项活动的持久状态");
            var nextDay = new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
            clock.Advance(nextDay - clock.GetUtcNow());
            That(events.Read(nolan) == nolanOn && events.Read(64) == goldBefore, "2532显式开启跨日仍常驻且64版本不变");
            var nextYear = new DateTimeOffset(clock.GetUtcNow().Year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);
            clock.Advance(nextYear - clock.GetUtcNow());
            That(events.Read(nolan) == nolanOn && events.Read(64) == goldBefore, "2532显式开启跨年仍常驻且64版本不变");
            var nolanReopened = new ServerEventRepository(path);
            var nolanReloaded = new ServerEventService(nolanReopened, clock);
            That(nolanReloaded.Read(nolan) == nolanOn && nolanReloaded.Read(64) == goldBefore,
                "2532独立开关和版本在仓储及服务重建后完整恢复");
            var nolanOff = events.Override(nolan, false);
            That(nolanOff is { Active: false, Schedule: { Revision: 2, OverrideActive: false } } && events.Read(64) == goldBefore,
                "2532显式关闭只增加自己的版本且不关闭黄金牌64");
            Reject<InvalidOperationException>(() => nolanReopened.Save(nolanOn.Schedule, nolanOn.Schedule with { OverrideActive = false }),
                "2532旧revision不能覆盖新常驻开关");
            That(nolanReloaded.Read(nolan) == nolanOff &&
                new ServerEventService(new ServerEventRepository(path), clock).Read(nolan) == nolanOff && events.Read(64) == goldBefore,
                "2532显式off在服务及仓储重建后仍关闭，旧版本拒绝不复活默认状态");
            Reject<InvalidDataException>(() => store.Save(nolanOff.Schedule, goldBefore.Schedule), "2532配置不能借64的键或revision跨活动提交");
            Sql("CREATE TRIGGER nolan_event_fail AFTER UPDATE ON server_events WHEN NEW.event_id=2532 BEGIN SELECT RAISE(ABORT,'nolan event rollback'); END");
            Reject<SqliteException>(() => events.Override(nolan, true), "2532晚期SQL失败不返回常驻开启成功");
            That(store.Read(nolan) == nolanOff.Schedule && store.Read(64) == goldBefore.Schedule,
                "2532失败回滚保留自己的开关及版本且不修改64");
            Sql("DROP TRIGGER nolan_event_fail");
            var nolanAuto = events.Override(nolan, null);
            That(nolanAuto is { Active: true, Schedule: { Revision: 3, OverrideActive: null } } &&
                new ServerEventService(new ServerEventRepository(path), clock).Read(nolan) == nolanAuto && events.Read(64) == goldBefore,
                "2532的auto恢复默认可见，重建仍生效且不借64状态激活");
            var dated = store.Save(nolanAuto.Schedule, nolanAuto.Schedule with
            {
                StartsAt = clock.GetUtcNow().AddHours(-2).ToUnixTimeSeconds(),
                EndsAt = clock.GetUtcNow().AddHours(-1).ToUnixTimeSeconds(), OverrideActive = true
            });
            var cleared = events.Override(nolan, null);
            That(cleared.Active && cleared.Schedule.OverrideActive is null && cleared.Schedule.Revision == dated.Revision + 1 &&
                nolanReloaded.Read(nolan) == cleared && events.Read(64) == goldBefore,
                "2532的auto忽略已过期历史日期规则，默认可见及版本在重读后保持");
            clock.Advance(TimeSpan.FromDays(370));
            That(events.Read(nolan) == cleared && events.Read(64) == goldBefore,
                "2532默认可见跨日跨年保持，不依赖旧活动日期也不改64版本");
            var nolanBeforeGold = store.Read(nolan);
            var goldOff = events.Override(64, false);
            That(!goldOff.Active && goldOff.Schedule.Revision == goldBefore.Schedule.Revision + 1 && store.Read(nolan) == nolanBeforeGold &&
                events.Read(nolan).Active,
                "黄金牌64的真实状态及revision变更也不修改2532");
        }
        finally
        {
            TestFiles.DeleteDirectory(root, true);
        }
        Console.WriteLine($"PASS {Count - before} 活动时间表、GM覆盖、CAS、SQL回滚和无写查询断言");
    }

    static void VerifyStrongwayDefault(string root)
    {
        string path = Path.Combine(root, "default.db");
        var store = new ServerEventRepository(path);
        var events = new ServerEventService(store);
        int changes = 0;
        events.Changed += _ => changes++;
        var request = DevelopmentRequest.Parse("event initialize-default 312", "10000").Event!;
        var first = request.Execute(events, out var initialized);
        That(initialized == true && first is { Active: true, Schedule.Revision: 1, Schedule.OverrideActive: true }
            && changes == 1, "首次初始化312仅提交一次真实开启并发布变更");
        That(request.Execute(events, out initialized) == first && initialized == false && changes == 1,
            "默认初始化重试返回已有配置，不重复增加版本或通知");
        var off = events.Override(312, false);
        var reopened = new ServerEventService(new ServerEventRepository(path));
        That(reopened.InitializeStrongwayDefault() == new ServerEventInitialization(off, false),
            "初始化后手动关闭，仓储重开及后续初始化均保留关闭");
        var auto = events.Override(312, null);
        That(events.InitializeStrongwayDefault() == new ServerEventInitialization(auto, false), "已有auto配置原样保留");
        var scheduled = events.Schedule(312, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(2));
        That(events.InitializeStrongwayDefault() == new ServerEventInitialization(scheduled, false), "已有未来时间表不被默认开启覆盖");
        That(store.Read(64).Revision == 0 && store.Read(266).Revision == 0 && store.Read(2532).Revision == 0,
            "312初始化不修改其它活动");
        Reject<InvalidDataException>(() => DevelopmentRequest.Parse("event initialize-default 64", "10000"), "默认初始化仅允许312");

        var raced = new ServerEventRepository(Path.Combine(root, "default-race.db"));
        var manual = new ServerEventService(raced);
        var racing = new ServerEventService(new CompetingEventStore(raced, () => manual.Override(312, false)));
        That(racing.InitializeStrongwayDefault() is { Initialized: false, State.Active: false, State.Schedule.Revision: 1 }
            && raced.Read(312).OverrideActive == false, "初始化读后人工关闭抢先提交，条件INSERT不得重新打开");

        var concurrentStore = new ServerEventRepository(Path.Combine(root, "default-parallel.db"));
        int inserts = 0;
        var concurrentResults = new ServerEventInitialization[6];
        Parallel.For(0, concurrentResults.Length, index =>
        {
            var result = new ServerEventService(concurrentStore).InitializeStrongwayDefault();
            if (result.Initialized) Interlocked.Increment(ref inserts);
            concurrentResults[index] = result;
        });
        That(concurrentResults.All(result => result.State is { Active: true, Schedule.Revision: 1 }), "并发初始化均返回同一真实提交");
        That(inserts == 1 && concurrentStore.Read(312).Revision == 1, "多启动器并发只有一次默认写入");

        var failingStore = new ServerEventRepository(Path.Combine(root, "default-fail.db"));
        using var db = new SqliteGameDatabase(Path.Combine(root, "default-fail.db")).Open();
        using var sql = db.CreateCommand();
        sql.CommandText = "CREATE TRIGGER default_fail AFTER INSERT ON server_events BEGIN SELECT RAISE(ABORT,'default rollback'); END";
        sql.ExecuteNonQuery();
        var failing = new ServerEventService(failingStore);
        Reject<SqliteException>(() => failing.InitializeStrongwayDefault(), "默认活动晚期写失败不得返回成功");
        That(failingStore.Read(312).Revision == 0, "初始化失败不留下半行或已处理标记");
        sql.CommandText = "DROP TRIGGER default_fail"; sql.ExecuteNonQuery();
        That(failing.InitializeStrongwayDefault().Initialized, "失败后能够重新初始化");
    }

    static void VerifyRegisteredDefaults(string root, TimeProvider clock)
    {
        string path = Path.Combine(root, "registered-defaults.db");
        var now = clock.GetUtcNow();
        var events = new ServerEventService(new ServerEventRepository(path), clock);
        events.ConfigureBattlePass(now.AddDays(-1), now.AddDays(1), true);
        foreach (var definition in ServerEventService.Definitions)
        {
            var state = events.Read(definition.Id);
            That(state is { Active: true, Schedule.Revision: 0 }, "所有已登记活动缺省开启且只读不写库");
            var off = events.Override(definition.Id, false);
            var restored = new ServerEventService(new ServerEventRepository(path), clock);
            restored.ConfigureBattlePass(now.AddDays(-1), now.AddDays(1), true);
            That(restored.Read(definition.Id) == off && !off.Active, "人工关闭重启保留，不被缺省全开覆盖");
            That(restored.Override(definition.Id, null).Active, "无时间表auto恢复所有已登记活动的开启默认");
            if (!definition.SupportsSchedule) continue;
            var scheduled = restored.Schedule(definition.Id, now.AddHours(1), now.AddHours(2));
            That(!scheduled.Active && restored.Override(definition.Id, null) == scheduled,
                "人工未来时间表和auto保留原时段，不因默认全开提前开放");
        }
        That(!ServerEventService.DefaultActive(74), "资源公告编号不因默认全开变成已登记业务");
    }

    static void VerifyBattlePassDefault(string root)
    {
        string directory = Path.Combine(root, "pass-default"); Directory.CreateDirectory(directory);
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(8));
        var store = new ServerEventRepository(Path.Combine(directory, "game.db"));
        _ = store.Read(2520);
        var first = DfoServer.Persistence.BattlePassDefaultConfiguration.Resolve(directory, now);
        That(first.Start == new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8)) &&
            first.SeasonKey.StartsWith("local-", StringComparison.Ordinal), "无历史仅首次选最近合法周四起点和稳定技术季标识");
        That(DfoServer.Persistence.BattlePassDefaultConfiguration.Resolve(directory, now.AddDays(100)) == first,
            "跨重启和过期仍沿原稳定季，不重新取本周或自动换季");
        string concurrent = Path.Combine(root, "pass-concurrent"); Directory.CreateDirectory(concurrent);
        _ = new ServerEventRepository(Path.Combine(concurrent, "game.db")).Read(2520);
        var results = new DfoServer.Persistence.BattlePassDefaultConfiguration[4];
        Parallel.For(0, results.Length, index => results[index] = DfoServer.Persistence.BattlePassDefaultConfiguration.Resolve(concurrent, now));
        That(results.All(value => value == results[0]), "并发首次配置只保留一个稳定季和起点");
        string blocked = Path.Combine(root, "pass-blocked"); Directory.CreateDirectory(blocked);
        _ = new ServerEventRepository(Path.Combine(blocked, "game.db")).Read(2520);
        Directory.CreateDirectory(Path.Combine(blocked, "battle-pass-default.json"));
        Reject<IOException>(() => DfoServer.Persistence.BattlePassDefaultConfiguration.Resolve(blocked, now), "默认配置写失败不得伪报已保存");
        Directory.Delete(Path.Combine(blocked, "battle-pass-default.json"));
        var retried = DfoServer.Persistence.BattlePassDefaultConfiguration.Resolve(blocked, now);
        That(retried == DfoServer.Persistence.BattlePassDefaultConfiguration.Resolve(blocked, now.AddDays(1)) &&
            !Directory.EnumerateFiles(blocked, "*.tmp").Any(), "写失败清理临时文件后可重试并保持稳定");
        var events = new ServerEventService(store);
        Reject<InvalidOperationException>(() => events.InitializeDefault(2520), "未装配赛季的旧进程拒绝默认开启并说明需重启");
        events.ConfigureBattlePass(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), false);
        That(!events.Read(2520).Active && store.Read(2520).Revision == 0, "普通服务启动准备赛季但不创建开关");
        That(events.InitializeDefault(2520) is { Initialized: true, State.Active: true }, "新启动器首次初始化才真正开启2520");
        var off = events.Override(2520, false);
        var restarted = new ServerEventService(store);
        restarted.ConfigureBattlePass(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), true);
        That(restarted.InitializeDefault(2520) == new ServerEventInitialization(off, false), "显式CLI及后续初始化均保留已有关闭");
        var auto = restarted.Override(2520, null);
        That(restarted.InitializeDefault(2520) == new ServerEventInitialization(auto, false), "2520已有auto不被默认初始化覆盖");
        var scheduled = restarted.Schedule(2520, DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2));
        var scheduledRestart = new ServerEventService(store);
        scheduledRestart.ConfigureBattlePass(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), true);
        That(scheduledRestart.InitializeDefault(2520) == new ServerEventInitialization(scheduled, false), "2520已有时间表重启和默认API均保留");
        restarted.ConfigureBattlePass(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1), true);
        Reject<InvalidOperationException>(() => restarted.InitializeDefault(2520), "冻结季过期不虚报已开启或默开新季");
        string history = Path.Combine(root, "pass-history"); Directory.CreateDirectory(history);
        using (var db = new SqliteGameDatabase(Path.Combine(history, "game.db")).Open())
        {
            using var sql = db.CreateCommand();
            sql.CommandText = "INSERT INTO battle_pass_season_rules VALUES('frozen',$start,$end,'{}')";
            sql.Parameters.AddWithValue("$start", first.Start.ToUnixTimeSeconds());
            sql.Parameters.AddWithValue("$end", now.AddDays(1).ToUnixTimeSeconds()); sql.ExecuteNonQuery();
        }
        That(DfoServer.Persistence.BattlePassDefaultConfiguration.Resolve(history, now) == first with { SeasonKey = "frozen" },
            "唯一现有有效冻结季优先，不猜玩家历史或新建赛季");
        File.Delete(Path.Combine(history, "battle-pass-default.json"));
        using (var db = new SqliteGameDatabase(Path.Combine(history, "game.db")).Open())
        {
            using var sql = db.CreateCommand();
            sql.CommandText = "INSERT INTO battle_pass_season_rules SELECT 'ambiguous',start_utc,end_utc,rules_json FROM battle_pass_season_rules WHERE season_key='frozen'";
            sql.ExecuteNonQuery();
        }
        Reject<InvalidDataException>(() => DfoServer.Persistence.BattlePassDefaultConfiguration.Resolve(history, now),
            "多份有效冻结季不自动猜选或覆盖任何历史");
        Reject<InvalidDataException>(() => DfoServer.Persistence.BattlePassDefaultConfiguration.Resolve(history, now.AddDays(2)),
            "只有过期历史时保留历史并要求显式选择");
        File.WriteAllText(Path.Combine(history, "battle-pass-default.json"), "{}");
        Reject<InvalidDataException>(() => DfoServer.Persistence.BattlePassDefaultConfiguration.Resolve(history, now), "坏配置不能静默覆盖或重建季");
    }

    sealed class CompetingEventStore(IServerEventStore inner, Action competingWrite) : IServerEventStore
    {
        public ServerEventSchedule Read(ushort id) => inner.Read(id);
        public ServerEventSchedule Save(ServerEventSchedule expected, ServerEventSchedule desired)
        {
            competingWrite();
            return inner.Save(expected, desired);
        }
    }

    static void VerifyEpicRoad(string path)
    {
        const ushort epicRoad = 10005;
        var clock = new ConsumableUseSessionTests.FakeClock();
        var now = clock.GetUtcNow();
        var store = new ServerEventRepository(path);
        var events = new ServerEventService(store, clock);
        var unrelated = new ushort[] { 64, 266, 2532 }.Select(events.Read).ToArray();
        var initial = DevelopmentRequest.Parse("event status 10005", "10000").Event!.Execute(events);
        That(initial is { Active: true, Schedule: { Revision: 0, StartsAt: null, EndsAt: null, OverrideActive: null } },
            "星河裂缝10005未配置默认开启，独立配置及版本保持不变");
        That(DevelopmentRequest.Parse("event auto 10005", "10000").Event!.Execute(events) == initial,
            "星河裂缝无时间表auto保持默认开启且不创建无变化版本");
        var enabled = DevelopmentRequest.Parse("event on 10005", "10000").Event!.Execute(events);
        That(enabled is { Active: true, Schedule.Revision: 1 } && events.Override(epicRoad, true) == enabled &&
            new ServerEventService(new ServerEventRepository(path), clock).Read(epicRoad) == enabled,
            "星河裂缝GM覆盖沿已有持久化恢复，同值开启不增加版本");
        var disabled = DevelopmentRequest.Parse("event off 10005", "10000").Event!.Execute(events);
        That(disabled is { Active: false, Schedule.Revision: 2 } &&
            new ServerEventService(new ServerEventRepository(path), clock).Read(epicRoad) == disabled,
            "星河裂缝显式关闭在仓储和服务重建后保持");
        Reject<InvalidOperationException>(() => store.Save(enabled.Schedule, enabled.Schedule with { OverrideActive = false }),
            "星河裂缝旧revision不能覆盖较新的关闭配置");
        That(events.Override(epicRoad, null) is { Active: true, Schedule: { Revision: 3, OverrideActive: null } },
            "星河裂缝无时间表auto恢复默认开启");
        var request = DevelopmentRequest.Parse(
            $"event schedule 10005 {now.AddSeconds(1):yyyy-MM-ddTHH:mm:sszzz} {now.AddSeconds(3):yyyy-MM-ddTHH:mm:sszzz}", "10000");
        That(request.Event!.Execute(events) is { Active: false, Schedule.Revision: 4 },
            "星河裂缝GM时间表复用显式时区解析及已有配置事务，开始前保持关闭");
        clock.Advance(TimeSpan.FromSeconds(1));
        That(events.Read(epicRoad).Active, "星河裂缝时间表包含开始端点");
        clock.Advance(TimeSpan.FromSeconds(2));
        That(!events.Read(epicRoad).Active && events.Override(epicRoad, true).Active &&
            !DevelopmentRequest.Parse("event auto 10005", "10000").Event!.Execute(events).Active,
            "星河裂缝时间表排除结束端点，显式on覆盖时段，auto不复活已结束活动");
        That(unrelated.SequenceEqual(new ushort[] { 64, 266, 2532 }.Select(events.Read)),
            "星河裂缝开关和时间表不改变黄金牌、积分商城或诺兰的状态与版本");
    }
}
