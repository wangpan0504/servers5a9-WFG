using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.IO.Compression;

namespace DfoServer.Testing;

internal sealed record WirePacket(byte Kind, ushort Opcode, byte[] Body, bool Protected = false);
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
    byte? staminaPercent;
    long lastBarrierCompleted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    long UnixTime => (fixture?.UtcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
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
    public byte[] AssaultDailyInfo { get; private set; } = [];
    public WirePacket[] CollectBoxes { get; private set; } = [];
    public WirePacket[] TitleBooks { get; private set; } = [];
    public byte[] ExpertContract { get; private set; } = [];
    public byte[] PremiumInfo { get; private set; } = [];
    public byte[] SeriaLuckInfo { get; private set; } = [];
    public byte[] CubeContractInfo { get; private set; } = [];
    public WirePacket[] ClientSettings { get; private set; } = [];
    public byte[] InitialGameOptions { get; private set; } = [];
    public byte[] AdventureName { get; private set; } = [];
    public string ActorAdventureName { get; private set; } = "";
    public byte[] AdventureInfo { get; private set; } = [];
    public byte[] AdventureRefresh { get; private set; } = [];
    public byte[] CardMembershipInformation { get; private set; } = [];
    public byte[] DevilService0 { get; private set; } = [];
    public Dictionary<byte, long> PremiumServices { get; private set; } = [];

    static readonly Encoding names;
    static TcpPeer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        names = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public TcpPeer(int port, string? localAddress = null, byte channelNumber = 10, string serverAddress = "127.0.0.1")
    {
        this.channelNumber = channelNumber;
        try
        {
            if (localAddress is not null)
                client.Client.Bind(new IPEndPoint(IPAddress.Parse(localAddress), 0));
            client.ConnectAsync(serverAddress, port).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
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

    public static TcpPeer Open(TcpTestFixture fixture, string account = "10000", string? digest = null, int? gamePort = null, byte channelNumber = 10,
        string serverAddress = "127.0.0.1")
    {
        var peer = new TcpPeer(gamePort ?? fixture.Options.GamePort, channelNumber: channelNumber, serverAddress: serverAddress);
        peer.fixture = fixture;
        peer.lastBarrierCompleted = peer.UnixTime;
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
        bool protectedFrame = header[15] == 1;
        if (protectedFrame)
        {
            TestCheck.That(header[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1)) == 108,
                "当前保护帧只接已确认的EVENT_INFO通知，不放宽普通包头");
            TestCheck.That(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(7)) == 1,
                "保护帧使用固定校验字段1");
            TestCheck.Bytes(header.AsSpan(11), [0, 0, 0, 0, 1], "保护帧保留零序号和独立标志，不消耗普通序号");
        }
        else
        {
            TestCheck.That(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(11)) == sequence++, "per-connection sequence");
            TestCheck.Bytes(header.AsSpan(13), [0, 0, 0], "S2C reserved header");
        }
        byte[] frame = new byte[length];
        header.CopyTo(frame, 0);
        stream.ReadExactly(frame.AsSpan(16));
        if (!protectedFrame)
            TestCheck.That(Crc(frame.AsSpan(11)) == BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(7)), "S2C CRC");
        if (header[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1)) == 356)
            CompletedQuestHistory = frame[16..];
        if (header[0] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1)) == 6 && frame.Length > 16 && frame[16] == 1)
            requestedRoster = true;
        if (header[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1)) == 2 && frame.Length >= 21 && frame[16] is 0 or 1)
            TestCheck.Bytes(frame.AsSpan(19, 2), [2, channelNumber], "本人、同屏和刷新USERINFO均进入接收者当前频道的本地Actor表");
        var packet = new WirePacket(header[0], BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1)), frame[16..], protectedFrame);
        if (packet is { Kind: 0, Opcode: 4, Body.Length: 1 }) staminaPercent = packet.Body[0];
        return packet;
    }

    public WirePacket Expect(byte kind, ushort opcode, byte[]? body = null)
    {
        var packet = Read();
        // #233 打开选图时 NOTI1536(5B) 紧挨 NOTI27 之前下发；未显式期待它的旧用例在等待27时记录并跳过。
        if (kind == 0 && opcode == 27 && packet.Kind == 0 && packet.Opcode == 1536 && packet.Body.Length == 5)
        {
            AssaultDailyInfo = packet.Body;
            packet = Read();
        }
        TestCheck.That(packet.Kind == kind && packet.Opcode == opcode, $"packet order: expected ({kind},{opcode}), got ({packet.Kind},{packet.Opcode})");
        if (body is not null)
            TestCheck.Bytes(packet.Body, body, $"body ({kind},{opcode})");
        return packet;
    }

    public WirePacket[] Batch(params (byte Kind, ushort Opcode)[] expected) => expected.Select(p => Expect(p.Kind, p.Opcode)).ToArray();
    public void SendBarrier() => Send(1347, [2, 0, 0, 0]);
    public void ReadCardMembershipState(WirePacket? information = null, bool initialization = false)
    {
        information ??= Expect(0, 1760);
        TestCheck.That(information.Kind == 0 && information.Opcode == 1760, "会员状态必须从1760开始，不能跳过其他通知");
        CardMembershipInformation = information.Body;
        PremiumInfo = information.Body;
        var next = Read();
        while (next is { Kind: 0, Opcode: 66 })
        {
            TestCheck.That(!initialization, "登录不能在恢复选择之前发送契约横幅更新");
            TestCheck.That(next.Body.Length == 11 && U16(next.Body, 0) == 2 && next.Body[2] is 27 or 84 or 97 &&
                BinaryPrimitives.ReadInt64LittleEndian(next.Body.AsSpan(3)) >= 0,
                "契约更新用本版66完整期限触发原生提示，不把1760当作效果刷新");
            next = Read();
        }
        if (next is { Kind: 0, Opcode: 901 })
        {
            TestCheck.That(next.Body.Length == 2 && next.Body[0] <= 1 && (next.Body[1] < 6 || next.Body[1] == 255),
                "晶体选择回放严格使用901原始两字段");
            CubeContractInfo = next.Body;
            next = Read();
        }
        if (next is { Kind: 0, Opcode: 2 })
        {
            TestCheck.That(next.Body.Length > 24 && next.Body[0] == 1, "契约权益与选择后只刷新本人Player，不重建Actor或场景");
            next = Read();
        }
        var updates = new List<byte>();
        while (next is { Kind: 0, Opcode: 66 })
        {
            TestCheck.That(next.Body.Length == 11 && U16(next.Body, 0) == 2 && next.Body[2] is 84 or 97 &&
                BinaryPrimitives.ReadInt64LittleEndian(next.Body.AsSpan(3)) >= 0,
                "契约横幅与真实期限更新均在选择集和Player之后发送");
            updates.Add(next.Body[2]);
            next = Read();
        }
        TestCheck.That(next.Kind == 1 && next.Opcode == 905,"契约初始化后保持魔王905包序");
        DevilService0 = next.Body;
        TestCheck.That(CardMembershipInformation.Length == 1 + CardMembershipInformation[0] * 9, "1760完整契约目录长度与数量一致");
        PremiumServices = [];
        for (int index = 0; index < CardMembershipInformation[0]; index++)
        {
            byte type = CardMembershipInformation[1 + index * 9];
            long remaining = BinaryPrimitives.ReadInt64LittleEndian(CardMembershipInformation.AsSpan(2 + index * 9));
            TestCheck.That(type is > 0 and < 132 && remaining >= 0 && PremiumServices.TryAdd(type, remaining),
                "统一契约目录类型合法、无重复且期限非负");
        }
        if (initialization)
            TestCheck.That(updates.SequenceEqual(PremiumServices.Where(pair => pair.Key is 84 or 97 && pair.Value > 0)
                .Select(pair => pair.Key).Order()), "每次选角恢复成长/晶体横幅，既不提前触发空选择提醒，也不漏发原生显示刷新");
        TestCheck.That(PremiumServices.ContainsKey(56) && PremiumServices.ContainsKey(58),
            "统一契约列表包含56/58的有效期限或显式清除值，不覆盖22/27");
        TestCheck.That(DevilService0.Length == 77 && DevilService0[0] == 1 && U16(DevilService0, 1) == 1 &&
            DevilService0.AsSpan(3, 2).IndexOfAnyExcept((byte)0) == -1 && DevilService0[13] <= 10 &&
            Enumerable.Range(0, 8).All(slot => DevilService0.AsSpan(5 + slot * 9, 4).IndexOfAnyExcept((byte)0) == -1) &&
            Enumerable.Range(0, 8).Any(slot => U32(DevilService0, 9 + slot * 9) > 0) == (PremiumServices[58] > 0),
            "魔王八项期限独立投影，58概览与至少一项有效服务一致，黄金牌次数不超过10");
    }
    public void Barrier()
    {
        SendBarrier();
        var packet = Read();
        int staminaUpdates = 0;
        // 撤退及自然恢复会在命令尾部发布虚弱状态；只接收与本人持久期限吻合的通知。
        while (packet is { Kind: 0, Opcode: 4 })
        {
            TestCheck.That(++staminaUpdates <= 2, "屏障前虚弱通知数量有界");
            ValidateStamina(packet, Id, lastBarrierCompleted);
            packet = Read();
        }
        TestCheck.That(packet.Kind == 1 && packet.Opcode == 1347, "屏障仅允许已校验虚弱通知，不跳过其他包");
        TestCheck.Bytes(packet.Body, [1], "body (1,1347)");
        lastBarrierCompleted = UnixTime;
    }

    void ValidateStamina(WirePacket packet, ushort character, long earliest)
    {
        TestCheck.That(packet is { Kind: 0, Opcode: 4, Body.Length: 1 } && fixture is not null,
            "虚弱通知必须有单字节正文及本人隔离数据库");
        var saved = fixture!.Query("SELECT stamina_until FROM characters WHERE id=$p0", character);
        TestCheck.That(saved is [[long]], "虚弱通知必须绑定已选择的本人角色");
        long until = Convert.ToInt64(saved[0][0]);
        long finished = UnixTime;
        long first = Math.Clamp(100 - ((Math.Max(0, until-earliest)+59)/60)*9, 10, 100);
        long last = Math.Clamp(100 - ((Math.Max(0, until-finished)+59)/60)*9, 10, 100);
        TestCheck.That(packet.Body[0] >= first && packet.Body[0] <= last && (100-packet.Body[0]) % 9 == 0,
            "虚弱百分比符合本人落库期限及本版每分钟恢复规则");
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
    public static string DecodeActorAdventureName(byte[] body)
    {
        ReadOnlySpan<byte> field = body.AsSpan(56, 17);
        int length = field.IndexOf((byte)0);
        TestCheck.That(length is >= 0 and <= 16, "Actor昵称的17B字段必须保留NUL终止");
        return names.GetString(field[..length]);
    }
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
        byte[]? sceneSkills = null, byte[]? combos = null, bool resumeTown = false, bool room = true, bool premiumContracts = false, ushort avatarExpansion = 105)
    {
        if (!resumeTown && !requestedRoster)
            Send(8, [255, 255, 2]);
        long selectionStarted = UnixTime;
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
        // 无存档也必须先初始化客户端模型；期望值独立于服务端 Builder。
        initialGameOptions ??= Build(w =>
        {
            w.Write(204u);
            for (int slotIndex = 0; slotIndex < 102; slotIndex++) w.Write((ushort)0x7FFF);
            w.Write(0u);
            w.Write(0u);
        });
        InitialGameOptions = Expect(0, 173, initialGameOptions).Body;
        var middle = new List<WirePacket>();
        SkillInfo = [];
        SceneSkillInfo = [];
        CompletedQuestHistory = [];
        CollectBoxes = [];
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
        TestCheck.That(avatars.Body[0] == 1 && U16(avatars.Body, 1) == avatarExpansion && pets.Body[0] == 7, "special container initialization and avatar capacity");
        TestCheck.That(medals.Body[0] == 38, "medal container initialization");
        SpecialContainers = [creatures, avatars, pets, medals];
        var tail = new List<WirePacket>();
        var mails = new List<byte[]>();
        ClientSettings = [];
        foreach (var notice in inventoryNotices ?? [])
            tail.Add(Expect(notice.Kind, notice.Opcode));
        void ExpectSelectionStamina()
        {
            ushort character = U16(prefix[0].Body, 9);
            var saved = fixture!.Query("SELECT stamina_until FROM characters WHERE id=$p0", character);
            TestCheck.That(saved is [[long]], "选角场景虚弱状态绑定本人持久角色");
            if (Convert.ToInt64(saved[0][0]) > 0 || staminaPercent is < 100)
                ValidateStamina(Expect(0, 4), character, selectionStarted);
        }
        foreach (var notice in expected.Skip(afterCargo))
        {
            if (notice == ((byte)0, (ushort)124)) ReadSeriaInitialization();
            // 普通入城在124前恢复；换频道等最终24绑定后恢复。
            if (!resumeTown && notice == ((byte)0, (ushort)124)) ReadSceneInitialization();
            if (notice == ((byte)0, (ushort)3))
            {
                var packet = Read();
                var collectBoxes = new List<WirePacket>();
                while (packet.Kind == 0 && packet.Opcode == 1030)
                {
                    TestCheck.That(packet.Body.Length >= 8 && packet.Body[0] != 0 && packet.Body[1] is 0 or 1 &&
                        packet.Body[6] is 0 or 1 && packet.Body.Length == 8 + packet.Body[7] * 4 &&
                        (packet.Body[6] == 0 || U32(packet.Body, 2) == 0),
                        "宝珠收集箱初始化使用完整固定头与u8物品计数");
                    collectBoxes.Add(packet);
                    packet = Read();
                }
                TestCheck.That(collectBoxes.Count > 0 &&
                    collectBoxes.Select(row => row.Body[0]).Distinct().Count() == collectBoxes.Count &&
                    collectBoxes.Count(row => row.Body[1] == 1) == 1,
                    "宝珠收集箱逐箱初始化且仅一个箱被选中");
                CollectBoxes = collectBoxes.ToArray();
                // 史诗之书后先逐箱恢复1030，再下发 #233 的1536强袭每日信息。
                TestCheck.That(packet.Kind == 0 && packet.Opcode == 1536 && packet.Body.Length == 5, "强袭每日信息在邮件前初始化");
                AssaultDailyInfo = packet.Body;
                packet = Read();
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
                AdventureName = Expect(0, 1375).Body;
                packet = Read();
                TestCheck.That(packet.Kind == 0 && packet.Opcode == 1343, "冒险团信息在角色资产后初始化");
                AdventureInfo = packet.Body;
                int nameSize = checked((int)U32(AdventureInfo, 18 + 7440));
                TestCheck.That(AdventureName.AsSpan().SequenceEqual(new byte[5]) ||
                    AdventureName.Length == nameSize + 5 && AdventureName[^1] == 0 &&
                    AdventureName.AsSpan(0, nameSize + 4).SequenceEqual(AdventureInfo.AsSpan(18 + 7440)),
                    "1375回放已命名账号名称或未命名账号空名，尾标志为0");
                AdventureRefresh = Expect(0, 1349).Body;
                // #169 在冒险团之后初始化两种拍卖服务；逐包核验完整正文，保留原城镇状态断言。
                Expect(0, 183, [0, 1]);
                Expect(0, 183, [1, 1]);
                packet = Read();
                TestCheck.That(mails.Count > 0 && packet.Kind == 0 && packet.Opcode == 3, "mailbox initializes after account assets and before town state");
                tail.Add(packet);
            }
            else
            {
                tail.Add(Expect(notice.Kind, notice.Opcode));
                // 恢复城镇时LocationAsync不发24，虚弱通知跟在23后；最终24仅绑定已恢复场景。
                if ((!resumeTown && notice == ((byte)0, (ushort)24) || resumeTown && notice == ((byte)0, (ushort)23)) && fixture is not null)
                    ExpectSelectionStamina();
                if (notice == ((byte)0, (ushort)53))
                    ReadCardMembershipState(initialization: true);
            }
        }
        if (resumeTown)
            ReadSceneInitialization();
        Mailbox = mails.ToArray();
        WirePacket[] packets = [.. prefix, .. middle, .. tail];
        Id = U16(packets[0].Body, 9);
        ActorAdventureName = DecodeActorAdventureName(packets[2].Body);
        string fullAdventureName = names.GetString(AdventureName, 4, AdventureName.Length - 5);
        TestCheck.That(fullAdventureName.StartsWith(ActorAdventureName, StringComparison.Ordinal) &&
            (NameWidth(fullAdventureName) <= 16 ? ActorAdventureName == fullAdventureName : NameWidth(ActorAdventureName) <= 16),
            "本人Actor与1375回放同一账号名称，窄字段不截断双字节字符");
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

        void ReadSeriaInitialization()
        {
            var events = Expect(0, 108);
            TestCheck.That(events.Protected, "入城的赛丽亚事件配置使用保护压缩帧");
            var fatigue = Expect(0, 36);
            TestCheck.That(fatigue.Body.Length == 10, "EVENT_INFO建立城镇HUD后同步本版五个u16疲劳字段");
            using (var compressed = new MemoryStream(events.Body, writable: false))
            using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
            {
                byte[] payload = new byte[68];
                zlib.ReadExactly(payload);
                TestCheck.That(zlib.ReadByte() == -1, "事件正文必须恰好68B，解压不产生无界输出");
                TestCheck.Bytes(payload, Build(writer =>
                {
                    writer.Write((ushort)1);
                    writer.Write((ushort)2037);
                    writer.Write(1u);
                    writer.Write(0u);
                    writer.Write(0u);
                    writer.Write((byte)0);
                    writer.Write((byte)1);
                    writer.Write((ushort)2037);
                    writer.Write(8u);
                    for (int index = 1; index < 12; index++) writer.Write(0u);
                }), "独立核对事件2037、最大阶段8及完整尾部，不能跳过新通知");
            }
            var gauge = Expect(0, 412);
            SeriaLuckInfo = gauge.Body;
            TestCheck.That(!gauge.Protected && gauge.Body.Length == 2 && gauge.Body[0] == gauge.Body[1] &&
                gauge.Body[0] <= 40 && gauge.Body[0] % 5 == 0, "幸运值初始通知使用普通CRC/序号，两端阶段一致且不超8");
        }

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
