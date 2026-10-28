using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Marriage;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Marriage;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Projection;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MarriageLifecycleTests
{
    public static void RunCore()
    {
        int checks = Count;
        var hall = new TownScene(39, 8, "Church/Church.map", false);
        Guid ceremony = Guid.NewGuid();
        That(hall.OwnerKey("first", 1, ceremonyId: ceremony) == hall.OwnerKey("guest", 3, ceremonyId: ceremony) &&
            hall.OwnerKey("first", 1, ceremonyId: ceremony) != hall.OwnerKey("second", 2, ceremonyId: Guid.NewGuid()) &&
            hall.OwnerKey("first", 1) != hall.OwnerKey("guest", 3),
            "同礼堂地图按不可复用婚礼实例隔离同屏和聊天，未授权场景不能混入");
        var before = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), 123, 1, 2, 1000) with
        {
            State = 2,
            Wedding = new() { Id = Guid.NewGuid(), State = 1, ReservedAt = 1000, StartsAt = 1300, EndsAt = 1360,
                Gifts = [new() { Id = Guid.NewGuid(), CharacterId = 3, Gold = 10001 }] }
        };
        Reject<InvalidOperationException>(() => MarriageWeddingScheduler.Complete(before, 1299), "预约倒计时结束前不能结婚");
        var complete = MarriageWeddingScheduler.Complete(before, 1300);
        That(complete.State == 4 && complete.MarriedAt == 1300 && complete.Revision == 2 &&
            complete.Wedding is { State: 2 } && complete.Wedding.Gifts.All(row => row.Settled) && before.State == 2,
            "开场状态与实际结婚时间一起生成新快照，原预约保持不变");
        Reject<InvalidOperationException>(() => MarriageWeddingScheduler.Complete(complete, 1301), "同一婚礼不能再次完成");
        Reject<InvalidOperationException>(() => MarriageWeddingScheduler.Complete(before, 1360), "超过通知容差的旧预约必须退款而非迟到成婚");
        var cancelled = MarriageWeddingScheduler.Cancelled(before, 1400);
        That(cancelled.Active && cancelled.State == 1 && cancelled.MarriedAt == 0 && cancelled.Wedding is { State: 3 } &&
            cancelled.Wedding.Gifts.All(row => row.Settled), "错过预约恢复婚约，不误做离婚或遗失礼金结算标志");
        Reject<InvalidOperationException>(() => MarriageWeddingScheduler.Cancelled(cancelled, 1401), "取消重放不再生成退款状态");
        That(MarriageWeddingScheduler.SpouseShare(10001, 50) == 2500 &&
            MarriageWeddingScheduler.SpouseShare(10003, 50) == 2500 && MarriageWeddingScheduler.SpouseShare(10004, 50) == 2501,
            "逐笔礼金先扣50%并舍去小数，再平分并舍去不能平分的1金币");
        var card = new MarriageCharacterCard(new MailboxRecipient(2, "test", "伴侣", 1, 0x21, 55), Enumerable.Range(10, 13).ToArray());
        byte[] body = MarriageProjection.Card(0x11223344, card);
        That(body.Length == 98 && body[0] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5)) == 0x11223344 &&
            body[43] == 55 && body[44] == 1 && body[45] == 0x21 &&
            BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(46)) == 10 && BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(94)) == 22,
            "角色卡独立检查ACK前导、上下文、等级职业次序及首尾装备偏移");
        byte[] info = MarriageProjection.Information(complete, 1);
        That(BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(6)) == 1300 && info[4] == 4,
            "婚姻日期投影实际开场成婚时刻，不复用订婚创建时间");
        Console.WriteLine($"PASS {Count - checks} marriage lifecycle core assertions");
    }

    public static void Run(MarriageCatalog catalog, ItemCatalog items)
    {
        int checks = Count;
        string directory = Path.Combine(Path.GetTempPath(), "s5a9-marriage-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "game.db");
            var auth = new AuthenticationService(new AuthenticationRepository(path));
            auth.Create("lifecycle-first", "fixture-password"); auth.Create("lifecycle-second", "fixture-password");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var firstOwner = owners.Acquire("lifecycle-first", () => { });
            using var secondOwner = owners.Acquire("lifecycle-second", () => { });
            var first = repository.Create(firstOwner, new("First", new(1, 0, 50, 50), null) { Level = 30, Inventory = new([], Gold: 1000) });
            var second = repository.Create(secondOwner, new("Second", new(1, 0, 50, 50), null) { Job = 1, Level = 30, Inventory = new([], Gold: 1000) });
            var record = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), 23, first.Id, second.Id, 1000) with
            {
                State = 2,
                Wedding = new() { Id = Guid.NewGuid(), State = 1, ReservedAt = 1000, StartsAt = 1300, EndsAt = 1360,
                    PayerId = first.Id, PaidGold = 50, HallArea = 8, ServerIndex = 1, ChannelNumber = 10 }
            };
            var committed = repository.CommitMarriageTogether(firstOwner, secondOwner, first, second,
                new(first.Inventory, first.Inventory with { Gold = 950 }, "marriage_lifecycle_fixture"),
                new(second.Inventory, second.Inventory, "marriage_lifecycle_fixture"), null, record);
            var reopened = new CharacterRepository(path);
            var online = new OnlineSessionDirectory();
            var clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds(1400));
            using (var log = new ServerLog(directory))
            {
                var scheduler = new MarriageWeddingScheduler(reopened, catalog, items, online,
                    new MarriageProjection(reopened, catalog, online, clock), log, clock);
                scheduler.TickAsync(startup: true).GetAwaiter().GetResult();
                scheduler.TickAsync(startup: true).GetAwaiter().GetResult();
            }
            var after = reopened.FindMarriage(record.Id)!;
            That(after.State == 1 && after.Wedding is { State: 3 } && after.Revision == 2 && reopened.FindPendingWeddings().Count == 0,
                "隔离库重启恢复使用注入时钟，过期预约只取消一次并保留婚约");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            db.Open(); using var query = db.CreateCommand();
            query.CommandText = "SELECT count(*) FROM mailbox_messages WHERE gold=50";
            That(Convert.ToInt32(query.ExecuteScalar()) == 1 &&
                reopened.LoadCharacter(firstOwner, first.Slot).Inventory.Gold == 950 &&
                reopened.LoadCharacter(firstOwner, first.Slot).Revision == committed.First.Revision &&
                reopened.LoadCharacter(secondOwner, second.Slot).Revision == committed.Second!.Revision,
                "恢复退款仅投一封本金邮件，不直接改玩家金币、资产版本或重复发放");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
        Console.WriteLine($"PASS {Count - checks} marriage lifecycle recovery assertions");
    }

    sealed class FixedClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
