using System.Collections.Immutable;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Persistence;

namespace DfoServer.Testing.Inventory;

internal static partial class DailyBoosterProjectionTests
{
    public static async Task RunResourcesAsync(TcpTestResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-daily-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalogWatch = System.Diagnostics.Stopwatch.StartNew();
            int supported = resources.ItemResources.Boosters.DailyRetainedTemplateIds.Count;
            Console.WriteLine($"Daily retained catalog first enumeration: scanned={resources.ItemResources.Boosters.DailyRetainedScannedTemplates}, supported={supported}, elapsed={catalogWatch.Elapsed.TotalMilliseconds:F2}ms");
            var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-18T21:59:59Z"));
            int day = ShopPurchaseCount.Today(clock.GetUtcNow());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            using var owner = new AccountOwners().Acquire("daily-projection", () => { });
            var rows = Enumerable.Range(700000001, 512).Select(id => new DailyBoosterUsage(id, day, 1)).ToImmutableArray();
            var first = repository.Create(owner, new("DailyFirst", resources.Towns.Room, resources.Towns.InitialReturn));
            first = repository.CommitInventory(owner, first, new(first.Inventory,
                first.Inventory with { DailyBoosterUses = rows }, "daily_test_setup"));
            var second = repository.Create(owner, new("DailySecond", resources.Towns.Room, resources.Towns.InitialReturn));
            var session = new GameSession(repository, resources.Towns, owner);
            session.Select(first.Slot);
            using var stop = new CancellationTokenSource();
            using var stream = new HeldOutput();
            var sent = new List<byte[]>();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0,
                (_, opcode, _, body, _) => { if (opcode == 541) sent.Add(body.ToArray()); });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            using var log = new ServerLog(root);
            await using var projection = new DailyRetainedBoosterProjection(context, resources.ItemResources.Boosters, log, clock);
            await projection.InitializeAsync(session.Selected!, () => false);
            TestCheck.That(sent.Count == 0, "未真正发送的541不能宣布已发");
            await projection.InitializeAsync(session.Selected!);
            var consumer = Consume(sent);
            TestCheck.That(sent.Count >= 3 && sent.Take(2).All(packet => packet[0] == 255) && rows.All(row => consumer[row.ItemId] == 1) &&
                resources.ItemResources.Boosters.DailyRetainedTemplateIds.All(consumer.ContainsKey),
                "初始化未Ready也覆盖Catalog全部key及超过255条的完整账本");
            int initialPackets = sent.Count;
            await projection.InitializeAsync(session.Selected!);
            TestCheck.That(sent.Count == initialPackets, "相同已发视图不重复发送");
            session.MarkReady();
            session.ReturnToRoster();
            session.Select(second.Slot);
            sent.Clear();
            await projection.InitializeAsync(session.Selected!);
            foreach (var row in Consume(sent)) consumer[row.Key] = row.Value;
            TestCheck.That(rows.All(row => consumer[row.ItemId] == 0), "连接历史key跨选角保留且逐项zero覆盖，无需A0清map");
            session.MarkReady();
            session.ReturnToRoster();
            session.Select(first.Slot);
            session.MarkReady();
            sent.Clear();
            stream.Hold();
            Task oldHeld = sender.Send(1, 0, [], "held_epoch");
            await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
            Task oldEpoch = projection.InitializeAsync(session.Selected!);
            session.ReturnToRoster();
            session.Select(first.Slot);
            session.MarkReady();
            stream.Release();
            await Task.WhenAll(oldHeld, oldEpoch).WaitAsync(TimeSpan.FromSeconds(5));
            TestCheck.That(sent.Count == 0, "同角色重新选择后旧epoch排队541不得发布");
            var result = session.MutateInventory(c => new(true, 0, new(c.Inventory,
                c.Inventory with { DailyBoosterUses = c.Inventory.DailyBoosterUses.SetItem(0, rows[0] with { Used = 2 }) }, "projection_test")));
            stream.Hold();
            Task held = sender.Send(1, 0, [], "held");
            await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
            Task publication = projection.CommittedAsync(result.Receipt!);
            clock.Advance(TimeSpan.FromSeconds(1));
            stream.Release();
            await Task.WhenAll(held, publication).WaitAsync(TimeSpan.FromSeconds(5));
            TestCheck.That(sent.Count == 0, "发送队列跨日后旧receipt视图被日期guard丢弃");
            await projection.RefreshAsync();
            TestCheck.That(Consume(sent).Values.All(value => value == 0), "丢弃旧日包后按实际服务日完整归零");
            await clock.WaitForOneShotTimersAsync(1);
            // timer先醒、请求仍持门禁；退出时取消其等待，不可互相等待死锁。
            await context.CommandGate.WaitAsync();
            try
            {
                clock.Advance(TimeSpan.FromDays(1));
                session.ReturnToRoster();
                await projection.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { context.CommandGate.Release(); }
            TestCheck.That(clock.ActiveTimers == 0, "门禁内停止排队时钟可完成并回收timer");
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
    }

    static Dictionary<int, byte> Consume(IEnumerable<byte[]> packets)
    {
        var result = new Dictionary<int, byte>();
        foreach (var packet in packets)
            foreach (var row in Decode(packet)) result.Add(row.Key, row.Value);
        return result;
    }

    sealed class HeldOutput : MemoryStream
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource? release;
        public Task Started => started.Task;
        public void Hold()
        {
            started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public void Release() => release?.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            if (release is { } gate) await gate.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
