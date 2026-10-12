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
    TcpTestFixture? fixture;
    ushort sequence;
    bool requestedRoster;
    readonly byte channelNumber;
    public byte[] Roster { get; private set; } = [];
    public byte[] LoginEndpoint { get; private set; } = [];
    public byte[] SkillInfo { get; private set; } = [];
    public byte[] SceneSkillInfo { get; private set; } = [];
    public byte[] AvailableQuests { get; private set; } = [];
    public byte[] CompletedQuestHistory { get; private set; } = [];
    public ushort Id { get; private set; }
    public WirePacket[] SpecialContainers { get; private set; } = [];
    public byte[][] Mailbox { get; private set; } = [];
    public byte[] TitleProgress { get; private set; } = [];
    public WirePacket[] TitleBooks { get; private set; } = [];
    public byte[] ExpertContract { get; private set; } = [];
    public WirePacket[] ClientSettings { get; private set; } = [];
    public byte[] InitialGameOptions { get; private set; } = [];

    static readonly Encoding names;
    static TcpPeer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        names = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public TcpPeer(int port, string? localAddress = null, byte channelNumber = 10)
    {
        this.channelNumber = channelNumber;
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

    public static TcpPeer Open(TcpTestFixture fixture, string account = "10000", string? digest = null, int? gamePort = null, byte channelNumber = 10)
    {
        var peer = new TcpPeer(gamePort ?? fixture.Options.GamePort, channelNumber: channelNumber);
        peer.fixture = fixture;
        try
        {
            peer.Expect(0, 1);
            peer.Send(1562, NativeLoginWire.Precheck(account));
            peer.Expect(1, 1562, [1]);
            digest ??= account == "10000" ? "e10adc3949ba59abbe56e057f20f883e" : NativeLoginWire.Digest;
            peer.Send(1, NativeLoginWire.Login(account, digest));
            peer.LoginEndpoint = peer.Expect(1, 1).Body;
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
        if (opcode == 8 && body.AsSpan().SequenceEqual(new byte[] { 255, 255, 2 })) requestedRoster = true;
        if (opcode == 7 && body.Length == 0) requestedRoster = false;
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
        if (header[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1)) == 356)
            CompletedQuestHistory = frame[16..];
        if (header[0] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1)) == 6 && frame.Length > 16 && frame[16] == 1)
            requestedRoster = true;
        if (header[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1)) == 2 && frame.Length >= 21 && frame[16] is 0 or 1)
            TestCheck.Bytes(frame.AsSpan(19, 2), [2, channelNumber], "本人、同屏和刷新USERINFO均进入接收者当前频道的本地Actor表");
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
    public byte[] Create(string name, byte job = 0, byte variant = 0, bool withTutorial = false)
    {
        Send(5, CreateBody(name, job, variant));
        var ack = Expect(1, 5).Body;
        TestCheck.That(ack[0] == 1, "creation committed");
        Id = U16(ack, 1);
        Roster = Expect(0, 2).Body;
        // 既有业务用例从已完成训练的角色开始；训练专组保留真实建角状态，不在每组重玩整段剧情。
        if (!withTutorial && fixture is not null)
            fixture.Sql("UPDATE character_tutorial_state SET completed=1 WHERE character_id=$p0", Id);
        return Roster;
    }

    // 从提交的行独立编码预期，不借用服务端 Builder；初始化连招可含资源声明的默认组。
    internal static byte[] StoredCombos(TcpTestFixture fixture, ushort characterId) => Build(writer =>
    {
        var rows = fixture.Query("SELECT context,root_id,children FROM character_skill_combos WHERE character_id=$p0 ORDER BY context,root_id", characterId);
        writer.Write((byte)0);
        writer.Write((byte)2);
        for (byte context = 0; context < 2; context++)
        {
            var combos = rows.Where(row => Convert.ToByte(row[0]) == context).ToArray();
            writer.Write(context);
            writer.Write(checked((byte)combos.Length));
            foreach (var combo in combos)
            {
                writer.Write(Convert.ToUInt16(combo[1]));
                var children = (byte[])combo[2];
                writer.Write(checked((byte)(children.Length / 2)));
                writer.Write(children);
            }
        }
    });

    public WirePacket[] Select(ushort slot = 0, bool prepared = false, (byte Kind, ushort Opcode)[]? inventoryNotices = null,
        (ushort Opcode, byte[] Body)[]? settings = null, byte? appearanceFlags = null, bool expertContract = false,
        byte[]? sceneSkills = null, byte[]? combos = null, bool resumeTown = false, bool room = true)
    {
        if (!resumeTown && !requestedRoster)
            Send(8, [255, 255, 2]);
        Send(4, Build(w => w.Write(slot)));
        var expected = prepared ? PreparedEntry : Entry;
        if (resumeTown)
            expected = [.. expected.Where(p => p != ((byte)0, (ushort)24) && p != ((byte)0, (ushort)1379) && p != ((byte)0, (ushort)124)),
                .. (room ? new (byte Kind, ushort Opcode)[] { (0, 1379) } : []), (0, 124), (0, 24)];
        int afterCargo = prepared ? 9 : 8;
        // 保留普通入城断言的返回索引，特殊容器单独暴露；线上顺序仍逐包严格验证。
        var prefix = Batch(expected[..2]);
        var creatures = Expect(0, 105);
        byte[]? initialGameOptions = (settings ?? []).FirstOrDefault(option => option.Opcode == 173).Body;
        InitialGameOptions = initialGameOptions is null ? [] : Expect(0, 173, initialGameOptions).Body;
        var middle = new List<WirePacket>();
        SkillInfo = [];
        SceneSkillInfo = [];
        CompletedQuestHistory = [];
        for (int index = 2; index < afterCargo; index++)
        {
            var packet = Read();
            if (index == afterCargo - 2 && packet.Kind == 0 && packet.Opcode == 19)
            {
                SkillInfo = packet.Body;
                ExpertContract = expertContract ? Expect(0, 66).Body : [];
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
        ClientSettings = [];
        foreach (var notice in inventoryNotices ?? [])
            tail.Add(Expect(notice.Kind, notice.Opcode));
        foreach (var notice in expected.Skip(afterCargo))
        {
            // 普通入城在124前恢复；换频道等最终24绑定后恢复。
            if (!resumeTown && notice == ((byte)0, (ushort)124)) ReadSceneInitialization();
            if (notice == ((byte)0, (ushort)3))
            {
                var packet = Read();
                while (packet.Kind == 0 && packet.Opcode == 97)
                {
                    mails.Add(packet.Body);
                    packet = Read();
                }
                TestCheck.That(packet.Kind == 0 && packet.Opcode == 359, "角色称号三计数在邮件后、城镇状态前初始化");
                TitleProgress = packet.Body;
                TestCheck.That(TitleProgress.Length == 20 + U32(TitleProgress, 0) * 10, "成就列表必读16B最近完成尾部");
                TitleBooks = Enumerable.Range(0, 5).Select(category =>
                {
                    var book = Expect(0, 358);
                    TestCheck.That(book.Body.Length >= 11 && U16(book.Body, 1) == 0 && U32(book.Body, 3) == category,
                        "本人称号簿owner为0，五种分类均下发空或完整表");
                    return book;
                }).ToArray();
                packet = Read();
                TestCheck.That(mails.Count > 0 && packet.Kind == 0 && packet.Opcode == 3, "mailbox initializes after account assets and before town state");
                tail.Add(packet);
            }
            else tail.Add(Expect(notice.Kind, notice.Opcode));
        }
        if (resumeTown)
            ReadSceneInitialization();
        Mailbox = mails.ToArray();
        WirePacket[] packets = [.. prefix, .. middle, .. tail];
        Id = U16(packets[0].Body, 9);
        if (appearanceFlags.HasValue)
            TestCheck.That(packets[2].Body[^23] == appearanceFlags.Value, "Actor外观flags与设置后357一致");
        TestCheck.That(packets[prepared ? 7 : 6].Body[0] == 2 && packets[prepared ? 8 : 7].Body[0] == 12,
            "personal and account cargo initialize before locks");
        TestCheck.That(packets[4].Body[0] == 0, "main inventory initialization precedes cargo");
        ushort expansionSlots = U16(packets[4].Body, 1);
        TestCheck.That(expansionSlots % 8 == 0 && (expansionSlots >> 3) <= 3,
            "client expansion reader stays within its three-stage table");
        TestCheck.That(packets.Single(p => p is { Kind: 0, Opcode: 124 }).Body.Length == 0, "entry complete body empty");
        return packets.ToArray();

        void ReadSceneInitialization()
        {
            if (SkillInfo.Length > 0)
            {
                SceneSkillInfo = Expect(0, 19, sceneSkills).Body;
                var originalTrees = SkillPacketsTests.DecodeInitialized(SkillInfo);
                var sceneTrees = SkillPacketsTests.DecodeInitialized(SceneSkillInfo);
                TestCheck.That(originalTrees.Length == sceneTrees.Length && originalTrees.Zip(sceneTrees).All(pair =>
                    pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp &&
                    pair.First.Skills.Length == pair.Second.Skills.Length && pair.First.Skills.Zip(pair.Second.Skills).All(skills =>
                        skills.First.Slot == skills.Second.Slot && skills.First.Id == skills.Second.Id && skills.First.Level == skills.Second.Level &&
                        (skills.First.Command.Length == 0 || skills.First.Command == skills.Second.Command))),
                    "场景后仅可补默认指令，完整两树余额、绑定、等级与已保存指令保持");
            }
            if (combos is not null) Expect(0, 447, combos);
            ClientSettings = (settings ?? []).Select(option => Expect(0, option.Opcode, option.Body)).ToArray();
            if (appearanceFlags.HasValue)
                Expect(0, 357, Build(w =>
                {
                    w.Write(U16(prefix[0].Body, 9));
                    w.Write(appearanceFlags.Value);
                }));
            AvailableQuests = Expect(0, 21).Body;
            if (prepared)
            {
                CompletedQuestHistory = Expect(0, 356).Body;
                TestCheck.Bytes(CompletedQuestHistory, middle.Single(packet => packet.Opcode == 356).Body,
                    "入城21后恢复同一角色完成历史，保留前面的场景完成位");
            }
        }
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
