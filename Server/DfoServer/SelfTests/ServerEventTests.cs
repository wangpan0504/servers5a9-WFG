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
        }
        finally
        {
            Directory.Delete(root, true);
        }
        Console.WriteLine($"PASS {Count - before} 活动时间表、GM覆盖、CAS、SQL回滚和无写查询断言");
    }
}
