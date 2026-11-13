using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DfoServer.Game.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class AntonTcpProbe
{
    sealed class FiendwarKeys
    {
        internal byte[] Header = [];
        internal readonly Queue<ushort> Available = new();
        internal readonly HashSet<ushort> Granted = [];
        internal readonly HashSet<ushort> Candidates = [];
        internal List<WirePacket>? Entry;
        internal int Next = 1;
    }

    static readonly ConditionalWeakTable<TcpPeer, FiendwarKeys> keyLeases = new();

    static bool IsFiendwarDungeon(int dungeon) => dungeon is >= 291100339 and <= 291100364 or >= 5350 and <= 5358;

    static void RememberFiendwarEntry(TcpPeer peer, List<WirePacket> rows)
    {
        if (rows.Any(packet => packet is { Kind: 0, Opcode: 27 }) &&
            rows.Any(packet => packet is { Kind: 0, Opcode: 28, Body.Length: >= 4 } && IsFiendwarDungeon(checked((int)TcpPeer.U32(packet.Body)))) &&
            rows.Any(packet => packet is { Kind: 0, Opcode: 29 }))
            keyLeases.GetOrCreateValue(peer).Entry = [.. rows];
    }

    internal static List<WirePacket> TakeFiendwarEntry(TcpPeer peer, int dungeon)
    {
        if (!keyLeases.TryGetValue(peer, out var state) || state.Entry is not { } rows ||
            !rows.Any(packet => packet is { Kind: 0, Opcode: 28, Body.Length: >= 4 } && TcpPeer.U32(packet.Body) == (uint)dungeon))
            throw new InvalidOperationException(dungeon + " 尚无真实36触发的完整入场场景");
        state.Entry = null;
        return rows;
    }

    // 假客户端也走真实grant/ready，不以高键绕过整路线、Ground及所有成员的占用判断。
    internal static bool ProcessFiendwarKeyNotice(TcpPeer peer, WirePacket packet)
    {
        if (packet is not { Kind: 0, Opcode: FiendwarKeyLeaseRequests.Opcode }) return false;
        var body = packet.Body;
        That(body.Length >= 80 && BinaryPrimitives.ReadUInt32LittleEndian(body) == FiendwarKeyLeaseRequests.Magic &&
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(76)) == 0,
            "真实对象键通知保持FWL1完整头与保留零字段");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6));
        That(count <= 64 && body.Length == 80 + count * 2 && BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(40)) != 0,
            "真实对象键通知容量及非零租约完整");
        var state = keyLeases.GetOrCreateValue(peer);
        if (body[4] == (byte)FiendwarKeyLeaseKind.Cancel)
        {
            // 失败用例仍可检查原通知；不替生产端吞掉取消或伪造新的ready。
            return false;
        }
        if (body[4] == (byte)FiendwarKeyLeaseKind.Offer)
        {
            That(count == 0 && body[5] == 16, "服务端原offer声明16个未用键");
            bool sameRun = state.Header.Length == 80 && state.Header.AsSpan(24, 24).SequenceEqual(body.AsSpan(24, 24));
            if (!sameRun)
            {
                state.Available.Clear(); state.Granted.Clear(); state.Candidates.Clear(); state.Next = 1;
            }
            else if (BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(68)) < BinaryPrimitives.ReadInt64LittleEndian(state.Header.AsSpan(68)))
                throw new InvalidOperationException("服务端不得以旧epoch对象键通知覆盖新房间");
            bool duplicate = state.Header.AsSpan().SequenceEqual(body.AsSpan(0, 80));
            state.Header = body.AsSpan(0, 80).ToArray();
            if (state.Available.Count >= 16) SendKeyMessage(peer, state, FiendwarKeyLeaseKind.Ready);
            else if (duplicate && state.Candidates.Count != 0)
                SendKeyMessage(peer, state, FiendwarKeyLeaseKind.Candidates, state.Candidates.Order().ToArray());
            else SendCandidates(peer, state);
            return true;
        }
        That(body[4] == (byte)FiendwarKeyLeaseKind.Grant && body[5] == 0 && state.Header.Length == 80 &&
            state.Header.AsSpan(8).SequenceEqual(body.AsSpan(8, 72)), "grant绑定本次完整握手、Run、租约与房间身份");
        for (int index = 0; index < count; index++)
        {
            ushort key = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(80 + index * 2));
            That(key is > 0 and < ushort.MaxValue && (state.Candidates.Contains(key) || state.Granted.Contains(key)),
                "grant只取本客户端实际提出的有限候选");
            if (state.Granted.Add(key)) state.Available.Enqueue(key);
        }
        state.Candidates.Clear();
        if (state.Available.Count >= 16) SendKeyMessage(peer, state, FiendwarKeyLeaseKind.Ready);
        else SendCandidates(peer, state);
        return true;
    }

    static void SendCandidates(TcpPeer peer, FiendwarKeys state)
    {
        state.Candidates.Clear();
        while (state.Next < ushort.MaxValue && state.Candidates.Count < 64)
            state.Candidates.Add(checked((ushort)state.Next++));
        if (state.Candidates.Count == 0) throw new InvalidOperationException("真实有限对象键空间耗尽");
        SendKeyMessage(peer, state, FiendwarKeyLeaseKind.Candidates, state.Candidates.Order().ToArray());
    }

    static void SendKeyMessage(TcpPeer peer, FiendwarKeys state, FiendwarKeyLeaseKind kind, ushort[]? keys = null)
    {
        keys ??= [];
        byte[] body = new byte[80 + keys.Length * 2];
        state.Header.CopyTo(body, 0); body[4] = (byte)kind; body[5] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), checked((ushort)keys.Length));
        for (int index = 0; index < keys.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(80 + index * 2), keys[index]);
        peer.Send(FiendwarKeyLeasePackets.Opcode, body);
    }

    internal static ushort TakeFiendwarKey(TcpPeer peer)
    {
        _ = PeekFiendwarKey(peer);
        return keyLeases.GetOrCreateValue(peer).Available.Dequeue();
    }

    // 仅窥视队列；模拟原生已采用的FWS2必须由Take取走，不能因预期登记被拒而保留本地信用。
    internal static ushort PeekFiendwarKey(TcpPeer peer)
    {
        var state = keyLeases.GetOrCreateValue(peer);
        if (state.Header.Length != 80) throw new InvalidOperationException("尚无真实对象键租约");
        if (state.Available.Count == 0)
        {
            byte[] original = state.Header;
            state.Header = (byte[])original.Clone(); Guid.NewGuid().TryWriteBytes(state.Header.AsSpan(8, 16));
            SendKeyMessage(peer, state, FiendwarKeyLeaseKind.Refill);
            state.Header = original;
            _ = Barrier(peer, "fiendwar real key refill");
        }
        That(state.Available.Count > 0, "后续召唤也必须先收到服务端真实grant");
        return state.Available.Peek();
    }

    internal static ulong FiendwarLease(TcpPeer peer) => keyLeases.TryGetValue(peer, out var state) && state.Header.Length == 80
        ? BinaryPrimitives.ReadUInt64LittleEndian(state.Header.AsSpan(40)) : throw new InvalidOperationException("未安装对象键租约");

    internal static void CompleteFiendwarEntry(TcpPeer[] peers, List<WirePacket>[] rows,
        Func<TcpPeer, List<WirePacket>> read)
    {
        for (int round = 0; round < 8 && rows.Any(frames => !frames.Any(packet => packet is { Kind: 0, Opcode: 29 })); round++)
            for (int index = 0; index < peers.Length; index++) rows[index].AddRange(read(peers[index]));
        That(rows.All(frames => frames.Count(packet => packet is { Kind: 0, Opcode: 29 }) == 1),
            "全员真实grant/ready完成后恰好发布一次完整首次进图");
    }
}
