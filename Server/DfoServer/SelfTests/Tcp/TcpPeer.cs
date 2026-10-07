using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text;

namespace DfoServer.Testing;

internal sealed record WirePacket(byte Kind, ushort Opcode, byte[] Body);
// 独立实现测试帧与 CRC，不能调用服务端的 Frame、Parser、Builder 来自证。
internal sealed class TcpPeer : IDisposable
{
    public static readonly (byte Kind, ushort Opcode)[] Entry =
        [(1, 4), (0, 1382), (0, 2), (0, 2), (0, 13), (0, 988), (0, 13), (0, 13), (0, 950), (0, 53), (0, 1152), (0, 3), (0, 22), (0, 23), (0, 24), (0, 1379), (0, 124)];
    public static readonly (byte Kind, ushort Opcode)[] PreparedEntry =
        [(1, 4), (0, 1382), (0, 2), (0, 2), (0, 13), (0, 988), (0, 356), (0, 13), (0, 13), (0, 950), (0, 53), (0, 1152), (0, 3), (0, 22), (0, 23), (0, 24), (0, 1379), (0, 124)];
    readonly TcpClient client = new()
    {
        NoDelay = true
    };
    readonly NetworkStream stream;
    ushort sequence;
    public byte[] Roster { get; private set; } = [];
    public byte[] SkillInfo { get; private set; } = [];
    public ushort Id { get; private set; }
    public WirePacket[] SpecialContainers { get; private set; } = [];
    public byte[][] Mailbox { get; private set; } = [];

    static readonly Encoding names;
    static TcpPeer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        names = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public TcpPeer(int port, string? localAddress = null)
    {
        try
        {
            if (localAddress is not null)
                client.Client.Bind(new IPEndPoint(IPAddress.Parse(localAddress), 0));
            client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            stream = client.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static TcpPeer Open(TcpTestFixture fixture, string account = "10000", string? digest = null)
    {
        var peer = new TcpPeer(fixture.Options.GamePort);
        try
        {
            peer.Expect(0, 1);
            peer.Send(1562, NativeLoginWire.Precheck(account));
            peer.Expect(1, 1562, [1]);
            digest ??= account == "10000" ? "e10adc3949ba59abbe56e057f20f883e" : NativeLoginWire.Digest;
            peer.Send(1, NativeLoginWire.Login(account, digest));
            peer.Expect(1, 1);
            peer.Roster = peer.Expect(0, 2).Body;
            return peer;
        }
        catch
        {
            peer.Dispose();
            throw;
        }
    }

    static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0x4DB89129u);
        }

        return crc ^ uint.MaxValue;
    }

    public void Send(ushort opcode, byte[]? body = null)
    {
        body ??= [];
        byte[] frame = new byte[13 + body.Length];
        frame[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(3), frame.Length);
        body.CopyTo(frame, 13);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), Crc(frame.AsSpan(11)));
        stream.Write(frame);
    }

    public WirePacket Read()
    {
        byte[] header = new byte[16];
        stream.ReadExactly(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(3));
        TestCheck.That(length is >= 16 and <= 65536, "S2C frame length");
        TestCheck.That(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(11)) == sequence++, "per-connection sequence");
        TestCheck.Bytes(header.AsSpan(13), [0, 0, 0], "S2C reserved header");
        byte[] frame = new byte[length];
        header.CopyTo(frame, 0);
        stream.ReadExactly(frame.AsSpan(16));
        TestCheck.That(Crc(frame.AsSpan(11)) == BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(7)), "S2C CRC");
        return new(header[0], BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1)), frame[16..]);
    }

    public WirePacket Expect(byte kind, ushort opcode, byte[]? body = null)
    {
        var packet = Read();
        TestCheck.That(packet.Kind == kind && packet.Opcode == opcode, $"packet order: expected ({kind},{opcode}), got ({packet.Kind},{packet.Opcode})");
        if (body is not null)
            TestCheck.Bytes(packet.Body, body, $"body ({kind},{opcode})");
        return packet;
    }

    public WirePacket[] Batch(params (byte Kind, ushort Opcode)[] expected) => expected.Select(p => Expect(p.Kind, p.Opcode)).ToArray();
    public void SendBarrier() => Send(1347, [2, 0, 0, 0]);
    public void Barrier()
    {
        SendBarrier();
        Expect(1, 1347, [1]);
    }

    public void Closed()
    {
        try
        {
            TestCheck.That(stream.ReadByte() == -1, "connection closes without extra packet");
        }
        catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
        {
            TestCheck.That(true, "connection reset after rejection");
        }
    }

    public static byte[] Build(Action<BinaryWriter> write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        write(writer);
        return bytes.ToArray();
    }

    public static byte[] Name(string name) => Build(w =>
    {
        byte[] encoded = names.GetBytes(name);
        w.Write(encoded.Length);
        w.Write(encoded);
    });
    public static int NameWidth(string name) => names.GetByteCount(name);
    public static string DecodeName(byte[] body, int at, int width) => names.GetString(body, at, width);
    public static byte[] CreateBody(string name, byte job = 0, byte variant = 0) => [job, .. Name(name), 0, 0, 0, 0, 0, 0, 255, 0, variant, 0, 0, 0];
    public byte[] Create(string name, byte job = 0, byte variant = 0)
    {
        Send(5, CreateBody(name, job, variant));
        var ack = Expect(1, 5).Body;
        TestCheck.That(ack[0] == 1, "creation committed");
        Id = U16(ack, 1);
        Roster = Expect(0, 2).Body;
        return Roster;
    }

    public WirePacket[] Select(ushort slot = 0, bool prepared = false, (byte Kind, ushort Opcode)[]? inventoryNotices = null)
    {
        Send(4, Build(w => w.Write(slot)));
        var expected = prepared ? PreparedEntry : Entry;
        int afterCargo = prepared ? 9 : 8;
        // 保留普通入城断言的返回索引，特殊容器单独暴露；线上顺序仍逐包严格验证。
        var prefix = Batch(expected[..2]);
        var creatures = Expect(0, 105);
        var middle = new List<WirePacket>();
        SkillInfo = [];
        for (int index = 2; index < afterCargo; index++)
        {
            var packet = Read();
            if (index == afterCargo - 2 && packet.Kind == 0 && packet.Opcode == 19)
            {
                SkillInfo = packet.Body;
                packet = Read();
            }
            TestCheck.That(packet.Kind == expected[index].Kind && packet.Opcode == expected[index].Opcode,
                $"entry packet order at {index}");
            middle.Add(packet);
        }
        var avatars = Expect(0, 13);
        var pets = Expect(0, 13);
        var medals = Expect(0, 13);
        TestCheck.That(avatars.Body[0] == 1 && U16(avatars.Body, 1) == 105 && pets.Body[0] == 7, "special container initialization and avatar capacity");
        TestCheck.That(medals.Body[0] == 38, "medal container initialization");
        SpecialContainers = [creatures, avatars, pets, medals];
        var tail = new List<WirePacket>();
        var mails = new List<byte[]>();
        foreach (var notice in inventoryNotices ?? [])
            tail.Add(Expect(notice.Kind, notice.Opcode));
        foreach (var notice in expected.Skip(afterCargo))
        {
            if (notice == ((byte)0, (ushort)3))
            {
                var packet = Read();
                while (packet.Kind == 0 && packet.Opcode == 97)
                {
                    mails.Add(packet.Body);
                    packet = Read();
                }
                TestCheck.That(mails.Count > 0 && packet.Kind == 0 && packet.Opcode == 3, "mailbox initializes after account assets and before town state");
                tail.Add(packet);
            }
            else tail.Add(Expect(notice.Kind, notice.Opcode));
        }
        Mailbox = mails.ToArray();
        WirePacket[] packets = [.. prefix, .. middle, .. tail];
        Id = U16(packets[0].Body, 9);
        TestCheck.That(packets[prepared ? 7 : 6].Body[0] == 2 && packets[prepared ? 8 : 7].Body[0] == 12,
            "personal and account cargo initialize before locks");
        TestCheck.That(packets[4].Body[0] == 0, "main inventory initialization precedes cargo");
        ushort expansionSlots = U16(packets[4].Body, 1);
        TestCheck.That(expansionSlots % 8 == 0 && (expansionSlots >> 3) <= 3,
            "client expansion reader stays within its three-stage table");
        TestCheck.That(packets[^1].Body.Length == 0, "entry complete body empty");
        return packets.ToArray();
    }

    public static byte[] Move(ushort x, ushort y, bool motion = false) => Build(w =>
    {
        w.Write(x);
        w.Write(y);
        w.Write((byte)5);
        w.Write((ushort)100);
        if (motion)
        {
            w.Write((byte)0);
            w.Write((ushort)0);
            w.Write(new byte[3]);
        }
    });
    public static byte[] Area(byte town, byte area, ushort x, ushort y, ushort previousTown, ushort previousArea, byte kind = 0, uint npc = 0) => Build(w =>
    {
        w.Write(town);
        w.Write(area);
        w.Write(x);
        w.Write(y);
        w.Write((byte)5);
        w.Write(previousTown);
        w.Write(previousArea);
        w.Write(npc);
        w.Write(kind);
    });
    public void Travel(byte town, byte area, ushort x, ushort y, ushort previousTown, ushort previousArea, bool bottom = false, uint? returnTown = null, byte kind = 0, uint npc = 0)
    {
        Send(bottom ? (ushort)1426 : (ushort)36, bottom ? [] : Area(town, area, x, y, previousTown, previousArea, kind, npc));
        SendBarrier();
        var location = Expect(0, 23).Body;
        TestCheck.Bytes(location.AsSpan(0, 8), Build(w =>
        {
            w.Write(Id);
            w.Write(town);
            w.Write(area);
            w.Write(x);
            w.Write(y);
        }), "area preserves raw coordinates");
        TestCheck.Bytes(Expect(0, 24).Body.AsSpan(6, 4), Build(w =>
        {
            w.Write(x);
            w.Write(y);
        }), "transition coordinates");
        if (returnTown.HasValue)
            Expect(0, 1379, Build(w => w.Write(returnTown.Value)));
        Expect(1, 1347, [1]);
    }

    public static ushort U16(byte[] body, int at = 0) => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
    public static uint U32(byte[] body, int at = 0) => BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at));
    public void Dispose() => client.Dispose();
}
