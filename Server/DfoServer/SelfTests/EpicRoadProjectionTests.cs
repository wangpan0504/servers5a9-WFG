using System.IO.Compression;
using System.Net;
using System.Threading.Channels;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicRoadProjectionTests
{
    public static async Task RunCoreAsync()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-epic-road-projection-" + Guid.NewGuid().ToString("N"));
        bool passed = false;
        try
        {
            using var stop = new CancellationTokenSource();
            var owners = new AccountOwners();
            using var owner = owners.Acquire("epic-projection", () => stop.Cancel());
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("synthetic/epic-event.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            repository.Create(owner, new("EpicView", point, point));
            var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-11-01T12:00:00Z"));
            var session = new GameSession(repository, towns, owner, timeProvider: clock);
            session.Select(0);
            session.MarkReady();
            var events = new ServerEventService(new ServerEventRepository(path), clock);
            events.Override(ServerEventService.EpicRoadId, false);
            var stats = new CharacterStatComputer(new CharacterCatalog([]));
            var pieces = new EpicPieceCatalog("[piece list]\n9001 9002\n[/piece list]");
            const string normalShop = "[item]\n1001 101 1 0 0 100 `1个` 0 0\n[/item]";
            const string mileageShop = "[item]\n2001 101 1 0 5 0 `1个` 0 0\n[/item]";
            var mileage = CashShopCatalog.Parse(normalShop, mileageText: mileageShop).Mileage;
            That(mileage.Available, "合成积分目录有效，后续验证266与10005独立投影");
            TownProjection Town(bool available = false, bool withEvents = true) => new(towns, stats, null, pieces,
                clock: clock, events: withEvents ? events : null, mileageShop: mileage, epicRoadAvailable: available);
            var town = Town(true);
            var delivered = Channel.CreateBounded<byte[]>(32);
            int eventCount = 0;
            using var stream = new GameSenderBudgetTests.HeldStream();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, opcode, _, body, _) =>
            {
                if (opcode != 108) return;
                Interlocked.Increment(ref eventCount);
                if (!delivered.Writer.TryWrite(body.ToArray())) throw new InvalidOperationException("测试活动队列已满");
            });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            async Task Expect(bool epic, bool shop, bool nolan = true) =>
                AssertEvents(await delivered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), epic, shop, nolan);

            // 截住发送泵，验证旧选角的整表投影在真正写帧前失效。
            var held = sender.Send(0, 2, [1], "epic_event_held");
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            long oldEpoch = session.SelectionEpoch;
            var stale = town.ServerEventsAsync(context, () => session.Ready && session.SelectionEpoch == oldEpoch);
            session.ReturnToRoster();
            session.Select(0);
            session.MarkReady();
            stream.Release.TrySetResult();
            await held;
            await stale;
            await sender.DrainAsync();
            That(eventCount == 0, "旧epoch排队的108整表不覆盖重新选角后的活动目录");

            await town.ServerEventsAsync(context);
            await Expect(false, true);
            events.Override(ServerEventService.EpicRoadId, true);
            await town.ServerEventsAsync(context);
            await Expect(true, true);
            await Town().ServerEventsAsync(context);
            await Expect(false, true);
            await Town(true, false).ServerEventsAsync(context);
            await Expect(false, false, false);

            using var log = new ServerLog(Path.Combine(root, "log"));
            await using (var projection = new ServerEventProjection(context, events, town, log, clock))
            {
                context.ServerEvents = projection;
                await town.ServerEventsAsync(context);
                await Expect(true, true);
                events.Override(ServerEventService.EpicRoadId, false);
                await Expect(false, true);
                events.Override(ServerEventService.MileageShopId, false);
                await Expect(false, false);
                events.Override(ServerEventService.EpicRoadId, true);
                await Expect(true, false);

                var now = clock.GetUtcNow();
                events.Schedule(ServerEventService.EpicRoadId, now.AddSeconds(1), now.AddSeconds(3));
                await Expect(false, false);
                events.Schedule(ServerEventService.MileageShopId, now.AddSeconds(2), now.AddSeconds(4));
                await clock.WaitForOneShotTimerAsync(now.AddSeconds(1));
                clock.Advance(TimeSpan.FromSeconds(1));
                await Expect(true, false);
                await clock.WaitForOneShotTimerAsync(now.AddSeconds(2));
                clock.Advance(TimeSpan.FromSeconds(1));
                await Expect(true, true);
                await clock.WaitForOneShotTimerAsync(now.AddSeconds(3));
                clock.Advance(TimeSpan.FromSeconds(1));
                await Expect(false, true);
                await clock.WaitForOneShotTimerAsync(now.AddSeconds(4));
                clock.Advance(TimeSpan.FromSeconds(1));
                await Expect(false, false);
            }
            context.ServerEvents = null;
            await sender.DrainAsync();
            That(clock.ActiveTimers == 0 && !stop.IsCancellationRequested,
                "单worker结束释放活动边界计时器，不关闭仍有效连接");
            int disposedCount = eventCount;
            events.Override(ServerEventService.EpicRoadId, true);
            await sender.DrainAsync();
            That(eventCount == disposedCount && !delivered.Reader.TryRead(out _),
                "退出后取消订阅且无残留或重复108通知");
            passed = true;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (passed) TestFiles.DeleteDirectory(root, true);
            else Console.WriteLine("FAIL fixture: " + root);
        }
        Console.WriteLine($"PASS {Count - before} 星河裂缝完整活动投影、独立时段与选角失效断言");
    }

    static void AssertEvents(byte[] compressed, bool epic, bool shop, bool nolan)
    {
        using var source = new MemoryStream(compressed);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var actual = new MemoryStream();
        zlib.CopyTo(actual);
        using var expected = new MemoryStream();
        using (var writer = new BinaryWriter(expected, System.Text.Encoding.UTF8, true))
        {
            ushort[] ids = [2037, 2378, 2395, .. nolan ? new ushort[] { 2532 } : [],
                .. shop ? new ushort[] { 266 } : [], .. epic ? new ushort[] { 10005 } : []];
            writer.Write((ushort)ids.Length);
            foreach (ushort id in ids)
            {
                writer.Write(id);
                writer.Write(1u);
                writer.Write(0u);
                writer.Write(0u);
                writer.Write((byte)0);
            }
            writer.Write((byte)2);
            writer.Write((ushort)2037);
            writer.Write(8u);
            for (int index = 1; index < 12; index++) writer.Write(0u);
            writer.Write((ushort)2395);
            for (int index = 0; index < 12; index++) writer.Write(0u);
        }
        Bytes(actual.ToArray(), expected.ToArray(), "108保留完整活动目录与2037/2395尾表；10005只加目录，不猜数值块");
    }
}
