using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FiendwarKeyLeaseTests
{
    internal static void RunCore()
    {
        int before = Count;
        var run = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f001");
        var scene = new FiendwarKeyLeaseScene(run, 291100339, 123, 456, 0x201, 0, 17);
        var ledger = new FiendwarObjectKeyLeases(run);
        var offer = ledger.Offer(41, scene, 100);
        var candidate = offer with { Kind = FiendwarKeyLeaseKind.Candidates, Status = 0,
            Keys = Enumerable.Range(1000, 64).Select(value => (ushort)value).ToImmutableArray() };
        That(ledger.Grant(41, candidate with { Lease = 0 }, 100, _ => true) is null,
            "准确当前batch也不保留旧零租约旁路");
        var grant = ledger.Grant(41, candidate, 101, key => key != 1000)!;
        That(grant.Keys.Length == 16 && grant.Keys[0] == 1001 && !grant.Keys.Contains((ushort)1000),
            "候选先通过静态/Ground守卫，再按原顺序授予有限互斥小批");
        That(ledger.Grant(41, candidate, 102, _ => throw new InvalidOperationException())!.Keys.SequenceEqual(grant.Keys),
            "重复本批候选重放原grant，不重复分配或重新运行守卫");
        That(!ledger.CanUse(41, grant.Lease, grant.Keys[0]), "grant未ready前不能登记原生召唤");
        That(ledger.Confirm(41, grant with { Kind = FiendwarKeyLeaseKind.Ready, Keys = [] }, 103), "准确ready开放本成员16键");
        That(ledger.CanUse(41, grant.Lease, grant.Keys[0]) && !ledger.CanUse(42, grant.Lease, grant.Keys[0]),
            "租约只能由原成员连接采用");

        var second = ledger.Offer(42, scene, 104);
        var other = ledger.Grant(42, candidate with { Handshake = second.Handshake, Lease = second.Lease }, 105, _ => true)!;
        That(other.Keys.Length == 16 && !other.Keys.Intersect(grant.Keys).Any() && other.Lease != grant.Lease,
            "不同原生池提出相同候选，全组账本仍授予不相交租约");
        foreach (ushort key in grant.Keys.Take(9)) ledger.Consume(41, grant.Lease, key);
        That(ledger.Available(41) == 7 && ledger.Contains(grant.Keys[0]) && !ledger.CanUse(41, grant.Lease, grant.Keys[0]),
            "已用键保留墓碑和Ground占位，低水位只计算未用键");
        Reject<InvalidOperationException>(() => ledger.Consume(41, grant.Lease, grant.Keys[0]), "同一原生键不能被两次采用");

        var nextScene = scene with { Serial = 3, Epoch = 18, Map = 124 };
        var update = ledger.Offer(41, nextScene, 106);
        That(ledger.CanUse(41, grant.Lease, grant.Keys[^1]) && !ledger.Confirm(41,
            grant with { Kind = FiendwarKeyLeaseKind.Ready, Keys = [] }, 107),
            "同Run换房保留已ready键，旧scene ready不能污染新batch");
        var renewal = ledger.Refill(41, update with { Kind = FiendwarKeyLeaseKind.Refill, Status = 0,
            Handshake = Guid.NewGuid() }, 108)!;
        That(renewal.Handshake != update.Handshake && renewal.Lease == grant.Lease, "续租创建新batch而保留原成员lease");
        var recycledCandidates = renewal with { Kind = FiendwarKeyLeaseKind.Candidates, Status = 0,
            Keys = ImmutableArray.Create(grant.Keys[0]).AddRange(Enumerable.Range(2000, 63).Select(value => (ushort)value)) };
        var add = ledger.Grant(41, recycledCandidates, 109, _ => true)!;
        That(add.Keys.Length == 9 && !ledger.CanUse(41, grant.Lease, add.Keys[0]), "续租只补足16，新增键仍须ready");
        That(!add.Keys.Contains(grant.Keys[0]) && ledger.Contains(grant.Keys[0]),
            "原生回收候选混入已用键时逐项跳过墓碑，仍授有效新键，不整批超时");
        That(ledger.Grant(41, recycledCandidates, 109, _ => throw new InvalidOperationException())!.Keys.SequenceEqual(add.Keys),
            "含墓碑候选的重发仍精确重放原grant，不重复分配");
        That(ledger.Confirm(41, add with { Kind = FiendwarKeyLeaseKind.Ready, Keys = [] }, 110) && ledger.Available(41) == 16,
            "续租ready不重置原墓碑");
        var partialOffer = ledger.Offer(43, scene, 200);
        var partial = ledger.Grant(43, partialOffer with { Kind = FiendwarKeyLeaseKind.Candidates, Status = 0, Keys = [3000] }, 201, _ => true)!;
        That(!ledger.Confirm(43, partial with { Kind = FiendwarKeyLeaseKind.Ready, Keys = [] }, 202) &&
            !ledger.CanUse(43, partial.Lease, 3000), "不足初始容量的ready不提前开放部分新键");
        That(ledger.CancelBatch(43, partial with { Kind = FiendwarKeyLeaseKind.Cancel, Keys = [] }) && ledger.Contains(3000),
            "已授键取消后仍保守占位，避免在途ready与Ground复用");
        var timeout = ledger.Offer(44, scene, 203);
        That(ledger.Grant(44, timeout with { Kind = FiendwarKeyLeaseKind.Candidates, Status = 0, Keys = [3001] },
            10203, _ => true) is null && !ledger.Contains(3001), "超时边界不能增加租约占位");

        byte[] wire = CandidateWire(candidate);
        var parsed = FiendwarKeyLeaseRequests.Parse(wire);
        That(parsed.Handshake == candidate.Handshake && parsed.Scene == scene && parsed.Lease == candidate.Lease &&
            parsed.Keys.SequenceEqual(candidate.Keys), "独立编码FWL1完整80B身份与16位候选");
        Bytes(FiendwarKeyLeasePackets.Build(candidate), wire, "Builder每一字段与独立wire预期一致");
        byte[] zeroLease = (byte[])wire.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(zeroLease.AsSpan(40), 0);
        Reject<InvalidDataException>(() => FiendwarKeyLeaseRequests.Parse(zeroLease), "FWL1拒绝零租约");
        byte[] negativeEpoch = (byte[])wire.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(negativeEpoch.AsSpan(68), -1);
        Reject<InvalidDataException>(() => FiendwarKeyLeaseRequests.Parse(negativeEpoch), "FWL1拒绝负epoch");
        foreach (int length in new[] { 0, 79, wire.Length - 1, wire.Length + 1 })
        {
            byte[] bad = new byte[length];
            wire.AsSpan(0, Math.Min(wire.Length, length)).CopyTo(bad);
            Reject<InvalidDataException>(() => FiendwarKeyLeaseRequests.Parse(bad), "拒绝有效报文的截断和尾字节");
        }
        foreach (int at in new[] { 76, 77, 78, 79 })
        {
            byte[] bad = (byte[])wire.Clone(); bad[at] = 1;
            Reject<InvalidDataException>(() => FiendwarKeyLeaseRequests.Parse(bad), "保留字段非零拒绝");
        }
        foreach (ushort key in new ushort[] { 0, ushort.MaxValue, candidate.Keys[1] })
        {
            byte[] bad = (byte[])wire.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(80), key);
            Reject<InvalidDataException>(() => FiendwarKeyLeaseRequests.Parse(bad), "拒绝哨兵和重复候选");
        }
        var repeated = new FiendwarObjectKeyLeases(run);
        var batch = repeated.Offer(51, scene, 0);
        var used = new HashSet<ushort>();
        for (int round = 0; round < 21; round++)
        {
            var granted = repeated.Grant(51, batch with { Kind = FiendwarKeyLeaseKind.Candidates, Status = 0,
                Keys = Enumerable.Range(4000 + round * 16, 16).Select(value => (ushort)value).ToImmutableArray() }, round * 10 + 1, _ => true)!;
            That(granted.Keys.Length == 16 && repeated.Confirm(51,
                granted with { Kind = FiendwarKeyLeaseKind.Ready, Keys = [] }, round * 10 + 2), "后续合法轮次可继续续租");
            foreach (ushort key in granted.Keys) { That(used.Add(key), "续租不复用Run墓碑"); repeated.Consume(51, granted.Lease, key); }
            if (round != 20) batch = repeated.Refill(51, granted with { Kind = FiendwarKeyLeaseKind.Refill, Status = 0,
                Handshake = Guid.NewGuid(), Keys = [] }, round * 10 + 3)!;
        }
        That(used.Count == 21 * 16, "不把初始16键或少量批次变成合法ACT轮数上限");
        ledger.RequireRecovery(renewal.Handshake, nextScene);
        That(ledger.RecoveryRequired && !ledger.CanUse(41, grant.Lease, grant.Keys[^1]) && ledger.Contains(grant.Keys[0]),
            "原生故障先暂停原Run，保留墓碑等待原子退场而不伪继续机制");
        RunReadyDeadlineBoundaries(run, scene);
        Console.WriteLine($"PASS {Count - before} 漩涡对象键握手断言");
    }

    static void RunReadyDeadlineBoundaries(Guid run, FiendwarKeyLeaseScene initialScene)
    {
        var scene = initialScene with { Serial = 3, Epoch = 18 };
        var live = new FiendwarObjectKeyLeases(run);
        var offer = live.Offer(51, scene, 1000);
        var candidates = offer with { Kind = FiendwarKeyLeaseKind.Candidates, Status = 0,
            Keys = Enumerable.Range(5000, 16).Select(value => (ushort)value).ToImmutableArray() };
        var grant = live.Grant(51, candidates, 10999, _ => true)!;
        var ready = grant with { Kind = FiendwarKeyLeaseKind.Ready, Keys = [] };
        That(live.Grant(51, candidates, 11000, _ => throw new InvalidOperationException()) is null,
            "候选及重复候选在精确截止仍拒绝，不能借迟到ready继续分配新键");
        That(!live.Confirm(51, ready with { Lease = ready.Lease + 1 }, 11000) &&
            !live.Confirm(51, ready with { Handshake = Guid.NewGuid() }, 11000) &&
            !live.Confirm(51, ready with { Scene = scene with { Epoch = scene.Epoch + 1 } }, 11000) &&
            !live.Confirm(51, ready with { Status = 1 }, 11000) &&
            !live.Confirm(51, ready with { Keys = [5000] }, 11000) &&
            !live.Confirm(51, ready with { Kind = FiendwarKeyLeaseKind.Candidates }, 11000),
            "过期ready仍严格校验成员租约、批次、完整场景、状态及空键正文");
        That(!live.CanUse(51, grant.Lease, grant.Keys[0]) && live.Confirm(51, ready, 11000) &&
            live.CanUse(51, grant.Lease, grant.Keys[0]) && live.Available(51) == 16,
            "截止前完整授键在边界收到精确ready仍可采用，避免客户端已ready而服务端拒绝的分歧");
        That(live.Confirm(51, ready, 11001) && live.Available(51) == 16,
            "截止之后的当前ready幂等，不重复加入可用键");

        var partial = live.Offer(52, scene, 12000);
        var partialGrant = live.Grant(52, partial with { Kind = FiendwarKeyLeaseKind.Candidates, Status = 0, Keys = [5100] }, 12001, _ => true)!;
        That(!live.Confirm(52, partialGrant with { Kind = FiendwarKeyLeaseKind.Ready, Keys = [] }, 22000) && live.Available(52) == 0,
            "迟到不能把不足16个的部分grant开放为可用键");
        var cancelled = live.Offer(53, scene, 22001);
        var cancelledGrant = live.Grant(53, cancelled with { Kind = FiendwarKeyLeaseKind.Candidates, Status = 0,
            Keys = Enumerable.Range(5200, 16).Select(value => (ushort)value).ToImmutableArray() }, 22002, _ => true)!;
        That(live.CancelBatch(53, cancelledGrant with { Kind = FiendwarKeyLeaseKind.Cancel, Keys = [] }) &&
            !live.Confirm(53, cancelledGrant with { Kind = FiendwarKeyLeaseKind.Ready, Keys = [] }, 32001) &&
            live.Available(53) == 0 && live.Contains(cancelledGrant.Keys[0]),
            "取消仍使完整已授批次失效，迟到ready不复活，原键墓碑保留");
        var stale = live.Offer(54, scene, 32002);
        var staleGrant = live.Grant(54, stale with { Kind = FiendwarKeyLeaseKind.Candidates, Status = 0,
            Keys = Enumerable.Range(5300, 16).Select(value => (ushort)value).ToImmutableArray() }, 32003, _ => true)!;
        _ = live.Offer(54, scene with { Map = scene.Map + 1, Epoch = scene.Epoch + 1 }, 32004);
        That(!live.Confirm(54, staleGrant with { Kind = FiendwarKeyLeaseKind.Ready, Keys = [] }, 42004) &&
            live.Available(54) == 0 && live.Contains(staleGrant.Keys[0]),
            "换场景的新batch拒绝旧grant的迟到ready，不跨房间重新开放键");

        foreach (ushort key in grant.Keys) live.Consume(51, grant.Lease, key);
        var refill = live.Refill(51, ready with { Kind = FiendwarKeyLeaseKind.Refill, Handshake = Guid.NewGuid() }, 43000)!;
        var refillGrant = live.Grant(51, refill with { Kind = FiendwarKeyLeaseKind.Candidates, Status = 0,
            Keys = Enumerable.Range(5400, 16).Select(value => (ushort)value).ToImmutableArray() }, 43001, _ => true)!;
        live.RequireRecovery(refill.Handshake, scene);
        That(!live.Confirm(51, refillGrant with { Kind = FiendwarKeyLeaseKind.Ready, Keys = [] }, 53000) &&
            live.Available(51) == 0 && !live.CanUse(51, refillGrant.Lease, refillGrant.Keys[0]),
            "Recovery锁后完整已授ready仍拒绝，不因放宽截止重新开放producer");

        var initial = new FiendwarObjectKeyLeases(run);
        var initialOffer = initial.Offer(61, initialScene with { Serial = 0 }, 1000);
        var initialGrant = initial.Grant(61, initialOffer with { Kind = FiendwarKeyLeaseKind.Candidates, Status = 0,
            Keys = Enumerable.Range(5500, 16).Select(value => (ushort)value).ToImmutableArray() }, 10999, _ => true)!;
        That(!initial.Confirm(61, initialGrant with { Kind = FiendwarKeyLeaseKind.Ready, Keys = [] }, 11000) &&
            initial.Available(61) == 0 && !initial.IsReady(61),
            "serial0初次入场完整授键也不能突破原10秒总截止");
    }

    static byte[] CandidateWire(FiendwarKeyLeaseMessage message)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x314C5746u); writer.Write((byte)2); writer.Write((byte)0); writer.Write((ushort)message.Keys.Length);
        writer.Write(message.Handshake.ToByteArray()); writer.Write(message.Scene.Run.ToByteArray()); writer.Write(message.Lease);
        writer.Write(message.Scene.Dungeon); writer.Write(message.Scene.Map); writer.Write(message.Scene.Seed);
        writer.Write(message.Scene.Position); writer.Write(message.Scene.Serial); writer.Write(message.Scene.Epoch); writer.Write(0u);
        foreach (ushort key in message.Keys) writer.Write(key);
        return stream.ToArray();
    }
}
