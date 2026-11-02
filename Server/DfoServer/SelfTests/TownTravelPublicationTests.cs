using System.Net;
using System.Collections.Concurrent;
using System.IO.Compression;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Events;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;

namespace DfoServer;

internal static class TownTravelPublicationTests
{
    public static async Task RunAsync(TownCatalog towns, CharacterCatalog characters, ItemResources items)
    {
        int before = TestCheck.Count;
        var computer = new CharacterStatComputer(characters);
        string root = Path.Combine(Path.GetTempPath(), "S5A9-travel-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await NormalAsync();
            foreach (bool reselect in new[] { true, false })
            foreach (bool beforeClear in new[] { true, false })
                await InterruptedAsync(reselect, beforeClear);
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
        Console.WriteLine($"PASS {TestCheck.Count - before} town travel publication assertions");

        async Task NormalAsync()
        {
            var repository = new CharacterRepository(Path.Combine(root, "normal.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("normal-travel", () => { });
            repository.Create(owner, new("NormalTravel", towns.Room, towns.InitialReturn));
            var session = new GameSession(repository, towns, owner);
            session.Select(0);
            session.MarkReady();
            var presence = new TownPresence(towns);
            var projection = new TownProjection(towns, computer, presence, items.EpicPieces);
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var sent = new List<string>();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, action) => sent.Add(action));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            projection.Join(context);
            TestCheck.That(session.LeaveRoom(out var receipt), "普通出房产生回执");
            await projection.TravelAsync(context, receipt!);
            TestCheck.That(sent.SequenceEqual(["area", "transition"]) && presence.Count == 1, "出房只发布23/24并加入实际提交场景");
            projection.Move(context);
            sent.Clear();
            var outside = session.Selected!;
            TestCheck.That(session.Travel(new(towns.Room.Town, towns.Room.Area, towns.Room.X, towns.Room.Y, 5,
                outside.Position.Town, outside.Position.Area, 0), out var returned), "有坐标回房产生回执");
            await projection.TravelAsync(context, returned!);
            TestCheck.That(sent.SequenceEqual(["area", "transition", "room_return_town"]) && presence.Count == 1,
                "入房保留23/24/1379顺序，不追加选角124");
            TestCheck.That(repository.LoadCharacter(owner, 0).Revision == returned!.Character.Revision &&
                session.CanPublishTravel(returned), "发布不会再次提交旅行或改变回执");
            session.ReturnToRoster(); session.Select(0); session.MarkReady(); projection.Join(context);
            sent.Clear();
            await projection.TravelAsync(context, returned, auraTeleport: true);
            TestCheck.That(sent.Count == 0 && presence.Count == 1,
                "旧选角回执不发送出发、到达或切图，也不移除新会话成员");
            projection.Remove(context);
        }

        async Task InterruptedAsync(bool reselect, bool beforeClear)
        {
            var repository = new CharacterRepository(Path.Combine(root, $"interrupt-{reselect}-{beforeClear}.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("interrupted-travel", () => { });
            repository.Create(owner, new("QueuedTravel", towns.Room, towns.InitialReturn));
            var session = new GameSession(repository, towns, owner);
            session.Select(0);
            session.MarkReady();
            var presence = new TownPresence(towns);
            var projection = new TownProjection(towns, computer, presence, items.EpicPieces);
            using var log = new ServerLog(root);
            var commands = new GameCommandRegistry();
            new TownHandler(projection, log).Register(commands);
            commands.Seal();
            using var stop = new CancellationTokenSource();
            using var stream = new HeldOutput();
            var sent = new List<string>();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, action) => sent.Add(action));
            var context = new GameConnectionContext(new(2, IPAddress.Loopback, stream, stop), owner, session, sender);
            projection.Join(context);
            long epoch = session.SelectionEpoch;
            long revision = session.Selected!.Revision;
            Task first = beforeClear ? sender.Send(1, 0, [], "held") : Task.CompletedTask;
            if (beforeClear)
                await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
            var publication = commands.DispatchAsync(context, new(1, (ushort)CmdPacketType.PREV_VILLAGE, []));
            TownTravelReceipt? receipt = null;
            try
            {
                if (!beforeClear)
                    await stream.Started.WaitAsync(TimeSpan.FromSeconds(5));
                receipt = new(session.Selected!, owner.Generation, epoch);
                TestCheck.That(receipt.Character.Revision == revision + 1 && receipt.Character.Position == towns.InitialReturn &&
                    repository.LoadCharacter(owner, 0).Position == towns.InitialReturn && !publication.IsCompleted, "等待清场/网络期间位置已先提交");
                TestCheck.That(presence.Count == 0, "旅行等待期间已离开原成员集合");
                if (reselect)
                {
                    session.ReturnToRoster();
                    session.Select(0);
                    session.MarkReady();
                    projection.Join(context);
                }
                else
                {
                    session.UpdateProgress(new(CharacterProgressField.Level, owner.Account, receipt.Character.Name, 2, 0));
                    TestCheck.That(session.Selected!.Revision == receipt.Character.Revision, "GM替换快照保持revision");
                }
            }
            finally { stream.Release(); }
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            if (reselect)
            {
                TestCheck.That(await publication.WaitAsync(TimeSpan.FromSeconds(5)) == CommandResult.Handled, "新epoch跳过旧切图结果");
                TestCheck.That(presence.Count == 1 && !stop.IsCancellationRequested, "旧结果不移除或重新加入新角色成员");
                projection.Move(context);
                await projection.TravelAsync(context, receipt!);
                TestCheck.That(presence.Count == 1, "已经过期的回执在清场前即被拦截");
            }
            else
            {
                bool failed = false;
                try { await publication.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (InvalidOperationException) { failed = true; }
                TestCheck.That(failed && stop.IsCancellationRequested && presence.Count == 0, "同选角快照变化终止发送连接，不以不完整切图继续运行");
                var saved = repository.LoadCharacter(owner, 0);
                TestCheck.That(saved.Level == 2 && saved.Position == towns.InitialReturn && saved.Revision == receipt!.Character.Revision,
                    "发布失败保留已提交位置和GM更新，供重登恢复");
            }
            TestCheck.That(sent.SequenceEqual(beforeClear ? ["held"] : ["area"]),
                "原回执失效后不混发后续包；已经开始写入的首帧保留在途边界");
            projection.Remove(context);
        }
    }

    // 复用已加载的TCP资源；场景本身验证真实的进程内发送器和活动工作任务。
    internal static async Task RunServerEventPublicationAsync(TcpTestResources resources)
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-event-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var start = new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8));
        var catalog = BattlePassCatalog.Load(resources.Archive);
        try
        {
            await Scenario(false);
            await Scenario(true);
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
        Console.WriteLine($"PASS {TestCheck.Count - before} server event publication assertions");

        async Task Scenario(bool reselect)
        {
            string scenarioRoot = Path.Combine(root, reselect ? "reselect" : "same-character");
            Directory.CreateDirectory(scenarioRoot);
            var items = resources.ItemResources;
            var clock = new DfoServer.Testing.Inventory.DailyBoosterTestClock(reselect ? start.AddSeconds(-1) : start.AddHours(1));
            var repository = new CharacterRepository(Path.Combine(scenarioRoot, "game.db"));
            var policy = new BattlePassMissionPolicy("event2520-publication", start, catalog);
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("event-publication", () => { });
            foreach (string name in new[] { "EventOne", "EventTwo" })
                repository.Create(owner, new NewCharacter(name, resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
            var session = new GameSession(repository, resources.Towns, owner, timeProvider: clock);
            session.Select(0); session.MarkReady();
            var initial = session.Selected!;
            long epoch = session.SelectionEpoch;
            var events = new ServerEventService(new ServerEventRepository(Path.Combine(scenarioRoot, "game.db")), clock);
            events.Override(2532, true); // 仅用于隔离夹具初始化；战令边界变化不使用活动开关。
            var stats = new CharacterStatComputer(resources.Characters);
            var town = new TownProjection(resources.Towns, stats, null, items.EpicPieces,
                clock: clock, events: events, mileageShop: items.CashShop.Mileage) { BattlePassMissions = policy };
            using var stop = new CancellationTokenSource();
            using var stream = new GameSenderBudgetTests.HeldStream();
            if (reselect) stream.Release.TrySetResult();
            var frames = new ConcurrentQueue<(byte Kind, ushort Opcode, byte[] Body, string Action)>();
            var actual108 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (kind, opcode, _, body, action) =>
            {
                frames.Enqueue((kind, opcode, body.ToArray(), action));
                if (kind == 0 && opcode == 108) actual108.TrySetResult();
            });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            using var log = new ServerLog(Path.Combine(scenarioRoot, "logs"));
            await using var projection = new ServerEventProjection(context, events, town, log, clock);
            context.ServerEvents = projection;
            bool gateHeld = false;
            try
            {
                if (!reselect)
                {
                    await context.CommandGate.WaitAsync(); gateHeld = true;
                    Task held = sender.Send(1, 0, [], "held");
                    await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    long heldBytes = sender.PendingBytes;
                    bool Current() => ReferenceEquals(session.Selected, initial);
                    using (sender.QueuePublication((Func<bool>)Current))
                        await town.ServerEventsAsync(context, Current);
                    TestCheck.That(sender.PendingBytes > heldBytes && !actual108.Task.IsCompleted,
                        "共享活动快照仅排队，实际写泵仍被首帧阻塞");
                    session.UpdateProgress(new(CharacterProgressField.Level, owner.Account, initial.Name, 2, 0));
                    var updated = session.Selected!;
                    TestCheck.That(!Current() && updated.Id == initial.Id && session.SelectionEpoch == epoch && updated.Revision == initial.Revision,
                        "真实GM更新使捕获守卫失效，同时角色号、epoch和revision不变");
                    stream.Release.TrySetResult();
                    await held.WaitAsync(TimeSpan.FromSeconds(5));
                    await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    TestCheck.That(!frames.Any(row => row.Opcode == 108) && !actual108.Task.IsCompleted,
                        "失效共享108出队时被拒绝，不能记成已经展示");
                    context.CommandGate.Release(); gateHeld = false;
                    await actual108.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    var written = frames.Where(row => row.Kind == 0 && row.Opcode == 108).ToArray();
                    TestCheck.That(written.Length == 1, "原worker在同选角守卫失效后补发且只补发一份当前108");
                    Validate108(written[0].Body, true);
                    TestCheck.That(!stop.IsCancellationRequested && session.Selected!.Id == initial.Id,
                        "同一角色保持在线并收到恢复快照");
                }
                else
                {
                    await town.ServerEventsAsync(context);
                    var first = frames.Where(row => row.Opcode == 108).ToArray();
                    TestCheck.That(first.Length == 1, "旧角色先收到未开始期的完整初始化108");
                    Validate108(first[0].Body, false);
                    await clock.WaitForOneShotTimerAsync(start);
                    await context.CommandGate.WaitAsync(); gateHeld = true;
                    clock.Advance(TimeSpan.FromSeconds(1));
                    session.ReturnToRoster(); session.Select(1); session.MarkReady();
                    var selected = session.Selected!;
                    TestCheck.That(session.SelectionEpoch != epoch && selected.Id != initial.Id,
                        "开始边界已经唤醒但仍被gate挡住时，真实切换到另一角色");
                    await town.ServerEventsAsync(context);
                    context.CommandGate.Release(); gateHeld = false;
                    await clock.WaitForOneShotTimerAsync(start.AddDays(30));
                    await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    var written = frames.Where(row => row.Kind == 0 && row.Opcode == 108).ToArray();
                    TestCheck.That(written.Length == 2 && session.Selected!.Id == selected.Id && !stop.IsCancellationRequested,
                        "新角色仅收到自己的当前初始化108，旧边界唤醒不追加过期刷新");
                    Validate108(written[1].Body, true);
                }
            }
            finally
            {
                stream.Release.TrySetResult();
                if (gateHeld) context.CommandGate.Release();
            }
        }

        static void Validate108(byte[] compressed, bool active)
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            byte[] bounded = new byte[32768]; int size = 0;
            while (size < bounded.Length)
            {
                int count = zlib.Read(bounded, size, bounded.Length - size);
                if (count == 0) break;
                size += count;
            }
            using var expected = new MemoryStream();
            using (var writer = new BinaryWriter(expected, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                ushort[] ids = active ? [2037, 2378, 2395, 2532, 266, 2520] : [2037, 2378, 2395, 2532, 266];
                writer.Write(checked((ushort)ids.Length));
                foreach (ushort id in ids) { writer.Write(id); writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write((byte)0); }
                writer.Write((byte)2);
                foreach (ushort id in new ushort[] { 2037, 2395 })
                {
                    writer.Write(id);
                    for (int index = 0; index < 12; index++) writer.Write(id == 2037 && index == 0 ? 8u : 0u);
                }
            }
            TestCheck.That(zlib.ReadByte() == -1 && bounded.AsSpan(0, size).SequenceEqual(expected.ToArray()),
                "独立完整108逐字节保留原目录及24个active数值，仅变更2520目录项");
        }
    }

    sealed class HeldOutput : MemoryStream
    {
        readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => started.Task;
        public void Release() => release.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
