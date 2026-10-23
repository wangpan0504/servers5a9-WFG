using System.Net;
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
        finally { Directory.Delete(root, recursive: true); }
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
