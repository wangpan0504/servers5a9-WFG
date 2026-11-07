using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RaidMovieResponseTests
{
    // 借用已提交资格的原实例；28及返城receipt明确为合成场景输入，不冒充正式Run。
    internal static async Task ReturnAsync(TcpTestResources resources, RaidActor actor, AntonRaidBattle battle, TimeProvider clock)
    {
        var session = actor.Session;
        That(session.Phase == SessionPhase.Town, "场景屏障夹具要求已真实回到业务Town");
        var view = battle.View;
        ushort stateOpcode = battle.Kind == RaidKind.Fiendwar ? (ushort)1523 : (ushort)587;
        var town = new TownProjection(resources.Towns, new CharacterStatComputer(resources.Characters), null,
            resources.ItemResources.EpicPieces, clock: clock);
        foreach (bool queued in new[] { true, false })
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var stream = new HeldReturnStream();
            var frames = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            var gate = new object();
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var sender = new GameConnectionSender(actor.Owner, stream, stop, 0, (kind, opcode, _, body, _) =>
            {
                lock (gate) frames.Add((kind, opcode, body.ToArray()));
                if (kind == 0 && opcode == stateOpcode && body.Span[0] == 3) shown.TrySetResult();
            });
            var context = new GameConnectionContext(new(80, IPAddress.Loopback, stream, stop), actor.Owner, session, sender);
            void Publish() => RaidCombatProjection.PostState(context, view, actor,
                () => session.CanPublishRaidBattle(actor, view), "fixture_raid_scene_movie");
            await sender.Send(0, 28, new byte[10], "fixture_raid_scene_enter");
            Publish();
            await sender.DrainAsync();
            That(sender.DungeonSceneWritten && Snapshot().All(row => row.Opcode != 601 && row.Opcode != stateOpcode),
                $"{battle.Kind}/{queued}：Town已提交但客户端仍在副本时，电影及状态均不得提前写出");
            var receipt = new TownTravelReceipt(session.Selected!, actor.Owner.Generation, session.SelectionEpoch)
                { RaidBattleChange = new(view, view) };
            Task returning;
            if (queued)
            {
                using (sender.QueuePublication(() => session.CanPublishTravel(receipt)))
                    returning = town.ReturnFromFormalDungeonAsync(context, receipt, "fixture_raid_scene_return");
            }
            else returning = town.ReturnFromFormalDungeonAsync(context, receipt, "fixture_raid_scene_return");
            try
            {
                await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                That(queued ? returning.IsCompletedSuccessfully : !returning.IsCompleted,
                    "排队返城只入队，直接返城仍等待实际网络完成");
                Publish(); Publish();
                That(!shown.Task.IsCompleted && Snapshot().All(row => row.Opcode != 601 && row.Opcode != stateOpcode),
                    "实际24被信号持住时，同阶段重复广播也不能越过返城电影屏障");
            }
            finally { stream.Release.TrySetResult(); await returning; }
            try { await shown.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException($"{battle.Kind}/{queued}: movie not written after return; " +
                    $"scene={sender.DungeonSceneWritten} travel={session.CanPublishTravel(receipt)} " +
                    $"finalReturn={session.NeedsFiendwarFinalReturn()} current={battle.IsCurrent(view)} " +
                    $"frames={string.Join(',', Snapshot().Select(row => row.Kind.ToString() + ':' + row.Opcode))}", error);
            }
            await sender.DrainAsync();
            Publish();
            await sender.DrainAsync();
            var written = Snapshot();
            int returned = Array.FindIndex(written, row => row is { Kind: 0, Opcode: 24 });
            int result = Array.FindIndex(written, row => row is { Kind: 0, Opcode: 601 });
            int state = Array.FindIndex(written, row => row.Kind == 0 && row.Opcode == stateOpcode && row.Body[0] == 3);
            That(!sender.DungeonSceneWritten && returned >= 0 && result > returned && state > result &&
                written.Count(row => row is { Kind: 0, Opcode: 601 }) == 1 &&
                written.Count(row => row.Kind == 0 && row.Opcode == stateOpcode && row.Body[0] == 3) == 1,
                $"{battle.Kind}/{queued}：实际24后唯一601与团本原生状态依次写出，后续重放不重建电影");

            (byte Kind, ushort Opcode, byte[] Body)[] Snapshot() { lock (gate) return frames.ToArray(); }
        }
    }

    internal static async Task FailedFinalAckAsync(RaidActor peer, RaidActor last, AntonRaidBattle battle)
    {
        using var peerStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var lastStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var peerStream = new MemoryStream();
        using var lastStream = new MemoryStream();
        int opened = 0;
        await using var peerSender = new GameConnectionSender(peer.Owner, peerStream, peerStop, 0, (kind, opcode, _, body, _) =>
        { if (kind == 0 && opcode == 587 && body.Span.SequenceEqual(new byte[] { 4, 0 })) Interlocked.Increment(ref opened); });
        await using var lastSender = new GameConnectionSender(last.Owner, lastStream, lastStop, 0, static (_, _, _, _, _) => { });
        var peerContext = new GameConnectionContext(new(81, IPAddress.Loopback, peerStream, peerStop), peer.Owner, peer.Session, peerSender);
        var lastContext = new GameConnectionContext(new(82, IPAddress.Loopback, lastStream, lastStop), last.Owner, last.Session, lastSender);
        var view = battle.View;
        RaidCombatProjection.PostState(peerContext, view, peer, () => peer.Session.CanPublishRaidBattle(peer, view), "fixture_peer_movie");
        RaidCombatProjection.PostState(lastContext, view, last, () => last.Session.CanPublishRaidBattle(last, view), "fixture_last_movie");
        await Task.WhenAll(peerSender.DrainAsync(), lastSender.DrainAsync());
        var request = last.Session.RaidMovieSnapshot()!.Value;
        var originalNotice = peer.Session.RaidBattleNoticeArrived;
        peer.Session.RaidBattleNoticeArrived = (latest, actor) => RaidCombatProjection.PostState(peerContext, latest, actor,
            () => peer.Session.CanPublishRaidBattle(actor, latest), "fixture_peer_after_failed_ack");
        try
        {
            await lastStop.CancelAsync();
            That(!RaidCombatProjection.Movie(lastContext, last, battle, request.View, request.Event, true),
                "末位确认遇真实已关闭发送队列仍返回关闭连接，不冒充ACK成功");
            await peerSender.DrainAsync();
            That(battle.View.AntonCardsReady && Volatile.Read(ref opened) == 1,
                "末位真实电影事实已提交，ACK队列失败仍锁外广播，另一接收者实际写出唯一开牌587");
        }
        finally { peer.Session.RaidBattleNoticeArrived = originalNotice; }
    }

    sealed class HeldReturnStream : MemoryStream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length >= 3 && buffer.Span[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[1..]) == 24)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
