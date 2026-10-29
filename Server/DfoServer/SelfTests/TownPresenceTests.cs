using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using System.Buffers.Binary;
using DfoServer.Network;
using DfoServer.Persistence;

namespace DfoServer;

internal static class TownPresenceTests
{
    sealed class HeldStream : Stream
    {
        readonly MemoryStream data = new();
        readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly bool held;
        readonly bool fail;
        readonly ushort? holdOpcode;
        public HeldStream(bool held = false, bool fail = false, ushort? holdOpcode = null)
        {
            this.held = held;
            this.fail = fail;
            this.holdOpcode = holdOpcode;
            if (!held && holdOpcode is null)
                release.SetResult();
        }

        public Task WriteStarted => writeStarted.Task;

        public void Release() => release.TrySetResult();
        public byte[] Bytes()
        {
            lock (data)
                return data.ToArray();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (held)
                await release.Task.WaitAsync(token);
            if (fail)
                throw new ApplicationException("owned write failure fixture");
            if (holdOpcode is { } opcode && BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[1..]) == opcode)
            {
                // 模拟半帧已经送达，WriteAsync尚未完成；不能把源失效前的可见性检查当成原子发送。
                int prefix = buffer.Length / 2;
                lock (data)
                    data.Write(buffer.Span[..prefix]);
                writeStarted.TrySetResult();
                await release.Task.WaitAsync(token);
                lock (data)
                    data.Write(buffer.Span[prefix..]);
                return;
            }

            lock (data)
                data.Write(buffer.Span);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long n, SeekOrigin o) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    }

    static (byte Kind, ushort Opcode, byte[] Body)[] Frames(HeldStream stream)
    {
        byte[] raw = stream.Bytes();
        var result = new List<(byte, ushort, byte[])>();
        int at = 0;
        ushort sequence = 0;
        while (at < raw.Length)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(at + 3));
            if (size < 16 || at + size > raw.Length || BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 11)) != sequence++)
                throw new Exception("sender frame sequence/length invalid");
            if (GameFrame.Crc(raw.AsSpan(at + 11, size - 11)) != BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at + 7)))
                throw new Exception("sender frame CRC invalid");
            result.Add((raw[at], BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(at + 1)), raw[(at + 16)..(at + size)]));
            at += size;
        }

        return result.ToArray();
    }

    public static async Task Run(TownCatalog towns)
    {
        int assertions = Count;

        var owners = new AccountOwners();
        var presence = new TownPresence(towns);
        string root = Path.Combine(Path.GetTempPath(), "dfo-presence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var aCancel = new CancellationTokenSource();
        using var bCancel = new CancellationTokenSource();
        using var a = owners.Acquire("alpha", () => aCancel.Cancel());
        using var b = owners.Acquire("beta", () => bCancel.Cancel());
        using var aStream = new HeldStream(true);
        using var bStream = new HeldStream();
        await using var aSend = new GameConnectionSender(a, aStream, aCancel, 0, (_, _, _, _, _) =>
        {
        });
        await using var bSend = new GameConnectionSender(b, bStream, bCancel, 0, (_, _, _, _, _) =>
        {
        });
        try
        {
            var repo = new CharacterRepository(Path.Combine(root, "game.db"));
            var ca = repo.Create(a, new("PresenceA", towns.Room, towns.InitialReturn));
            var cb = repo.Create(b, new("PresenceB", towns.Room, towns.InitialReturn));
            presence.Join(a, aSend, ca);
            presence.Join(b, bSend, cb);
            presence.Publish(a, aSend, 119, CreaturePackets.Message(3, ca.Id, "private"), "test_creature_private");
            await bSend.Send(1, 1347, [1], "barrier");
            That(Frames(bStream).All(f => f.Opcode == 1347), "different accounts have private rooms");
            presence.Remove(aSend);
            presence.Remove(bSend);
            await bSend.ClearScene();
            Task blocked = aSend.Send(1, 1347, [1], "held-own-frame");
            ca = ca with
            {
                Position = towns.InitialReturn
            };
            cb = cb with
            {
                Position = towns.InitialReturn
            };
            presence.Join(a, aSend, ca, adventureName: "AlphaGroup");
            presence.Join(b, bSend, cb, adventureName: "BetaGroup");
            byte[] creatureMessage = CreaturePackets.Message(3, cb.Id, "hello");
            presence.Publish(b, bSend, 119, creatureMessage, "test_creature_public");
            for (int i = 0; i < 1000; i++)
            {
                cb = cb with
                {
                    Position = cb.Position with
                    {
                        X = (ushort)(623 + i % 10)
                    }
                };
                presence.Move(b, bSend, cb);
            }

            That(!blocked.IsCompleted && aSend.PendingMoves == 1, "slow receiver has one coalesced position instead of unbounded movement frames");
            await bSend.Send(1, 1347, [1], "unblocked-peer").WaitAsync(TimeSpan.FromSeconds(2));
            That(Frames(bStream).Any(f => f.Opcode == 2), "slow recipient does not block other sender or account gate");
            aStream.Release();
            await blocked;
            await aSend.Send(1, 1347, [1], "drain");
            var frames = Frames(aStream);
            Bytes(frames.Single(frame => frame.Opcode == 119).Body, creatureMessage,
                "宠物原生通知沿既有同屏队列发布，同频道公共场景收到完整正文");
            var position = frames.Last(f => f.Opcode == 22).Body;
            That(BinaryPrimitives.ReadUInt16LittleEndian(position) == cb.Id && BinaryPrimitives.ReadUInt16LittleEndian(position.AsSpan(2)) == 632, "latest committed position projection survives coalescing");
            That(frames.Count(f => f.Opcode == 2) == 1, "initial peer actor published once");
            Bytes(frames.Single(f => f.Opcode == 2).Body.AsSpan(56, 9),
                System.Text.Encoding.ASCII.GetBytes("BetaGroup"), "同屏昵称来自发送者成员快照，不读取接收者账号");
            cb = cb with
            {
                Inventory = new([new(1, new(InventoryListType.Equipment, 12), new(101010912, ItemKind.Equipment, 999999998))])
            };
            presence.Refresh(b, bSend, cb);
            await aSend.Send(1, 1347, [1], "after-equipment-refresh");
            var refreshed = Frames(aStream);
            That(refreshed.Count(f => f.Opcode == 2) == 2 && refreshed.All(f => f.Opcode != 6), "known peer receives appearance without leave or duplicate join");
            That(refreshed.Last(f => f.Opcode == 2).Body.SequenceEqual(TownPackets.Actor(cb, 0, adventureName: "BetaGroup")),
                "换装保留当前成员昵称并使用实际提交的装备");
            presence.Refresh(b, bSend, cb, "NewBeta");
            await aSend.Send(1, 1347, [1], "after-name-refresh");
            Bytes(Frames(aStream).Last(f => f.Opcode == 2).Body,
                TownPackets.Actor(cb, 0, adventureName: "NewBeta"), "昵称更新复用成员身份和外观刷新，不离场或重复加入");
            presence.Remove(bSend);
            await aSend.Send(1, 1347, [1], "after-leave");
            That(Frames(aStream).Last(f => f.Opcode == 6).Body.SequenceEqual(TownPackets.RemoveActor(cb.Id)), "departure removes actual visible actor");
            presence.Remove(bSend);
            await aSend.Send(1, 1347, [1], "duplicate-leave");
            That(Frames(aStream).Count(f => f.Opcode == 6) == 1, "duplicate cleanup does not emit extra deletion");
            presence.Join(b, bSend, cb, new(0, 1));
            presence.Publish(b, bSend, 119, creatureMessage, "test_creature_other_channel");
            presence.Publish(a, aSend, 119, creatureMessage, "test_creature_other_channel_reverse");
            await aSend.Send(1, 1347, [1], "channel-message-barrier");
            await bSend.Send(1, 1347, [1], "channel-message-barrier");
            That(Frames(aStream).Count(frame => frame.Opcode == 119) == 1 && Frames(bStream).All(frame => frame.Opcode != 119),
                "频道不同的宠物通知双向隔离，不创建第二套可见性索引");
            presence.Join(b, bSend, cb);
            await aSend.Send(1, 1347, [1], "reenter");
            await aSend.ClearScene();
            That(Frames(aStream).Count(f => f.Opcode == 6) == 2, "scene clear removes recipient known actors before native transition");
            using var replacementCancel = new CancellationTokenSource();
            using var replacement = owners.Acquire("alpha", () => replacementCancel.Cancel());
            using var newStream = new HeldStream();
            await using var next = new GameConnectionSender(replacement, newStream, replacementCancel, 0, (_, _, _, _, _) =>
            {
            });
            presence.Join(replacement, next, ca);
            presence.Remove(aSend);
            That(presence.Count == 2, "old connection cleanup preserves replacement membership");
            bool rejected = false;
            try
            {
                presence.Move(a, aSend, ca);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }

            That(rejected, "stale generation cannot publish movement");
            await next.Send(1, 1347, [1], "new-generation-ready");
            presence.Remove(next);
            presence.Remove(bSend);
            That(presence.Count == 0, "all members released at connection end");
            using var gCancel = new CancellationTokenSource();
            using var dCancel = new CancellationTokenSource();
            using var g = owners.Acquire("gamma", () => gCancel.Cancel());
            using var d = owners.Acquire("delta", () => dCancel.Cancel());
            using var gStream = new HeldStream(true);
            using var dStream = new HeldStream();
            await using var gSend = new GameConnectionSender(g, gStream, gCancel, 0, (_, _, _, _, _) =>
            {
            });
            await using var dSend = new GameConnectionSender(d, dStream, dCancel, 0, (_, _, _, _, _) =>
            {
            });
            var cg = repo.Create(g, new("PresenceG", towns.Room, towns.InitialReturn)) with
            {
                Position = towns.InitialReturn
            };
            var cd = repo.Create(d, new("PresenceD", towns.Room, towns.InitialReturn)) with
            {
                Position = towns.InitialReturn
            };
            Task waiting = gSend.Send(1, 1347, [1], "pending-source-test");
            presence.Join(g, gSend, cg);
            presence.Join(d, dSend, cd);
            presence.Move(d, dSend, cd with { Position = cd.Position with { X = 630 } });
            presence.Publish(d, dSend, 119, CreaturePackets.Message(3, cd.Id, "expired"), "test_creature_expired_source");
            using var newerDelta = owners.Acquire("delta", () =>
            {
            });
            presence.Remove(dSend, [1, 1, (byte)cd.Id, (byte)(cd.Id >> 8)]);
            gStream.Release();
            await waiting;
            await gSend.Send(1, 1347, [1], "drain-expired-source");
            That(Frames(gStream).All(f => f.Opcode == 1347), "expired queued source neither creates ghost actor nor deletes an unseen actor");
            presence.Remove(gSend);
            using var hCancel = new CancellationTokenSource();
            using var h = owners.Acquire("epsilon", () => hCancel.Cancel());
            using var hStream = new HeldStream(true);
            await using var hSend = new GameConnectionSender(h, hStream, hCancel, 0, (_, _, _, _, _) =>
            {
            });
            var ch = repo.Create(h, new("PresenceH", towns.Room, towns.InitialReturn)) with
            {
                Position = towns.InitialReturn
            };
            waiting = hSend.Send(1, 1347, [1], "pending-view-test");
            presence.Join(h, hSend, ch);
            presence.Join(replacement, next, ca);
            Task cleared = hSend.ClearScene();
            presence.Remove(hSend);
            hStream.Release();
            await waiting;
            await cleared;
            That(Frames(hStream).All(f => f.Opcode == 1347), "obsolete receiver epoch drops queued peer entry before scene clear");
            presence.Remove(next);
            That(presence.Count == 0, "adversarial scenarios release membership references");
            using var flightTargetCancel = new CancellationTokenSource();
            using var flightSourceCancel = new CancellationTokenSource();
            using var flightTarget = owners.Acquire("flight-target", () => flightTargetCancel.Cancel());
            using var flightSource = owners.Acquire("flight-source", () => flightSourceCancel.Cancel());
            using var flightTargetStream = new HeldStream(holdOpcode: 2);
            using var flightSourceStream = new HeldStream();
            await using var flightTargetSend = new GameConnectionSender(flightTarget, flightTargetStream, flightTargetCancel, 0, (_, _, _, _, _) =>
            {
            });
            await using var flightSourceSend = new GameConnectionSender(flightSource, flightSourceStream, flightSourceCancel, 0, (_, _, _, _, _) =>
            {
            });
            var flightTargetCharacter = repo.Create(flightTarget, new("FlightT", towns.Room, towns.InitialReturn)) with
            {
                Position = towns.InitialReturn
            };
            var flightSourceCharacter = repo.Create(flightSource, new("FlightS", towns.Room, towns.InitialReturn));
            flightSourceCharacter = repo.Save(flightSource, flightSourceCharacter, towns.InitialReturn, towns.InitialReturn);
            presence.Join(flightTarget, flightTargetSend, flightTargetCharacter);
            presence.Join(flightSource, flightSourceSend, flightSourceCharacter);
            await flightTargetStream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(2));
            That(flightTargetStream.Bytes().Length == (16 + TownPackets.Actor(flightSourceCharacter, 0).Length) / 2, "remote Actor is already partially written when source becomes stale");
            using var flightReplacementCancel = new CancellationTokenSource();
            using var flightReplacement = owners.Acquire("flight-source", () => flightReplacementCancel.Cancel());
            presence.Remove(flightSourceSend);
            using var flightReplacementStream = new HeldStream();
            await using var flightReplacementSend = new GameConnectionSender(flightReplacement, flightReplacementStream, flightReplacementCancel, 0, (_, _, _, _, _) =>
            {
            });
            var flightReplacementCharacter = repo.Save(flightReplacement, flightSourceCharacter, flightSourceCharacter.Position with { X = 633 }, towns.InitialReturn);
            presence.Join(flightReplacement, flightReplacementSend, flightReplacementCharacter);
            flightTargetStream.Release();
            await flightTargetSend.Send(1, 1347, [1], "drain-in-flight-replacement").WaitAsync(TimeSpan.FromSeconds(2));
            var flightFrames = Frames(flightTargetStream);
            That(flightFrames.Select(f => f.Opcode).SequenceEqual(new ushort[] { 2, 6, 2, 3, 23, 22, 1347 }), "in-flight old Actor is removed without stale State, Area or Position before replacement snapshot");
            That(flightFrames[1].Body.SequenceEqual(TownPackets.RemoveActor(flightSourceCharacter.Id)) &&
                flightFrames[4].Body.SequenceEqual(TownPackets.Position(flightReplacementCharacter, true)) &&
                flightFrames[5].Body.SequenceEqual(TownPackets.Position(flightReplacementCharacter, false)),
                "旧Actor清理、新成员区域入场与已提交坐标均保留复用的角色ID");
            // 旧连接的finally可晚于新成员入场；再次清理不得删除新membership，后续位置仍能送达。
            presence.Remove(flightSourceSend);
            flightReplacementCharacter = repo.Save(flightReplacement, flightReplacementCharacter, flightReplacementCharacter.Position with { X = 634 }, towns.InitialReturn);
            presence.Move(flightReplacement, flightReplacementSend, flightReplacementCharacter);
            await flightTargetSend.Send(1, 1347, [1], "drain-after-old-finally").WaitAsync(TimeSpan.FromSeconds(2));
            flightFrames = Frames(flightTargetStream);
            That(flightFrames.Count(f => f.Opcode == 6) == 1 && flightFrames[^2].Opcode == 22 && flightFrames[^2].Body.SequenceEqual(TownPackets.Position(flightReplacementCharacter, false)), "late old connection cleanup preserves replacement membership and its movement");
            presence.Remove(flightTargetSend);
            presence.Remove(flightReplacementSend);
            That(presence.Count == 0, "in-flight takeover fixture releases all memberships");
            using var failedCancel = new CancellationTokenSource();
            using var failedOwner = owners.Acquire("send-failure", () => failedCancel.Cancel());
            using var failedStream = new HeldStream(fail: true);
            await using var failedSend = new GameConnectionSender(failedOwner, failedStream, failedCancel, 0, (_, _, _, _, _) =>
            {
            });
            bool failed = false;
            try
            {
                await failedSend.Send(1, 1347, [1], "write-failure");
            }
            catch (IOException)
            {
                failed = true;
            }

            That(failed && failedCancel.IsCancellationRequested, "unexpected stream failure cancels connection and completes reliable waiter");
            using var fullCancel = new CancellationTokenSource();
            using var fullOwner = owners.Acquire("send-full", () => fullCancel.Cancel());
            using var fullStream = new HeldStream(true);
            await using var fullSend = new GameConnectionSender(fullOwner, fullStream, fullCancel, 0, (_, _, _, _, _) =>
            {
            });
            var pending = Enumerable.Range(0, 260).Select(_ => fullSend.Send(1, 1347, [1], "overflow-fixture")).ToArray();
            try
            {
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
            }

            That(fullCancel.IsCancellationRequested && pending.All(t => t.IsCompleted), "queue overflow cancels only that receiver and releases every reliable waiter");
            // 广播入队触发慢连接取消时，异步清理不能妨碍其他接收者投递。
            using var slowCancel = new CancellationTokenSource();
            using var healthyCancel = new CancellationTokenSource();
            using var sourceCancel = new CancellationTokenSource();
            using var slowOwner = owners.Acquire("index-slow", slowCancel.Cancel);
            using var healthyOwner = owners.Acquire("index-healthy", healthyCancel.Cancel);
            using var sourceOwner = owners.Acquire("index-source", sourceCancel.Cancel);
            using var slowStream = new HeldStream(true);
            using var healthyStream = new HeldStream();
            await using var slowSend = new GameConnectionSender(slowOwner, slowStream, slowCancel, 0, (_, _, _, _, _) => { });
            await using var healthySend = new GameConnectionSender(healthyOwner, healthyStream, healthyCancel, 0, (_, _, _, _, _) => { });
            await using var sourceSend = new GameConnectionSender(sourceOwner, Stream.Null, sourceCancel, 0, (_, _, _, _, _) => { });
            using var slowCleanup = slowCancel.Token.Register(() => presence.Remove(slowSend));
            var shared = ca with { Id = 65000, Position = towns.InitialReturn };
            Task slowWrite = slowSend.Send(1, 1347, [1], "reentrant-held-write");
            presence.Join(slowOwner, slowSend, shared);
            presence.Join(healthyOwner, healthySend, shared with { Id = 65001 });
            shared = shared with { Id = 65002 };
            presence.Join(sourceOwner, sourceSend, shared);
            for (int i = 0; i < 270; i++)
            {
                presence.Refresh(sourceOwner, sourceSend, shared);
                await healthySend.Send(1, 1347, [1], "reentrant-healthy-drain").WaitAsync(TimeSpan.FromSeconds(2));
            }
            try { await slowWrite.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
            await slowSend.DisposeAsync();
            That(slowCancel.IsCancellationRequested && presence.Count == 2 && !healthyCancel.IsCancellationRequested,
                "reentrant cancellation removes only the saturated scene member");
            That(Frames(healthyStream).Count(frame => frame.Opcode == 2) == 272,
                "healthy peer receives initial actors and every appearance update during reentrant removal");
            presence.Remove(sourceSend);
            presence.Remove(healthySend);
            That(presence.Count == 0, "reentrant fixture releases scene and actor memberships");
            var phase = towns.Areas.Values.SelectMany(area => area.Shifts.Where(s => s.Quest > 0 && s.Index >= 0 && s.Index < area.Phases.Length).Select(s => new { Area = area, Quest = s.Quest })).First(p => !p.Area.Default.IsRoom && !p.Area.Map(new HashSet<int> { p.Quest }).IsRoom && p.Area.Default.Path != p.Area.Map(new HashSet<int> { p.Quest }).Path);
            using var pCancel = new CancellationTokenSource();
            using var qCancel = new CancellationTokenSource();
            using var p = owners.Acquire("phase-one", () => pCancel.Cancel());
            using var q = owners.Acquire("phase-two", () => qCancel.Cancel());
            using var pStream = new HeldStream();
            using var qStream = new HeldStream();
            await using var pSend = new GameConnectionSender(p, pStream, pCancel, 0, (_, _, _, _, _) =>
            {
            });
            await using var qSend = new GameConnectionSender(q, qStream, qCancel, 0, (_, _, _, _, _) =>
            {
            });
            var cp = repo.Create(p, new("PresenceP", towns.Room, towns.InitialReturn)) with
            {
                Position = new(phase.Area.Town, phase.Area.Area, 450, 234)
            };
            var cq = repo.Create(q, new("PresenceQ", towns.Room, towns.InitialReturn)) with
            {
                Position = cp.Position,
                CompletedQuests = [phase.Quest]
            };
            presence.Join(p, pSend, cp);
            presence.Join(q, qSend, cq);
            presence.Publish(p, pSend, 379, [2, 1, (byte)cp.Id, (byte)(cp.Id >> 8)], "phase_effect");
            await pSend.Send(1, 1347, [1], "phase-barrier");
            await qSend.Send(1, 1347, [1], "phase-barrier");
            That(Frames(pStream).All(f => f.Opcode == 1347) && Frames(qStream).All(f => f.Opcode == 1347), "same town/area with different PVF phases does not share actors");
            presence.Remove(pSend, [1, 1, (byte)cp.Id, (byte)(cp.Id >> 8)]);
            presence.Remove(qSend);
            await qSend.Send(1, 1347, [1], "phase-effect-barrier");
            That(Frames(qStream).All(f => f.Opcode == 1347), "不同PVF阶段不接收传送到达或出发特效");
            That(presence.Count == 0, "phase fixture cleans up both memberships");
            await pSend.ClearScene();
            await qSend.ClearScene();
            using var rCancel = new CancellationTokenSource();
            using var r = owners.Acquire("phase-three", rCancel.Cancel);
            using var rStream = new HeldStream();
            await using var rSend = new GameConnectionSender(r, rStream, rCancel, 0, (_, _, _, _, _) => { });
            var cr = cp with { Id = 65000 };
            presence.Join(p, pSend, cp);
            presence.Join(q, qSend, cq);
            presence.Join(r, rSend, cr);
            await pSend.Send(1, 1347, [1], "phase-rejoin-barrier");
            await qSend.Send(1, 1347, [1], "phase-rejoin-barrier");
            await rSend.Send(1, 1347, [1], "phase-rejoin-barrier");
            int pAt = Frames(pStream).Length, qAt = Frames(qStream).Length, rAt = Frames(rStream).Length;
            cp = cp with { CompletedQuests = [phase.Quest], Position = cp.Position with { X = 451 } };
            presence.Move(p, pSend, cp);
            await pSend.Send(1, 1347, [1], "phase-shift-barrier");
            await qSend.Send(1, 1347, [1], "phase-shift-barrier");
            await rSend.Send(1, 1347, [1], "phase-shift-barrier");
            var pAdded = Frames(pStream).Skip(pAt).ToArray();
            var qAdded = Frames(qStream).Skip(qAt).ToArray();
            var rAdded = Frames(rStream).Skip(rAt).ToArray();
            That(pAdded.Any(frame => frame.Opcode == 2) && qAdded.Any(frame => frame.Opcode == 2) && presence.Count == 3,
                "同区阶段任务完成后移动改入新PVF场景，双方同屏且不断线");
            That(pAdded.Where(frame => frame.Opcode == 3).Any(frame => frame.Body.SequenceEqual(TownPackets.HideTownEntity(cr.Id))) &&
                rAdded.Where(frame => frame.Opcode == 3).Single().Body.SequenceEqual(TownPackets.HideTownEntity(cp.Id)) &&
                pAdded.Concat(qAdded).Concat(rAdded).All(frame => frame.Opcode != 6) && rAdded.All(frame => frame.Opcode != 2),
                "迁场双方隐藏旧阶段实体且不销毁Actor或队伍引用，旧阶段没有新入场");
            That(presence.CanInteract(p,cp.Id,q,cq.Id) && !presence.CanInteract(p,cp.Id,r,cr.Id),
                "迁场只切换PVF阶段关系，不把同town/area不同阶段的角色视为同屏");
            pAt = Frames(pStream).Length;
            qAt = Frames(qStream).Length;
            rAt = Frames(rStream).Length;
            presence.Refresh(q,qSend,cq with { CompletedQuests = [] },broadcast: false);
            await pSend.Send(1,1347,[1],"phase-refresh-barrier");
            await qSend.Send(1,1347,[1],"phase-refresh-barrier");
            await rSend.Send(1,1347,[1],"phase-refresh-barrier");
            That(Frames(pStream).Skip(pAt).Any(frame => frame.Opcode == 3 && frame.Body.SequenceEqual(TownPackets.HideTownEntity(cq.Id))) &&
                Frames(qStream).Skip(qAt).Any(frame => frame.Opcode == 2) && Frames(rStream).Skip(rAt).Any(frame => frame.Opcode == 2) &&
                Frames(pStream).Skip(pAt).Concat(Frames(qStream).Skip(qAt)).Concat(Frames(rStream).Skip(rAt)).All(frame => frame.Opcode != 6) &&
                presence.CanInteract(q,cq.Id,r,cr.Id) && !presence.CanInteract(p,cp.Id,q,cq.Id),
                "静默外观刷新也先迁场，旧阶段双向隐藏，新阶段正常重逢且保留Actor");
            presence.Remove(pSend);
            presence.Remove(qSend);
            presence.Remove(rSend);
            That(presence.Count == 0, "阶段迁移夹具释放双方成员");
            foreach (string scenario in new[] { "current", "stale-source", "stale-view" })
            foreach (bool partyPreparation in new[] { false, true })
            foreach (bool sceneTravel in new[] { false, true })
            {
                using var targetCancel = new CancellationTokenSource();
                using var effectSourceCancel = new CancellationTokenSource();
                using var targetOwner = owners.Acquire("effect-target-" + scenario, targetCancel.Cancel);
                using var effectOwner = owners.Acquire("effect-source-" + scenario, effectSourceCancel.Cancel);
                using var targetStream = new HeldStream(holdOpcode: 1347);
                await using var targetSender = new GameConnectionSender(targetOwner, targetStream, targetCancel, 0, (_, _, _, _, _) => { });
                await using var effectSender = new GameConnectionSender(effectOwner, Stream.Null, effectSourceCancel, 0, (_, _, _, _, _) => { });
                var effectCharacter = ca with { Id = 65000, Position = towns.InitialReturn };
                presence.Join(targetOwner, targetSender, ca with { Id = 65001, Position = towns.InitialReturn });
                presence.Join(effectOwner, effectSender, effectCharacter);
                await targetSender.Send(1, 65000, [], "effect-before-hold");
                Task heldEffect = targetSender.Send(1, 1347, [1], "effect-held-receiver");
                await targetStream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(2));
                byte[]? destination = sceneTravel ? new byte[] { 232, 253, 38, 2, 61, 4, 249, 0, 0, 1 } : null;
                presence.Remove(effectSender, [1, 1, 232, 253], partyPreparation ? new byte[] { 3, 1, 232, 253 } : null, destination);
                That(targetSender.PendingBytes == 17 + 20 + (sceneTravel ? 26 : 18) + (partyPreparation ? 20 : 0),
                    "准备、出发与普通切区/真正离场帧完整计入慢接收者字节预算");
                using var replacementOwner = scenario == "stale-source" ? owners.Acquire(effectOwner.Account, () => { }) : null;
                Task clearEffect = Task.CompletedTask;
                if (scenario == "current")
                {
                    presence.Join(effectOwner, effectSender, effectCharacter);
                    presence.Publish(effectOwner, effectSender, 379, [2, 1, 232, 253], "effect-after-rejoin");
                }
                else if (scenario == "stale-view")
                {
                    presence.Remove(targetSender);
                    clearEffect = targetSender.ClearScene();
                }
                targetStream.Release();
                await heldEffect.WaitAsync(TimeSpan.FromSeconds(2));
                await clearEffect.WaitAsync(TimeSpan.FromSeconds(2));
                await targetSender.Send(1, 65000, [], "effect-after-hold");
                var effectFrames = Frames(targetStream).SkipWhile(frame => frame.Opcode != 1347).Skip(1).ToArray();
                ushort departureOpcode = sceneTravel ? (ushort)23 : (ushort)6;
                That(effectFrames.Select(frame => frame.Opcode).SequenceEqual(scenario == "current"
                    ? (partyPreparation ? new ushort[] { 379, 379, departureOpcode, 2, 3, 23, 22, 379, 65000 } : new ushort[] { 379, departureOpcode, 2, 3, 23, 22, 379, 65000 })
                    : new ushort[] { scenario == "stale-view" ? (ushort)6 : departureOpcode, 65000 }),
                    "慢接收者保留旧Actor的出发→离场→新入场→到达顺序；过期源或目标视图不播放特效：" + scenario);
                if (sceneTravel && scenario != "stale-view")
                    Bytes(effectFrames.First(frame => frame.Opcode == 23).Body, destination!, "切区离场沿用已提交目标，不读取重入后的源位置");
                That(!targetCancel.IsCancellationRequested && targetSender.PendingBytes == 0,
                    "特效投递及失效跳过释放全部队列预算，不因目标慢写阻塞源成员迁移");
                presence.Remove(effectSender);
                presence.Remove(targetSender);
            }
        }
        finally
        {
            presence.Remove(aSend);
            presence.Remove(bSend);
            Directory.Delete(root, true);
        }

        Console.WriteLine($"PASS {Count - assertions} town presence, queue, epoch and cleanup assertions");
    }
}
