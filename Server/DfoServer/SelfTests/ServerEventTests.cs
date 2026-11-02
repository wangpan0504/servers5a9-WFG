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
            string path = Path.Combine(root, "game.db");
            var clock = new ConsumableUseSessionTests.FakeClock();
            var now = clock.GetUtcNow();
            var store = new ServerEventRepository(path);
            var events = new ServerEventService(store, clock);
            const ushort strongway = 312;
            var goldBeforeStrongway = store.Read(64);
            ServerEventState? strongwayInitial = null;
            try { strongwayInitial = events.Read(strongway); }
            catch (InvalidDataException) { }
            That(strongwayInitial is { Active: false, Schedule: { EventId: strongway, Revision: 0, StartsAt: null, EndsAt: null, OverrideActive: null } } &&
                store.Read(strongway) == new ServerEventSchedule(strongway),
                "强者之路312已登记但未配置默认关闭，只读不补写活动版本");
            var strongwayOn = DevelopmentRequest.Parse("event on 312", "10000").Event!.Execute(events);
            That(strongwayOn is { Active: true, Schedule: { Revision: 1, OverrideActive: true } } &&
                DevelopmentRequest.Parse("event on 312", "10000").Event!.Execute(events) == strongwayOn,
                "312 GM on立即生效，同值开启不增加revision");
            var strongwayOff = DevelopmentRequest.Parse("event off 312", "10000").Event!.Execute(events);
            That(strongwayOff is { Active: false, Schedule: { Revision: 2, OverrideActive: false } },
                "312 GM off关闭活动并提交独立revision");
            var strongwayAuto = DevelopmentRequest.Parse("event auto 312", "10000").Event!.Execute(events);
            That(strongwayAuto is { Active: false, Schedule: { Revision: 3, OverrideActive: null } },
                "312没有时间表时auto恢复默认关闭");
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
            That(!mileageEvents.Read(64).Active && mileageEvents.Read(ServerEventService.NolanVisibilityId).Active,
                "积分默认策略不改变黄金牌默认关闭或诺兰默认可见");
            That(events.Read(64) is { Active: false, Schedule.Revision: 0 }, "未配置活动保持关闭，不将PVF2011文案当作时间表");
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
}
