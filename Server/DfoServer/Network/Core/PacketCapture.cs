using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using DfoServer.Infrastructure;

namespace DfoServer.Network;

internal sealed record PacketTraceSnapshot(DateTimeOffset Timestamp, int ConnectionId, string Protocol, string Direction,
    byte Kind, ushort Opcode, string Name, int TotalLength, int BodyLength, string BodyHex, bool BodyTruncated, bool Redacted);

internal sealed record PacketSessionSnapshot(int ConnectionId, string Protocol, string RemoteEndpoint,
    DateTimeOffset ConnectedAt, long ReceivedPackets, long SentPackets, ushort? LastOpcode);

// 只记录完整应用层帧；关闭时不创建实例或文件，写入失败不终止游戏连接。
internal sealed class PacketCapture : IDisposable
{
    sealed class PacketSession(int connection, string protocol, IPEndPoint peer)
    {
        readonly DateTimeOffset connectedAt = DateTimeOffset.Now;
        long received;
        long sent;
        int lastOpcode = -1;

        public void Count(bool send, ushort opcode)
        {
            if (send)
                Interlocked.Increment(ref sent);
            else
            {
                Interlocked.Increment(ref received);
                Volatile.Write(ref lastOpcode, opcode);
            }
        }

        public PacketSessionSnapshot Snapshot()
        {
            int opcode = Volatile.Read(ref lastOpcode);
            return new(connection, protocol, peer.ToString(), connectedAt,
                Interlocked.Read(ref received), Interlocked.Read(ref sent), opcode < 0 ? null : (ushort)opcode);
        }
    }

    const int MaximumRecentPackets = 400;
    const int MaximumBodyBytes = 4096;
    readonly BufferedLogFile? file;
    readonly Queue<PacketTraceSnapshot>? recent;
    readonly ConcurrentDictionary<int, PacketSession>? sessions;
    readonly object recentGate = new();
    long totalConnections;
    long totalReceived;
    long totalSent;
    int failed;
    int disposed;
    public bool IsEnabled => file is not null && Volatile.Read(ref failed) == 0;
    public long TotalConnections => Interlocked.Read(ref totalConnections);
    public long TotalReceived => Interlocked.Read(ref totalReceived);
    public long TotalSent => Interlocked.Read(ref totalSent);

    public PacketCapture(string? directory, bool retainRecent = false)
    {
        if (retainRecent)
        {
            recent = new();
            sessions = new();
        }
        if (directory is not null)
        {
            file = new(Path.Combine(directory, "packet_log.txt"), Disable);
            Append($"=== Packet capture {DateTimeOffset.Now:O}; GAME SEND=16B, RECV=13B; CHANNEL=11B ===\n");
        }
    }

    public void Register(int connection, bool channel, IPEndPoint peer, bool native = false)
    {
        if (Volatile.Read(ref disposed) == 0 && sessions?.TryAdd(connection, new(connection, native ? "NATIVE" : channel ? "CHANNEL" : "GAME", peer)) == true)
            Interlocked.Increment(ref totalConnections);
    }

    public void Remove(int connection) => sessions?.TryRemove(connection, out _);

    public PacketSessionSnapshot[] Sessions() => sessions?.Values.Select(session => session.Snapshot())
        .OrderBy(session => session.ConnectionId).ToArray() ?? [];

    public void Write(int connection, bool channel, bool send, ReadOnlySpan<byte> frame)
    {
        if (Volatile.Read(ref disposed) != 0 || frame.Length < (channel ? 2 : 3) || recent is null && Volatile.Read(ref failed) != 0)
            return;
        var timestamp = DateTimeOffset.Now;
        int header = channel ? 11 : send ? 16 : 13;
        ushort opcode = channel ? frame[1] : BinaryPrimitives.ReadUInt16LittleEndian(frame[1..]);
        string name = channel ? "CHANNEL" : frame[0] == 1 ? ((CmdPacketType)opcode).ToString() : ((NotiPacketType)opcode).ToString();
        if (send)
            Interlocked.Increment(ref totalSent);
        else
            Interlocked.Increment(ref totalReceived);
        if (sessions?.TryGetValue(connection, out var session) == true)
            session.Count(send, opcode);
        // 连同畸形认证包一起脱敏；旧扩展包也可能含凭据，不输出 body 或 raw。
        bool redact = !channel && (!send || frame[0] == 1) && IsAuthentication(opcode);
        ReadOnlySpan<byte> body = frame[Math.Min(header, frame.Length)..];
        if (recent is not null)
        {
            int dumpLength = Math.Min(body.Length, MaximumBodyBytes);
            var snapshot = new PacketTraceSnapshot(timestamp, connection, channel ? "CHANNEL" : "GAME", send ? "SEND" : "RECV",
                frame[0], opcode, name, frame.Length, body.Length, redact ? "" : Convert.ToHexString(body[..dumpLength]),
                !redact && body.Length > dumpLength, redact);
            lock (recentGate)
            {
                if (Volatile.Read(ref disposed) != 0)
                    return;
                if (recent.Count == MaximumRecentPackets)
                    recent.Dequeue();
                recent.Enqueue(snapshot);
            }
        }
        if (file is null || Volatile.Read(ref failed) != 0)
            return;
        var text = new StringBuilder();
        text.Append($"{timestamp:O} connection={connection} protocol={(channel ? "CHANNEL" : "GAME")} {(send ? "SEND" : "RECV")} kind=0x{frame[0]:X2} opcode=0x{opcode:X4} name={name} len={frame.Length} body=({body.Length}B)");
        if (redact)
            text.Append(" [authentication payload redacted]\n");
        else
        {
            text.Append(" [");
            Hex(text, body);
            text.Append("]\n  raw: ");
            Hex(text, frame);
            text.Append('\n');
        }
        Append(text.ToString());
    }

    public PacketTraceSnapshot[] Recent(int? connectionId = null)
    {
        PacketTraceSnapshot[] snapshot;
        lock (recentGate)
            snapshot = recent?.ToArray() ?? [];
        return connectionId is { } id ? snapshot.Where(packet => packet.ConnectionId == id).ToArray() : snapshot;
    }

    // Native 每帧携带连接关联材料。尚未解释的内部载荷也只记录长度，不输出 raw 或关联值。
    public void WriteNative(int connection, int wireLength, int bodyLength, bool sending = false)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var timestamp = DateTimeOffset.Now;
        if (sending) Interlocked.Increment(ref totalSent);
        else Interlocked.Increment(ref totalReceived);
        if (sessions?.TryGetValue(connection, out var session) == true) session.Count(sending, 0);
        if (recent is not null)
        {
            var snapshot = new PacketTraceSnapshot(timestamp, connection, "NATIVE", sending ? "SEND" : "RECV", 1, 0,
                "NATIVE", wireLength, bodyLength, "", false, true);
            lock (recentGate)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                if (recent.Count == MaximumRecentPackets) recent.Dequeue();
                recent.Enqueue(snapshot);
            }
        }
        Append($"{timestamp:O} connection={connection} protocol=NATIVE {(sending ? "SEND" : "RECV")} len={wireLength} body=({bodyLength}B) [native association and payload redacted]\n");
    }

    internal static bool IsAuthentication(ushort opcode) => opcode is
        (ushort)CmdPacketType.LOGIN or (ushort)CmdPacketType.LOGIN_PRECHECK or (ushort)CmdPacketType.CLIENT_SPEC_STATISTIC;

    static void Hex(StringBuilder text, ReadOnlySpan<byte> bytes)
    {
        const string digits = "0123456789ABCDEF";
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i != 0)
                text.Append(' ');
            text.Append(digits[bytes[i] >> 4]);
            text.Append(digits[bytes[i] & 15]);
        }
    }

    void Append(string text)
    {
        if (file is null || Volatile.Read(ref failed) != 0) return;
        try { file.Append(text); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Disable(error); }
    }
    void Disable(Exception error)
    {
        if (Interlocked.Exchange(ref failed, 1) == 0)
            Console.Error.WriteLine("Packet capture disabled: " + error.Message);
    }
    public void Flush()
    {
        try { file?.Flush(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Disable(error); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        try { file?.Dispose(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Disable(error); }
        finally
        {
            Interlocked.Exchange(ref failed, 1);
            lock (recentGate) recent?.Clear();
            sessions?.Clear();
        }
    }
}
