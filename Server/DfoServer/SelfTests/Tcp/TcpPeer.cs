using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.IO.Compression;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Events;

namespace DfoServer.Testing;

internal sealed record WirePacket(byte Kind, ushort Opcode, byte[] Body, bool Protected = false);
// 独立实现测试帧与 CRC，不能调用服务端的 Frame、Parser、Builder 来自证。
internal sealed partial class TcpPeer : IDisposable
{
    public byte[]? DimensionEntranceState { get; private set; }
    public uint? AncientGateEnergy { get; private set; }
    internal void ReadAncientGate(WirePacket packet)
    {
        TestCheck.That(packet is { Kind: 0, Opcode: 772, Body.Length: 4 }, "772按完整u32能量合同接收");
        AncientGateEnergy = U32(packet.Body, 0);
    }
    internal void ReadDimensionEntrance(WirePacket packet)
    {
        TestCheck.That(packet is { Kind: 0, Opcode: 815, Body.Length: 9 }, "815按完整u32/u32/u8合同接收");
        DimensionEntranceState = packet.Body;
    }
    public static readonly (byte Kind, ushort Opcode)[] Entry =
        [(1, 4), (0, 1382), (0, 2), (0, 2), (0, 13), (0, 988), (0, 356), (0, 13), (0, 13), (0, 950), (0, 53), (0, 427), (0, 1152), (0, 3), (0, 1153), (0, 22), (0, 23), (0, 24), (0, 1379), (0, 124)];
    public static readonly (byte Kind, ushort Opcode)[] PreparedEntry =
        [(1, 4), (0, 1382), (0, 2), (0, 2), (0, 13), (0, 988), (0, 356), (0, 13), (0, 13), (0, 950), (0, 53), (0, 427), (0, 1152), (0, 3), (0, 1153), (0, 22), (0, 23), (0, 24), (0, 1379), (0, 124)];
    readonly TcpClient client = new()
    {
        NoDelay = true
    };
    readonly NetworkStream stream;
    readonly TcpTestFixture? fixture;
    readonly string? loginAccount;
    ushort sequence;
    ushort clientSequence;
    internal ushort NextClientSequence => clientSequence;
    bool requestedRoster;
    readonly byte channelNumber;
    byte? staminaPercent;
    long lastBarrierCompleted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    long UnixTime => (fixture?.UtcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
    public byte[] Roster { get; private set; } = [];
    public byte[] LoginEndpoint { get; private set; } = [];
    public WirePacket[] InitialAccountMaterialLists { get; private set; } = [];
    public byte[] StrongwayHistory { get; private set; } = [];
    public byte[] StrongwayAccountRewards { get; private set; } = [];
    public byte[] StrongwayNpcDisplay { get; private set; } = [];
    public byte[] PvpTotalActors { get; private set; } = [];
    public byte[] PvpMissions { get; private set; } = [];
    internal int PvpMissionRewardBatches { get; set; }
    public byte[] SkillInfo { get; private set; } = [];
    public byte[][] SkillGuideInfo { get; private set; } = [];
    public byte[] SceneSkillInfo { get; private set; } = [];
    public byte[] AvailableQuests { get; private set; } = [];
    public byte[] CompletedQuestHistory { get; private set; } = [];
    public int[] CompletedStoryBooks { get; private set; } = [];
    public SkinStorageKey[] RecentSkins { get; private set; } = [];
    public byte[] KnightShieldDeckBody { get; private set; } = [];
    public ushort Id { get; internal set; }

    internal DfoServer.GameWorld.Towns.Location SavedTownPosition()
    {
        var row = (fixture ?? throw new InvalidOperationException("位置预期缺少隔离夹具"))
            .Query("SELECT town,area,x,y,direction FROM characters WHERE id=$p0", Id).Single();
        return new(Convert.ToByte(row[0]), Convert.ToByte(row[1]), Convert.ToUInt16(row[2]),
            Convert.ToUInt16(row[3]), Convert.ToByte(row[4]));
    }

    // 预期来自夹具的原持久位置、完成位与只读资源；不调用生产Session或Builder，也不接受收到的包体自证。
    internal byte[] SelectionNoticeBody(ushort griefLayer = 0, ushort despairLayer = 0, int legendDungeon = 0,
        bool? incompleteHellQuest = null)
    {
        bool missing = incompleteHellQuest ?? (fixture ?? throw new InvalidOperationException("选图预期缺少夹具")).HasIncompleteHellQuest(Id);
        int shift = missing ? 2 : 0;
        byte[] expected = new byte[42 + shift];
        expected[8] = missing ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(9 + shift), legendDungeon);
        BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(20 + shift), despairLayer);
        BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(39 + shift), griefLayer);
        // 退役候选计数保留为零，不改变现有字段偏移。
        return expected;
    }
    public WirePacket[] SpecialContainers { get; private set; } = [];
    public List<WirePacket> RestoredConsumableStates { get; } = [];
    public byte[] CloneAvatarSources { get; private set; } = [];
    public byte[][] Mailbox { get; private set; } = [];
    public byte[] TitleProgress { get; private set; } = [];
    public byte[] AssaultDailyInfo { get; private set; } = [];
    public byte[][] EnterCounts { get; private set; } = [];
    public byte[] GriefTowerClearInfo { get; private set; } = [];
    public byte[] DazzlementBindings { get; private set; } = [];
    public byte[] DazzlementState { get; private set; } = [];
    public byte[] ItemMakingSkills { get; private set; } = [];
    public byte[] EpicProductionState { get; private set; } = [];
    public byte[] EpicWishInformation { get; private set; } = [];
    public WirePacket[] CollectBoxes { get; private set; } = [];
    public WirePacket[] TitleBooks { get; private set; } = [];
    public byte[] ExpertContract { get; private set; } = [];
    public byte[] PremiumInfo { get; private set; } = [];
    public byte[] SeriaLuckInfo { get; private set; } = [];
    public byte[] RaidWeeklyClearInfo { get; private set; } = [];
    public byte[] LukeChannelCardsInfo { get; private set; } = [];
    // 默认四团本全天开放：卢克、漩涡、普雷、安徒恩按目录顺序，再追加诺兰及积分商城；专项可显式覆盖。
    public ushort[] ExpectedOpenEvents { get; set; } = [282, 2449, 414, 181, ServerEventService.NolanVisibilityId, ServerEventService.MileageShopId];
    public ushort[] ExpectedActiveEvents { get; set; } = [2395];
    public IReadOnlyDictionary<ushort, IReadOnlyList<uint>> ExpectedEventValues { get; set; } =
        new Dictionary<ushort, IReadOnlyList<uint>>();
    public uint? ExpectedBattlePassEnd { get; set; }
    public byte[] EventInfoPayload { get; private set; } = [];
    // 专项回调另核精确累计与状态；通用路径同样完整消费已知目录/扩展/状态尾段。
    internal Action<byte[]>? ValidateNativePromotionEvents { get; set; }
    internal Action<TcpPeer>? BeforeMileageInitialization { get; set; }
    public byte[] MileageLimits { get; private set; } = [];
    public byte[] CubeContractInfo { get; private set; } = [];
    public WirePacket[] ClientSettings { get; private set; } = [];
    public byte[] ChatEmoticonInfo { get; private set; } = [];
    public byte[] RecentEmoticonInfo { get; private set; } = [];

    internal void ReadChatEmoticons(WirePacket packet)
    {
        var expected=fixture?.ExpectedChatEmoticons(Id)??throw new InvalidOperationException("表情通知缺少夹具资源与角色资格");
        TestCheck.That(packet is {Kind:0,Opcode:411},"聊天表情通知使用411");
        TestCheck.Bytes(packet.Body,expected,"表情通知逐字节对应当前角色资格和账号指令");
        ChatEmoticonInfo=packet.Body;
    }
    public Dictionary<int, byte> DailyBoosterUsed { get; } = [];
    public byte[][] InitialDailyBoosterPackets { get; private set; } = [];
    public byte[] InitialGameOptions { get; private set; } = [];
    public byte[] AdventureName { get; private set; } = [];
    public string ActorAdventureName { get; private set; } = "";
    public byte[] AdventureInfo { get; private set; } = [];
    public byte[] PlayerBody { get; private set; } = [];
    public byte[] AdventureRefresh { get; private set; } = [];
    public byte[] WeeklyDungeonConfig { get; private set; } = [];
    public WirePacket[] TagEvildomInfo { get; private set; } = [];
    public byte[] AvatarPresetList { get; private set; } = [];
    public byte[] NpcFavor { get; private set; } = [];
    public byte OwnGuildGrade { get; private set; } = 100;
    public byte[] GuildNpcs { get; private set; } = [];
    public byte[] CardMembershipInformation { get; private set; } = [];
    public byte[] PcRoomAccelerationState { get; private set; } = [];
    public byte[] DevilService0 { get; private set; } = [];
    public Dictionary<byte, long> PremiumServices { get; private set; } = [];
    public byte[] DungeonPermission { get; private set; } = [];
    public Dictionary<int, (byte Daily, byte Weekly)> DungeonEnterCounts { get; private set; } = [];

    static readonly Encoding names;
    static TcpPeer()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        names = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public TcpPeer(int port, string? localAddress = null, byte channelNumber = 10, string serverAddress = "127.0.0.1",
        TcpTestFixture? fixture = null, string? account = null)
    {
        this.fixture = fixture;
        loginAccount = account?.Trim().ToLowerInvariant();
        this.channelNumber = channelNumber;
        if (fixture?.IsolatedMockBattleSuppressed == true)
            ExpectedOpenEvents = [.. ExpectedOpenEvents.Where(id => id != ServerEventService.NolanVisibilityId)];
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
        var peer = new TcpPeer(gamePort ?? fixture.Options.GamePort, channelNumber: channelNumber, serverAddress: serverAddress, fixture: fixture, account: account);
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
        => SendAtSequence(opcode, clientSequence++, body);

    public void SendAtSequence(ushort opcode, ushort wireSequence, byte[]? body = null)
    {
        body ??= [];
        byte[] frame = new byte[13 + body.Length];
        frame[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(3), frame.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(11), wireSequence);
        body.CopyTo(frame, 13);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), Crc(frame.AsSpan(11)));
        stream.Write(frame);
        if (opcode == 8 && body.AsSpan().SequenceEqual(new byte[] { 255, 255, 2 })) requestedRoster = true;
        if (opcode == 7 && body.Length == 0) requestedRoster = false;
    }

    // 可选通知探针会原样归还读到的下一包，后续Expect仍按严格包序断言，不跳过任何业务包。
    WirePacket? pushedBack;
    public WirePacket Read()
    {
        if (pushedBack is { } packet) { pushedBack = null; return packet; }
        return ReadCore(channelNumber);
    }
    // 服务端在ACK、房间通知与补发buff之后才发36，读到房间通知时它尚未上路；
    // 是否发送取决于真实疲劳规则，无包时必须尽快返回，不能把可选通知拖成5秒socket超时。
    public void ExpectOptionalFatigue()
    {
        long deadline = Environment.TickCount64 + 1000;
        while (!stream.DataAvailable && Environment.TickCount64 < deadline) Thread.Sleep(1);
        if (!stream.DataAvailable) return;
        var packet = Read();
        if (packet is { Kind: 0, Opcode: 36 })
        {
            TestCheck.That(packet.Body.Length == 10, "正式疲劳通知保留五个u16的10字节合同");
            TestCheck.That(U16(packet.Body, 0) + U16(packet.Body, 6) == U16(packet.Body, 2),
                "正式疲劳通知已用与剩余之和等于上限");
            return;
        }
        pushedBack = packet;
    }

    void ReadStoryBooks(byte[] body)
    {
        TestCheck.That(body.Length >= 4, "703故事簿必须保留u32数量");
        uint count = U32(body);
        TestCheck.That(count < 30000 && body.Length == 4L + count * 48L, "703原生记录宽度为48字节，不能截断或追加尾部");
        var ids = new int[count];
        for (int at = 0; at < ids.Length; at++)
        {
            int offset = 4 + at * 48;
            TestCheck.That(body[offset] == 1 && body.AsSpan(offset + 1, 3).IndexOfAnyExcept((byte)0) < 0 &&
                body.AsSpan(offset + 8, 40).IndexOfAnyExcept((byte)0) < 0, "完成态703不伪造未读状态和阅读者元数据");
            ids[at] = checked((int)U32(body, offset + 4));
        }
        TestCheck.That(ids.All(id => id is > 0 and < 30000) && ids.Distinct().Count() == ids.Length && ids.SequenceEqual(ids.Order()),
            "故事簿章节号合法、稳定且不重复");
        CompletedStoryBooks = ids;
    }

    // 仅显式的跨频道公开查询允许安装目标频道Actor；普通同屏及本人通知仍须匹配本连接频道。
    public WirePacket ExpectOtherPlayerActor(ushort target, byte targetChannel)
    {
        var packet = ReadCore(targetChannel);
        TestCheck.That(packet is { Kind: 0, Opcode: 2, Body.Length: > 122 } && packet.Body[0] == 0 &&
            U16(packet.Body, 1) == 1 && U16(packet.Body, 121) == target, "跨频道公开查询必须只安装指定目标Actor");
        return packet;
    }

    WirePacket ReadCore(byte actorChannel, int? firstByte = null)
    {
        byte[] header = new byte[16];
        if (firstByte is { } first)
        {
            header[0] = checked((byte)first);
            stream.ReadExactly(header.AsSpan(1));
        }
        else stream.ReadExactly(header);
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
            TestCheck.Bytes(frame.AsSpan(19, 2), [2, actorChannel], "USERINFO进入本次期待的频道Actor表，普通读取保持接收者频道");
        var packet = new WirePacket(header[0], BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1)), frame[16..], protectedFrame);
        if (packet is { Kind: 1, Opcode: 4, Body.Length: >= 11 } && packet.Body[0] == 1) Id = U16(packet.Body, 9);
        if (packet is { Kind: 0, Opcode: 4, Body.Length: 1 }) staminaPercent = packet.Body[0];
        if (packet is { Kind: 0, Opcode: 703 }) ReadStoryBooks(packet.Body);
        if (packet is { Kind: 0, Opcode: 127 })
        {
            TestCheck.That(packet.Body.Length == 1 && packet.Body[0] is >= 1 and <= 5 or 100,
                "本人公会职位通知必须是独立单字节合法职位或无会籍100");
            OwnGuildGrade = packet.Body[0];
        }
        return packet;
    }

    public WirePacket Expect(byte kind, ushort opcode, byte[]? body = null)
    {
        var packet = Read();
        int countUpdates = 0;
        while (opcode != 1016 && packet is { Kind: 0, Opcode: 1016 })
        {
            TestCheck.That(packet.Body.Length == 14 && U32(packet.Body, 0) != Id,
                "异步婚姻属性只更新远端Actor且保持14字节合同");
            packet = Read();
        }
        bool griefAnnounced = false;
        // 选图附带的状态通知逐项校验后记录，550须按两祭坛及悲叹当前楼层逐项匹配真实账本；保留原27及正文断言。
        while (kind == 0 && opcode == 27 && packet.Kind == 0 &&
               (packet.Opcode == 1536 && packet.Body.Length == 5 || packet.Opcode == 1303 && packet.Body.Length == 2 ||
                packet.Opcode is 550 or 1608 or 815 or 772))
        {
            if (packet.Opcode == 1536)
            {
                AssaultDailyInfo = packet.Body;
                if (fixture?.ExpectedDaybreakSelectionRemaining(Id) is { } remaining)
                {
                    // 仅530入口世界地图在1536之后发布1545→550，二者完整核验后仍须原27。
                    byte[] daybreak = new byte[268];
                    BinaryPrimitives.WriteUInt32LittleEndian(daybreak, remaining);
                    BinaryPrimitives.WriteUInt32LittleEndian(daybreak.AsSpan(4), 260);
                    BinaryPrimitives.WriteUInt32LittleEndian(daybreak.AsSpan(12), 530);
                    BinaryPrimitives.WriteUInt32LittleEndian(daybreak.AsSpan(16), 2);
                    Expect(0, 1545, daybreak);
                    Expect(0, 550, [0x12, 0x02, 0, 0, remaining, 0]);
                    DungeonEnterCounts[530] = (remaining, 0);
                }
            }
            else if (packet.Opcode == 1608) ExpectDazzlementBindings(packet);
            else if (packet.Opcode == 815) ReadDimensionEntrance(packet);
            else if (packet.Opcode == 772) ReadAncientGate(packet);
            else if (packet.Opcode == 1303)
            {
                GriefTowerClearInfo = packet.Body;
                griefAnnounced = true;
            }
            else
            {
                TestCheck.That(griefAnnounced && ++countUpdates == 1,
                    "亡者峡谷状态之后只允许一组两祭坛与悲叹次数通知，不扩展普通选图包序");
                ReadDungeonEnterCounts(packet);
            }
            packet = Read();
        }
        TestCheck.That(packet.Kind == kind && packet.Opcode == opcode, $"packet order: expected ({kind},{opcode}), got ({packet.Kind},{packet.Opcode})");
        if (body is not null)
            TestCheck.Bytes(packet.Body, body, $"body ({kind},{opcode})");
        return packet;
    }

    internal void ExpectDazzlementBindings(WirePacket? information = null, ushort? characterId = null)
    {
        var packet = information ?? Expect(0, 1608);
        var f = fixture ?? throw new InvalidOperationException("眩惑通知核验需要真实账本");
        int week = DfoServer.Game.Dungeons.WeeklyDungeonPolicy.WeekKey(f.UtcNow);
        var account = f.Query("SELECT account FROM characters WHERE id=$p0", characterId ?? Id).Single()[0];
        var rows = f.Query("""
            SELECT c.name,c.job FROM account_dazzlement_tower_bindings b
            JOIN characters c ON c.id=b.character_id AND c.account=b.account
            WHERE b.account=$p0 AND b.week=$p1 ORDER BY b.slot
            """, account, week);
        TestCheck.That(rows.Length <= 5, "眩惑账号本周绑定名单最多五名");
        TestCheck.Bytes(packet.Body, Build(w =>
        {
            w.Write((byte)rows.Length);
            foreach (var row in rows)
            {
                byte[] name = names.GetBytes((string)row[0]);
                w.Write(name.Length); w.Write(name); w.Write(Convert.ToByte(row[1]));
            }
        }), "1608完整名称/职业名单等于当前账号本周持久绑定");
        DazzlementBindings = packet.Body;
        ExpectDazzlementState(characterId: characterId);
    }

    internal void ExpectDazzlementState(byte? expectedUsed = null, ushort? characterId = null)
    {
        var f = fixture ?? throw new InvalidOperationException("眩惑通知核验需要真实账本");
        int day = DfoServer.Game.Inventory.Models.ShopPurchaseCount.Today(f.UtcNow);
        int week = DfoServer.Game.Dungeons.WeeklyDungeonPolicy.WeekKey(f.UtcNow);
        var account = f.Query("SELECT account FROM characters WHERE id=$p0", characterId ?? Id).Single()[0];
        var rows = f.Query("""
            SELECT last_entry_day FROM account_dazzlement_tower_bindings
            WHERE account=$p0 AND character_id=$p1 AND week=$p2
            """, account, characterId ?? Id, week);
        byte used = rows.Any(row => Convert.ToInt32(row[0]) >= day) ? (byte)1 : (byte)0;
        if (expectedUsed is { } expected) TestCheck.That(used == expected, "眩惑日次数的独立持久账本预期");
        DazzlementState = Expect(0, 1605, [0, 0, 0, 0, 0, 0, used, 0, 0, 0, 0]).Body;
    }

    // 已显式校验悲叹状态的选图用例使用同一真实账本断言，不交给27的兼容读取再次猜测前置包。
    public void ExpectAltarEnterCounts() => ReadDungeonEnterCounts(Read());

    void ReadDungeonEnterCounts(WirePacket first)
    {
        TestCheck.That(fixture is not null && Id != 0, "550必须绑定已选角的本人隔离账本");
        var local = fixture!.UtcNow.ToOffset(TimeSpan.FromHours(8)).AddHours(-6);
        int day = DateOnly.FromDateTime(local.DateTime).DayNumber;
        foreach (int dungeonId in new[] { 11006, 11007 })
        {
            var packet = dungeonId == 11006 ? first : Read();
            TestCheck.That(packet is { Kind: 0, Opcode: 550, Body.Length: 6 } && U32(packet.Body, 0) == dungeonId,
                "550必须按11006、11007顺序各发一个完整6字节通知");
            long used = Convert.ToInt64(fixture.Query(
                "SELECT count(*) FROM dungeon_special_entry_receipts WHERE character_id=$p0 AND dungeon_id=$p1 AND service_day=$p2",
                Id, dungeonId, day)[0][0]);
            TestCheck.That(packet.Body[4] == Math.Max(0, 3 - used) && packet.Body[5] == 0,
                "550日剩余等于本游戏日已提交账本，周剩余为零");
            DungeonEnterCounts[dungeonId] = (packet.Body[4], packet.Body[5]);
        }
        ExpectGriefTowerEnterCounts();
        ExpectDespairTowerEnterCounts();
    }

    public void ExpectGriefTowerEnterCounts()
    {
        // 手工原生登录组不注入fixture，角色始终未挑战悲叹；独立核对首层满额的完整字节。
        if (fixture is null)
        {
            Expect(0, 550, [0xFB, 0x13, 0, 0, 1, 0]);
            DungeonEnterCounts[5115] = (1, 0);
            return;
        }
        TestCheck.That(Id != 0, "悲叹550必须绑定当前角色");
        var state = fixture!.Query("""
            SELECT COALESCE(g.layer,1),COALESCE(g.game_day,0) FROM characters c
            LEFT JOIN character_grief_tower g ON g.character_id=c.id WHERE c.id=$p0
            """, Id).Single();
        int layer = Convert.ToInt32(state[0]);
        int day = int.Parse(fixture.UtcNow.UtcDateTime.AddHours(2).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        byte remaining = Convert.ToInt32(state[1]) < day && layer <= 100 ? (byte)1 : (byte)0;
        foreach (int dungeonId in new[] { 5115, 5114 + Math.Min(layer, 100) }.Distinct())
        {
            byte[] expected = new byte[6];
            BinaryPrimitives.WriteUInt32LittleEndian(expected, checked((uint)dungeonId));
            expected[4] = remaining;
            Expect(0, 550, expected);
            DungeonEnterCounts[dungeonId] = (remaining, 0);
        }
    }

    public void ExpectDespairTowerEnterCounts()
    {
        // 原生登录组没有fixture且不挑战塔，仍严格核对完整首层初始化包。
        if (fixture is null)
        {
            Expect(0, 550, [0, 0x2B, 0, 0, 1, 0]);
            DungeonEnterCounts[11008] = (1, 0);
            return;
        }
        TestCheck.That(Id != 0, "绝望550必须绑定当前角色");
        var local = fixture.UtcNow.ToOffset(TimeSpan.FromHours(8)).AddHours(-6);
        int day = DateOnly.FromDateTime(local.DateTime).DayNumber;
        var state = fixture.Query("""
            SELECT COALESCE(p.next_layer,1),COALESCE(p.last_clear_day,0),
                (SELECT count(*) FROM dungeon_special_entry_receipts e WHERE e.character_id=c.id AND e.kind=3 AND e.service_day=$p1)
            FROM characters c LEFT JOIN dungeon_despair_progress p ON p.character_id=c.id WHERE c.id=$p0
            """, Id, day).Single();
        byte remaining = Convert.ToInt64(state[2]) == 0 && Convert.ToInt32(state[1]) != day ? (byte)1 : (byte)0;
        foreach (int dungeonId in new[] { 11008, 11007 + Convert.ToInt32(state[0]) }.Distinct())
        {
            byte[] expected = new byte[6];
            BinaryPrimitives.WriteUInt32LittleEndian(expected, checked((uint)dungeonId));
            expected[4] = remaining;
            Expect(0, 550, expected);
            DungeonEnterCounts[dungeonId] = (remaining, 0);
        }
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
                .Select(pair => pair.Key).Order()), "每次选角恢复成长/晶体横幅，128只通过完整1760初始化，不能进入有符号66类型字段");
        TestCheck.That(PremiumServices.ContainsKey(56) && PremiumServices.ContainsKey(58),
            "统一契约列表包含56/58的有效期限或显式清除值，不覆盖22/27");
        TestCheck.That(DevilService0.Length == 77 && DevilService0[0] == 1 && U16(DevilService0, 1) == 1 &&
            DevilService0.AsSpan(3, 2).IndexOfAnyExcept((byte)0) == -1 && DevilService0[13] <= 10 &&
            Enumerable.Range(0, 8).All(slot => DevilService0.AsSpan(5 + slot * 9, 4).IndexOfAnyExcept((byte)0) == -1) &&
            Enumerable.Range(0, 8).Any(slot => U32(DevilService0, 9 + slot * 9) > 0) == (PremiumServices[58] > 0),
            "魔王八项期限独立投影，58概览与至少一项有效服务一致，黄金牌次数不超过10");
    }
    public void Barrier(Action<WirePacket>? validateNotification = null)
    {
        SendBarrier();
        var packet = Read();
        int staminaUpdates = 0, notifications = 0;
        // 撤退虚弱仍独立核验；额外业务通知必须由调用方逐包断言，默认屏障不跳包。
        while (packet.Kind == 0 && (packet.Opcode == 4 || validateNotification is not null))
        {
            TestCheck.That(++notifications <= 8, "屏障前业务通知数量有界");
            if (packet.Opcode == 4)
            {
                TestCheck.That(++staminaUpdates <= 2, "屏障前虚弱通知数量有界");
                ValidateStamina(packet, Id, lastBarrierCompleted);
            }
            else validateNotification!(packet);
            packet = Read();
        }
        if (packet is { Kind: 0, Opcode: 550 })
        {
            ReadDungeonEnterCounts(packet);
            packet = Read();
        }
        TestCheck.That(packet.Kind == 1 && packet.Opcode == 1347, "屏障仅允许已校验虚弱与两祭坛次数通知，不跳过其他包");
        TestCheck.Bytes(packet.Body, [1], "body (1,1347)");
        lastBarrierCompleted = UnixTime;
    }

    void ValidateConsumableStates(WirePacket packet, ushort character, long earliest)
    {
        TestCheck.That(fixture is not null && packet.Body.Length >= 9 && packet.Body[0] > 0 &&
            packet.Body.Length == 1 + packet.Body[0] * 8, "持续状态通知有完整独立条目，不恢复空列表或短包");
        var rows = fixture!.Query("SELECT item_id,until_ms FROM character_consumable_states WHERE character_id=$p0 AND kind=$p1 ORDER BY item_id",
            character, packet.Opcode == 174 ? 0 : 1).ToDictionary(row => Convert.ToInt32(row[0]), row => Convert.ToInt64(row[1]));
        var seen = new HashSet<int>();
        for (int i = 0; i < packet.Body[0]; i++)
        {
            int id = checked((int)U32(packet.Body,1+i*8));
            long remaining = U32(packet.Body,5+i*8);
            TestCheck.That(rows.TryGetValue(id, out long until) && seen.Add(id) && remaining > 0 &&
                remaining <= int.MaxValue/1000 && remaining <= (until-earliest*1000)/1000 &&
                remaining >= Math.Max(0,(until-(UnixTime+1)*1000)/1000),
                "状态来源为本人已提交账本，使用倒计时，不续期或重复授予效果");
        }
        TestCheck.That(rows.Where(row => row.Value >= (UnixTime+1)*1000).All(row => seen.Contains(row.Key)),
            "仍有效的完整状态集合不能被截断");
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

    /// <summary>
    /// 负向断言：窗口内不得出现指定的下行业务帧。窗口内的无关通知照常读出并消费，序号与后续断言保持连续；
    /// 对端关闭不算静默通过。用于"只记录不发包"这类必须证明线上没有该帧的场景。
    /// </summary>
    public void NoFrame(int milliseconds, string note, params (byte Kind, ushort Opcode)[] forbidden)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            int remaining = milliseconds - (int)watch.ElapsedMilliseconds;
            if (remaining <= 0)
                break;
            if (!client.Client.Poll(remaining * 1000, SelectMode.SelectRead))
                break;
            // Poll 可读还可能是对端已关闭：此时没有帧可读，不能当作静默。
            if (client.Client.Available == 0)
                TestCheck.That(false, note + ": 等待静默时连接被对端关闭");
            var packet = Read();
            TestCheck.That(!forbidden.Contains((packet.Kind, packet.Opcode)),
                $"{note}: 窗口 {milliseconds}ms 内不应出现 ({packet.Kind},{packet.Opcode})，实际正文 {Convert.ToHexString(packet.Body)}");
        }

        TestCheck.That(true, note);
    }

    // NetworkStream未包装缓冲读器，ReadExactly只取当前帧；Poll保留下一帧在socket中，不消费或忽略它。
    public void WaitForPendingData(int milliseconds, string note)
    {
        TestCheck.That(milliseconds is > 0 and <= 10000, "等待积压帧必须有界");
        TestCheck.That(client.Client.Poll(TimeSpan.FromMilliseconds(milliseconds), SelectMode.SelectRead) &&
            client.Client.Available > 0, note + ": 未收到数据或对端已经关闭");
    }

    public void ClosedAfterFiendwarPartyRemoval(byte[] expected)
    {
        int first;
        try { first = stream.ReadByte(); }
        catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
        {
            TestCheck.That(true, "connection reset after rejection");
            return;
        }
        if (first < 0)
        {
            Closed();
            return;
        }
        var packet = ReadCore(channelNumber, first);
        TestCheck.That(packet is { Kind: 0, Opcode: 9 }, "only the original party removal may precede the counter failure close");
        TestCheck.Bytes(packet.Body, expected, "party removal retains this run's complete roster identity and channel");
        Closed();
    }

    // 入场回滚用例既允许先收到队伍名册删除NOTI9再关闭，也允许事务失败后直接干净关闭：
    // 生产关闭连接时会丢弃尚未冲刷的写队列，两种顺序都不改变回滚业务断言。
    public void ClosedAfterOptionalPartyRemoval()
    {
        int first;
        try { first = stream.ReadByte(); }
        catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted })
        {
            TestCheck.That(true, "connection reset after rejection");
            return;
        }
        if (first < 0) { Closed(); return; }
        var packet = ReadCore(channelNumber, first);
        TestCheck.That(packet is { Kind: 0, Opcode: 9 }, "回滚关闭前若仍有通知，只能是队伍名册NOTI9");
        Closed();
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
    public static void AssertBlackDiamondActor(byte[] body, bool active, bool dungeon = false, bool pcRoom = false)
    {
        TestCheck.That(body.Length > 134 && body[0] == 0 && U16(body, 1) == 1, "会员刷新使用完整单角色Actor，不以Player冒充PC房字段");
        int count = checked(133 + (int)U32(body, 123));
        TestCheck.That(count < body.Length && body[count - 1] == (dungeon ? 1 : 0), "会员刷新保持当前城镇或副本成员状态");
        TestCheck.That(body[^18] == (dungeon ? 0 : 100), "完整Actor同步正确场景的队伍血蓝百分比，不把城镇既有实时信息覆盖为零");
        int pet = checked(count + 1 + body[count] * 25 + 22);
        int pcroom = checked(pet + 9 + (int)U32(body, pet + 4));
        TestCheck.That(pcroom + 1 < body.Length && body[pcroom] == (pcRoom ? 1 : 0) && body[pcroom + 1] == (active ? 1 : 0),
            "全局网吧身份与账号黑钻外观各自发送，不互相开启或清除");
    }
    public void ExpectUnusedDailyBoosterReset()
    {
        // 调钟可能被上一请求尾部或本次屏障观察；精确收齐一次ACK和完整零次数日界视图。
        TestCheck.That(InitialDailyBoosterPackets.Length > 0, "日界同步已有完整初始化名册");
        SendBarrier();
        bool acknowledged = false;
        int resetChunks = 0;
        while (!acknowledged || resetChunks < InitialDailyBoosterPackets.Length)
        {
            var packet = Read();
            if (packet is { Kind: 1, Opcode: 1347 } && !acknowledged)
            {
                TestCheck.Bytes(packet.Body, [1], "跨日日界同步屏障ACK");
                acknowledged = true;
            }
            else if (packet is { Kind: 0, Opcode: 541 })
            {
                TestCheck.That(resetChunks < InitialDailyBoosterPackets.Length, "跨日礼盒次数分片不能重复");
                var expected = InitialDailyBoosterPackets[resetChunks++];
                TestCheck.That(Inventory.DailyBoosterProjectionTests.Decode(expected).Values.All(used => used == 0),
                    "本用例没有消费每日礼盒，跨日完整恢复零次数");
                TestCheck.Bytes(packet.Body, expected, "跨日541名册、分片顺序及零次数与初始化一致");
            }
            else ValidateServiceDayNotice(packet);
        }
        foreach (var packet in PartyDungeonTcpTests.Drain(this)) ValidateServiceDayNotice(packet);
    }

    internal void ValidateServiceDayNotice(WirePacket packet)
    {
        TestCheck.That(packet.Kind == 0 && fixture is not null && Id != 0, "在线日切通知必须属于当前测试角色");
        switch (packet.Opcode)
        {
            case 36:
                var fatigue = fixture!.Query("SELECT used,maximum,used_maximum,remaining,context_auxiliary FROM character_fatigue WHERE character_id=$p0", Id).Single();
                TestCheck.Bytes(packet.Body, Build(w => { foreach (var field in fatigue) w.Write(checked((ushort)Convert.ToInt64(field))); }), "在线疲劳通知与已提交余额一致");
                break;
            case 99:
                TestCheck.Bytes(packet.Body, [1, 0], "在线日切邮件提醒包体");
                TestCheck.That(fixture!.Query("SELECT count(*) FROM mailbox_messages WHERE recipient_id=$p0 AND delivery_key LIKE 'server-daily-item:%'", Id) is [[long mails]] && mails > 0,
                    "邮件提醒对应当前角色已提交的每日补给");
                break;
            case 550:
                TestCheck.That(packet.Body.Length == 6 && U32(packet.Body) > 0 &&
                    (EnterCounts.Any(row => U32(row) == U32(packet.Body)) || DungeonEnterCounts.ContainsKey((int)U32(packet.Body))),
                    "在线副本次数属于初始化的当前角色副本且包含完整字段");
                break;
            case 1536:
                TestCheck.That(packet.Body.Length == 5 && packet.Body[2] <= 6 && packet.Body[3] == 0 && packet.Body[4] == 0,
                    "在线强袭信息保持完整五字节");
                break;
            case 1343:
                TestCheck.That(packet.Body.Length > 7440 && U16(packet.Body) == Id, "冒险团日切视图绑定当前角色");
                break;
            case 1349:
                TestCheck.That(packet.Body.Length == 50 && U16(packet.Body) == Id, "冒险团头顶日切视图绑定当前角色");
                break;
            case 21 or 356 or 574:
                TestCheck.That(packet.Body.Length > 0, "任务日切通知保留完整非空正文");
                break;
            default:
                throw new InvalidOperationException($"FAIL: 未核对的在线日切通知 {packet.Kind}/{packet.Opcode}");
        }
    }

    public void CheckBlackDiamondRefresh(bool active, bool dungeon = false, bool appearanceChanged = true, bool pcRoom = false,
        bool allowDailyReset = false)
    {
        Send(63);
        var first = Read();
        if (allowDailyReset && first is { Kind: 0, Opcode: 541 })
        {
            // 仅自然到期场景容纳已证前置日界通知，仍严格核对完整初始化名册和后续会员包序。
            TestCheck.That(InitialDailyBoosterPackets.Length > 0, "日界刷新已有完整初始化名册");
            var expected = InitialDailyBoosterPackets.SelectMany(Inventory.DailyBoosterProjectionTests.Decode)
                .Select(row => row.Key).ToArray();
            int chunks = Math.Max(1, (expected.Length + byte.MaxValue - 1) / byte.MaxValue);
            int offset = 0;
            for (int chunk = 0; chunk < chunks; chunk++)
            {
                var rows = Inventory.DailyBoosterProjectionTests.Decode(first.Body);
                int count = Math.Min(byte.MaxValue, expected.Length - offset);
                TestCheck.That(rows.Count == count && rows.Keys.SequenceEqual(expected.Skip(offset).Take(count)) &&
                    rows.Values.All(used => used == 0), "日界541分片完整、有序、不重复且每项已用次数明确归零");
                offset += count;
                if (chunk + 1 < chunks) first = Expect(0, 541);
            }
            foreach (int id in expected) DailyBoosterUsed[id] = 0;
            first = Read();
        }
        bool ceraFirst = first is { Kind: 0, Opcode: 53 };
        var overview = ceraFirst ? Expect(0, 1760) : first;
        TestCheck.That(overview is { Kind: 0, Opcode: 1760 } && overview.Body.Length == 1 + overview.Body[0] * 9 &&
            Enumerable.Range(0, overview.Body[0]).Any(index => overview.Body[1 + index * 9] == 56 &&
                (BinaryPrimitives.ReadInt64LittleEndian(overview.Body.AsSpan(2 + index * 9)) > 0) == active),
            "黑钻刷新包含当前56有效期或明确清除值，不跳过其他通知");
        if (appearanceChanged)
        {
            AssertBlackDiamondActor(Expect(0, 2).Body, active, dungeon, pcRoom);
            var player = Expect(0, 2).Body;
            TestCheck.That(player[0] == 1 && U16(player, 22) == Id && player[36 + 84] == 100,
                "会员刷新保持Actor再完整本人Player包序，不改变无虚弱时的属性");
        }
        // 屏障ACK不表示命令尾部已完成；GM或时钟变化也可能由上一请求尾部发现。
        if (!ceraFirst) Expect(0, 53);
        Barrier();
    }
    public void CheckPcRoomRefresh(bool enabled, bool blackDiamond, bool dungeon = false, bool appearanceChanged = true)
    {
        bool returning = !enabled && !dungeon && appearanceChanged;
        Send(63);
        // 完整重选后1347不属于Unselected入口；ACK7和名册本身就是完成条件。
        if (!returning) SendBarrier();
        var notices = new List<WirePacket>();
        for (int count = 0; ; count++)
        {
            if (returning && notices.Count == 3) break;
            TestCheck.That(count < 16, "网吧资格刷新通知数量有界");
            var packet = Read();
            if (packet is { Kind: 1, Opcode: 1347 })
            {
                TestCheck.Bytes(packet.Body, [1], "网吧资格刷新后的正常请求屏障");
                break;
            }
            TestCheck.That(packet.Kind == 0 && packet.Opcode is 2 or 53 or 487 or 488 or 490 or 1517 or 1639 or 1760 ||
                packet is { Kind: 1, Opcode: 7 } && returning,
                "网吧资格刷新只允许已确认通知和城镇失效重选，不得发送66的有符号128类型");
            notices.Add(packet);
        }
        TestCheck.That(notices.Count(packet => packet.Opcode == 53) == 1, "身份刷新保留原CERA查询应答");
        if (returning)
        {
            var returned = notices.Single(packet => packet is { Kind: 1, Opcode: 7 });
            var roster = notices.Single(packet => packet is { Kind: 0, Opcode: 2 });
            TestCheck.Bytes(returned.Body, [1], "网吧失效在安全城镇边界发送真实ACK7");
            TestCheck.Bytes(roster.Body, Roster, "ACK7后完整名册保持原角色和预览，不用1760零期限冒充撤销128");
            TestCheck.That(notices.Count == 3 && notices.IndexOf(returned) < notices.IndexOf(roster),
                "网吧失效完整退回选角，不夹入旧角色Actor、Buff或面板通知");
            requestedRoster = true;
        }
        else if (!appearanceChanged && !enabled)
            TestCheck.That(notices.Count == 1, "同值关闭不重复刷新会员、Actor或面板");
        else
        {
            var premium = notices.Single(packet => packet.Opcode == 1760).Body;
            TestCheck.That(premium.Length == 1 + premium[0] * 9, "网吧资格更新保留完整u8/u8/i64会员表");
            var entries = Enumerable.Range(0, premium[0]).ToDictionary(index => premium[1 + index * 9],
                index => BinaryPrimitives.ReadInt64LittleEndian(premium.AsSpan(2 + index * 9)));
            TestCheck.That(entries.TryGetValue(128, out long seconds) && (enabled ? seconds > 0 : seconds == 0),
                "开启经1760初始化128，副本内关闭只同步期限且等待安全返城重选");
            if (appearanceChanged)
            {
                var actors = notices.Where(packet => packet.Opcode == 2).ToArray();
                TestCheck.That(actors is [{ Body: [0, ..] }, { Body: [1, ..] }], "网吧资格变化按Actor再完整Player恢复，不中断当前Run");
                AssertBlackDiamondActor(actors[0].Body, blackDiamond, dungeon, enabled);
                var buff = notices.Single(packet => packet.Opcode == (enabled ? (ushort)487 : (ushort)488));
                TestCheck.That(buff.Body[0] == 1 && U32(buff.Body, 1) == 1249 &&
                    notices.IndexOf(actors[1]) < notices.IndexOf(buff), "网吧Buff在本人Player恢复之后添加或移除");
            }
            else TestCheck.That(notices.All(packet => packet.Opcode is 53 or 1760 or 1517 or 1639 or 490),
                "Loading期间先恢复会员与面板，Actor及Buff延后到实际加载完成");
            TestCheck.That(notices.Single(packet => packet.Opcode == 1517).Body is [0, 0, 0, 0, 0 or 1] &&
                notices.Single(packet => packet.Opcode == 1639).Body is [0 or 1] &&
                notices.Single(packet => packet.Opcode == 490).Body is [0, 0, 0 or 1], "同次资格变化完整刷新两种领奖状态与燃烧选择");
            TestCheck.That(notices.FindIndex(packet => packet.Opcode == 1760) < notices.FindIndex(packet => packet.Opcode == 1517) &&
                notices.FindIndex(packet => packet.Opcode == 1517) < notices.FindIndex(packet => packet.Opcode == 1639) &&
                notices.FindIndex(packet => packet.Opcode == 1639) < notices.FindIndex(packet => packet.Opcode == 490),
                "资格切换先同步1760，再按1517、1639、490刷新面板，state0核资格时128已经到达");
            PcRoomAccelerationState = notices.Single(packet => packet.Opcode == 490).Body;
        }
        lastBarrierCompleted = UnixTime;
    }
    public void ExpectPcRoomReselection()
    {
        Expect(1, 7, [1]);
        TestCheck.Bytes(Expect(0, 2).Body, Roster, "副本关闭网吧资格后，安全回城才按完整ACK7和名册退回选角");
        requestedRoster = true;
    }
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
        var rows = fixture.Query("""
            SELECT c.context,c.root_id,c.children FROM character_skill_combos c
            LEFT JOIN character_skill_bindings b ON b.character_id=c.character_id AND b.context=c.context AND b.skill_id=c.root_id
            WHERE c.character_id=$p0 ORDER BY c.context,b.slot,c.root_id
            """, characterId);
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

    // 选角就绪后的固定包序：先0x063D装扮预设已应用槽与列表，再720空正文与721五槽。
    // 自定义入城读取器读到124后须调用本方法，避免把这三包留给后续断言。
    public void ReadWeeklyDungeon()
    {
        AvatarPresetList = Expect(0, 1597).Body;
        TestCheck.That(AvatarPresetList.Length >= 2 && AvatarPresetList[1] is >= 1 and <= 6 &&
            AvatarPresetList[0] < AvatarPresetList[1],
            "装扮预设先于每周地下城下发，记录数决定容量、已应用槽在范围内");
        NpcFavor = Expect(0, 746).Body;
        TestCheck.That(NpcFavor.Length >= 1, "好感度快照有u8行数");
        int favorOffset = 1;
        var favorIds = new HashSet<int>();
        for (int row = 0; row < NpcFavor[0]; row++)
        {
            TestCheck.That(favorOffset + 10 <= NpcFavor.Length && BitConverter.ToInt32(NpcFavor, favorOffset) > 0 &&
                BitConverter.ToInt32(NpcFavor, favorOffset + 4) > 0 && favorIds.Add(BitConverter.ToInt32(NpcFavor, favorOffset)),
                "好感度行完整、正数且owner不重复");
            int colors = NpcFavor[favorOffset + 9];
            favorOffset += 10;
            TestCheck.That(colors <= 4 && favorOffset + colors * 2 <= NpcFavor.Length, "NPC颜色列表宽度和数量有界");
            var channels = new HashSet<byte>();
            for (int color = 0; color < colors; color++, favorOffset += 2)
                TestCheck.That(NpcFavor[favorOffset] < 4 && channels.Add(NpcFavor[favorOffset]), "NPC颜色通道不越界、不重复");
        }
        TestCheck.That(favorOffset == NpcFavor.Length, "好感度按完整绝对快照恢复，无尾随字节");
        Expect(0, 720, []);
        WeeklyDungeonConfig = Expect(0, 721).Body;
        TestCheck.That(WeeklyDungeonConfig.Length == 5, "每周地下城721固定回放5个槽位");
        AccountFriendGroups = Expect(0, 1702).Body;
        CharacterFriends = Expect(0, 273).Body;
        uint friends = U32(CharacterFriends, 4);
        for (uint i = 0; i < friends; i++) Expect(0, 274);
        AccountFriends = Expect(0, 1376).Body;
        AccountFriendRequests = Expect(0, 1377).Body;
        if (OwnGuildGrade != 100)
        {
            Expect(0, 70);
            ReadGuildNpcs();
            ReadGuildDungeon();
        }
    }

    public void CheckGuildPassiveBuffs(WirePacket packet)
    {
        var f = fixture ?? throw new InvalidOperationException("Guild passive assertions require a fixture");
        long now = f.UtcNow.ToUnixTimeSeconds();
        var rows = f.Query("""
            SELECT b.value,b.expires_unix FROM guild_benefits b JOIN guild_members m ON m.guild_id=b.guild_id
            WHERE m.character_id=$p0 AND b.type IN (3,5) AND b.value>0 AND (b.expires_unix=0 OR b.expires_unix>$p1)
            ORDER BY b.type
            """, Id, now).Select(row => (Id: Convert.ToUInt32(row[0]), Until: Convert.ToInt64(row[1]))).ToList();
        var guardian = f.Query("""
            SELECT COALESCE(p.level,1) FROM guild_members m LEFT JOIN guild_npc_progress p
              ON p.guild_id=m.guild_id AND p.npc_id=400001060 WHERE m.character_id=$p0
            """, Id);
        TestCheck.That(guardian.Length == 1, "属性通知必须属于当前角色真实会籍");
        uint level = Convert.ToUInt32(guardian[0][0]);
        TestCheck.That(level is >= 1 and <= 5, "守护石初始化必须有合法实际档位");
        rows.Add((1275 + level, 0));
        TestCheck.That(packet is { Kind: 0, Opcode: 487 }, "公会属性通知类型必须匹配");
        byte[] body = packet.Body;
        TestCheck.That(body.Length == 1 + rows.Count * 16 && body[0] == rows.Count,
            "入城完整安装当前公会属性，不能遗留通知或省略永久守护石");
        for (int at = 0; at < rows.Count; at++)
        {
            int offset = 1 + at * 16;
            long seconds = rows[at].Until == 0 ? 0 : Math.Max(0, rows[at].Until - now);
            TestCheck.That(U32(body, offset) == rows[at].Id && U32(body, offset + 4) == 0 &&
                Math.Abs((long)U32(body, offset + 8) - seconds) <= (seconds == 0 ? 0 : 1) &&
                U32(body, offset + 12) == 0, "入城属性逐项匹配真实账本、剩余期限及零保留字段");
        }
    }

    public void ReadGuildDungeon() => CheckGuildDungeon(Expect(0, 744).Body);

    public void CheckGuildDungeon(byte[] body)
    {
        uint[] ids = [8500, 8503, 8504, 8505, 8507, 8508];
        TestCheck.That(body.Length == 17 + ids.Length * 8 && body[0] == 1 && U32(body, 1) == 8500 &&
            U32(body, 5) == 0 && U32(body, 9) == 0 && U32(body, 13) == ids.Length && fixture is not null,
            "744从正式资源根图打开，完整六行计数，不伪造周期或未知头字段");
        var saved = fixture!.Query("""
            SELECT dungeon_id,clear_count FROM guild_dungeon_progress
            WHERE guild_id=(SELECT guild_id FROM guild_members WHERE character_id=$p0)
            """, Id).ToDictionary(row => Convert.ToUInt32(row[0]), row => Convert.ToUInt32(row[1]));
        for (int i = 0; i < ids.Length; i++)
            TestCheck.That(U32(body, 17 + i * 8) == ids[i] && U32(body, 21 + i * 8) == saved.GetValueOrDefault(ids[i]),
                "744每行只投影本公会真实持久通关次数，既无百分比替代也无遗漏");
    }

    public void ReadGuildNpcs()
    {
        GuildNpcs = Expect(0, 1771).Body;
        TestCheck.That(GuildNpcs.Length == 128 &&
            Enumerable.Range(0, 8).Select(index => U32(GuildNpcs, index * 16)).Distinct().Count() == 8,
            "登录及打开面板后恢复八条独立NPC状态，不能依赖客户端额外请求1737");
        // 单条制造通知后紧跟完整缓存；空制造集合只发送1773。
        var packet = Read();
        int count = 0;
        while (packet is { Kind: 0, Opcode: 1772 })
        {
            TestCheck.That(packet.Body.Length == 12 && ++count <= 1, "守护石制造单条通知有界且完整");
            packet = Read();
        }
        TestCheck.That(packet is { Kind: 0, Opcode: 1773, Body.Length: 1080 },
            "NPC初始化包含固定1080字节福利快照");
        byte[] training = Expect(0, 1774).Body;
        TestCheck.That(training.Length == 16 && training.All(value => value <= 3),
            "NPC初始化恢复16格训练解锁与选择状态");
    }

    public byte[] CharacterFriends { get; private set; } = [];
    public byte[] AccountFriends { get; private set; } = [];
    public byte[] AccountFriendGroups { get; private set; } = [];
    public byte[] AccountFriendRequests { get; private set; } = [];

    // 决斗入口频道类型取自 LOGIN 应答第 4 字节；原生登录不取该应答（LoginEndpoint 仍为空数组），按非决斗处理，
    // 保持入城仍走共享房间与 1379。直接按下标读会在 NativeLoginTcpTests 抛 IndexOutOfRangeException。
    bool PvpEntry => LoginEndpoint.Length > 3 && LoginEndpoint[3] is 4 or 5 or 8 or 13 or 24;

    public WirePacket[] Select(ushort slot = 0, bool prepared = false, (byte Kind, ushort Opcode)[]? inventoryNotices = null,
        (ushort Opcode, byte[] Body)[]? settings = null, byte? appearanceFlags = null, bool expertContract = false,
        byte[]? sceneSkills = null, byte[]? combos = null, bool resumeTown = false, bool room = true, bool premiumContracts = false, ushort avatarExpansion = 105,
        (int[] Owned, int Selected)? weaponSkins = null,
        (SkinOwnership[] Owned, int Selected)? damageFonts = null,
        (SkinOwnership[] Owned, int[] Selected)? cutscenes = null, byte[]? expertJobInfo = null, bool? nativeSwitch = null,
        ushort[]? tagEvildom = null, byte[][]? echonSets = null, byte[]? pvpTotalTeam = null, bool premiumPcRoom = false,
        (SkinOwnership[] Owned, int Selected)? partyFrames = null,
        (SkinOwnership[] Owned, int[] Selected)? emoticons = null, SkinStorageState? accountSkins = null, byte[]? ekernCycle = null,
        byte? recoveredPvpGrade = null, byte recoveredPowerTitle = 0, SkinStorageKey[]? recentSkins = null, int[]? knightShieldDeck = null,
        (int BuffId, int Remaining)? bossBuff = null, byte[]? strongwayHistory = null, byte[]? strongwayAccountRewards = null)
    {
        // 三类决斗入口直接绑定非房间地区10/0；仍逐包断言1379不应出现，不跳过未知包。
        if (PvpEntry) room = false;
        PcRoomAccelerationState = [];
        // 团本玩法夹具按主动换频道直发4；fresh拒绝专项自行发8/4，显式false也可请求普通选角链。
        bool directSwitch = nativeSwitch ?? (LoginEndpoint.Length > 3 && LoginEndpoint[3] is 23 or 32 or 45 or 50);
        if (!resumeTown && !requestedRoster && !directSwitch)
            Send(8, [255, 255, 2]);
        long selectionStarted = UnixTime;
        var accountMaterialInitialization = CaptureAccountMaterialInitialization(slot);
        Send(4, Build(w => w.Write(slot)));
        var expected = prepared ? PreparedEntry : Entry;
        if (resumeTown)
            expected = [.. expected.Where(p => p != ((byte)0, (ushort)24) && p != ((byte)0, (ushort)1379) && p != ((byte)0, (ushort)124)),
                .. (room ? new (byte Kind, ushort Opcode)[] { (0, 1379) } : []), (0, 124), (0, 24)];
        else if (!room)
            expected = [.. expected.Where(p => p != ((byte)0, (ushort)1379))];
        int afterCargo = 9;
        // 保留普通入城断言的返回索引，特殊容器单独暴露；线上顺序仍逐包严格验证。
        var ack = Expect(expected[0].Kind, expected[0].Opcode);
        Id = U16(ack.Body, 9);
        var permission = Expect(0, 5);
        DungeonPermission = permission.Body;
        TestCheck.That(permission.Body.Length >= 2 &&
            permission.Body.Length == 2 + BinaryPrimitives.ReadUInt16LittleEndian(permission.Body) * 5 &&
            BinaryPrimitives.ReadUInt16LittleEndian(permission.Body) > 0,
            "选角 ACK 后立即下发 NOTI5 世界地图悬浮开通表");
        for (int index = 0, count = BinaryPrimitives.ReadUInt16LittleEndian(permission.Body); index < count; index++)
        {
            uint dungeonId = BinaryPrimitives.ReadUInt32LittleEndian(permission.Body.AsSpan(2 + index * 5));
            TestCheck.That(dungeonId != 0 && permission.Body[2 + index * 5 + 4] == 1,
                "开通表每项为非零地下城编号且 state=1");
        }
        var prefix = new[] { ack, Expect(expected[1].Kind, expected[1].Opcode) };
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
        byte[]? characterOptions = (settings ?? []).FirstOrDefault(option => option.Opcode == 390).Body;
        if (characterOptions is null)
        {
            // 独立编码无角色存档的512B初始化，不调用生产规则或Builder生成预期。
            byte[] options = Enumerable.Repeat((byte)255, 512).ToArray();
            bool unsetMain = Enumerable.Range(0, 102).All(index => U16(InitialGameOptions, 4 + index * 2) == 0x7fff);
            BinaryPrimitives.WriteUInt16LittleEndian(options.AsSpan(86), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(options.AsSpan(92), unsetMain ? (ushort)100 : U16(InitialGameOptions, 140));
            BinaryPrimitives.WriteUInt16LittleEndian(options.AsSpan(96), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(options.AsSpan(98), unsetMain ? (ushort)1 : U16(InitialGameOptions, 30));
            characterOptions = Build(writer => { writer.Write(512u); writer.Write(options); });
        }
        Expect(0, 390, characterOptions);
        var middle = new List<WirePacket>();
        RestoredConsumableStates.Clear();
        SkillInfo = [];
        SceneSkillInfo = [];
        CompletedQuestHistory = [];
        KnightShieldDeckBody = [];
        CollectBoxes = [];
        DungeonEnterCounts = [];
        for (int index = 2; index < afterCargo; index++)
        {
            if (index == 5) foreach (var set in echonSets ?? []) Expect(0, 1282, set);
            var packet = Read();
            if (index == 5)
            {
                TestCheck.That(packet.Kind == 0 && packet.Opcode == 351 && packet.Body.Length == 34 && packet.Body[0] == 11 &&
                    Enumerable.Range(0, 11).All(type => packet.Body[1 + type * 3] == type && U16(packet.Body, 2 + type * 3) <= 1000),
                    "制作熟练度在Main之后恢复十一类别，不能借用角色满级熟练度");
                ItemMakingSkills = packet.Body;
                packet = Read();
                while (packet is { Kind: 0, Opcode: 172 or 174 })
                {
                    TestCheck.That(RestoredConsumableStates.Count < 2 && (RestoredConsumableStates.Count == 0 ||
                        RestoredConsumableStates[^1].Opcode < packet.Opcode), "持续状态仅172再174，各一次，不跳过其他通知");
                    ValidateConsumableStates(packet, U16(prefix[0].Body,9), selectionStarted);
                    RestoredConsumableStates.Add(packet);
                    packet = Read();
                }
                // Main全量之后严格接收完整541分片，保持Entry返回索引和后续988顺序。
                var daily = new List<byte[]>();
                int lastId = 0;
                while (packet is { Kind: 0, Opcode: 541 })
                {
                    var rows = Inventory.DailyBoosterProjectionTests.Decode(packet.Body);
                    TestCheck.That(daily.Count == 0 || rows.Count != 0 && rows.Keys.Min() > lastId,
                        "541分片模板有序、不重复，空视图只有一包");
                    foreach (var row in rows) DailyBoosterUsed[row.Key] = row.Value;
                    if (rows.Count != 0) lastId = rows.Keys.Max();
                    daily.Add(packet.Body);
                    packet = Read();
                }
                TestCheck.That(daily.Count > 0, "Main全量之后必须初始化每日已用视图");
                InitialDailyBoosterPackets = daily.ToArray();
            }
            if (index == 3 && packet.Kind == 0 && packet.Opcode == 19)
            {
                SkillInfo = packet.Body;
                packet = Read();
            }
            if (index == afterCargo - 2 && expertContract)
            {
                TestCheck.That(packet.Kind == 0 && packet.Opcode == 66, "达人契约仍在技能树与完整穿戴之后初始化");
                ExpertContract = packet.Body;
                packet = Read();
            }
            TestCheck.That(packet.Kind == expected[index].Kind && packet.Opcode == expected[index].Opcode,
                $"entry packet order at {index}");
            if (packet is { Kind: 0, Opcode: 356 })
            {
                // Actor头部为5B+116B状态+u16角色号+u32名字字节数，名字后才是职业。
                var actor = middle.Single(row => row is { Kind: 0, Opcode: 2 } && row.Body[0] == 0).Body;
                int jobOffset = checked(127 + (int)U32(actor, 123));
                TestCheck.That(jobOffset < actor.Length, "盾牌初始化的职业必须来自完整Actor");
                byte job = actor[jobOffset];
                if (fixture is not null)
                    TestCheck.That(job == Convert.ToByte(fixture.Query("SELECT job FROM characters WHERE id=$p0", Id).Single()[0]),
                        "Actor职业与选中角色真实存档一致");
                if (job == 12)
                {
                    int[] deck = knightShieldDeck ?? (fixture is not null ? System.Text.Json.JsonSerializer.Deserialize<int[]>(
                        (string)fixture.Query("SELECT knight_shield_deck FROM characters WHERE id=$p0", Id).Single()[0]!)! : new int[5]);
                    KnightShieldDeckBody = Expect(0, 580, Build(writer => { foreach (int item in deck) writer.Write(item); })).Body;
                }
            }
            if (packet is { Kind: 0, Opcode: 2 } && packet.Body is [1, ..])
            {
                PlayerBody = packet.Body;
                ReadPrivilegeServiceInitialization();
                // 网吧身份的入城在本人Player后紧接487特权buff，随后才是两祭坛次数初始化。
                if (premiumPcRoom)
                {
                    byte[] buff = Expect(0, 487).Body;
                    TestCheck.That(buff.Length == 17 && buff[0] == 1 && U32(buff, 1) == 1249 && U32(buff, 5) == 0,
                        "网吧身份入城在本人Player后补发487特权buff，永久实例三项动态整数为0");
                }
                if (bossBuff is { } boss)
                    Expect(0, 487, Build(writer =>
                    { writer.Write((byte)1); writer.Write(boss.BuffId); writer.Write(boss.Remaining); writer.Write(0); writer.Write(0); }));
                if (ekernCycle is not null) Expect(0,656,ekernCycle);
                foreach (int dungeonId in new[] { 11006, 11007 })
                {
                    var count = Expect(0, 550).Body;
                    TestCheck.That(count.Length == 6 && U32(count, 0) == dungeonId && count[4] <= 3 && count[5] == 0,
                        "本人Player模型后逐包初始化两祭坛真实日剩余，周字段为零");
                    DungeonEnterCounts.Add(dungeonId, (count[4], count[5]));
                }
                ExpectGriefTowerEnterCounts();
                ExpectDespairTowerEnterCounts();
                // 无夹具的连接（原生登录等直接 new TcpPeer 用例）只能按线上包读职位；
                // 有夹具时再与公会成员表逐值对照，不能因为缺夹具就跳过 127/70 的消费。
                var memberInfo = Expect(0, 127).Body;
                TestCheck.That(memberInfo.Length == 1, "127 本人职位单字节");
                byte grade = memberInfo[0];
                if (fixture is not null)
                {
                    ushort characterId = U16(prefix[0].Body, 9);
                    var memberships = fixture.Query("""
                        SELECT CASE WHEN g.master_character_id=m.character_id THEN 1
                            WHEN m.grade BETWEEN 2 AND 5 THEN m.grade ELSE 4 END
                        FROM guild_members m JOIN guilds g ON g.guild_id=m.guild_id WHERE m.character_id=$p0
                        """, characterId);
                    byte expectedGrade = memberships.Length == 0 ? (byte)100 : Convert.ToByte(memberships[0][0]);
                    TestCheck.That(grade == expectedGrade, "127 职位与公会成员表一致（无会籍=100，会长=1，2-5 原值，其余=4）");
                }
                ExpectDazzlementBindings(characterId: U16(prefix[0].Body, 9));
            }
            middle.Add(packet);
        }
        var avatars = Expect(0, 13);
        CloneAvatarSources = Expect(0, 1449).Body;
        TestCheck.That(CloneAvatarSources.Length == 34 && CloneAvatarSources[0] == 11 &&
            Enumerable.Range(0, 11).All(index => CloneAvatarSources[1 + index * 3] == index),
            "装扮包裹初始化后完整恢复十一槽来源表，清除上一角色的关联状态");
        var pets = Expect(0, 13);
        var medals = Expect(0, 13);
        var talismans = Expect(0, 13);
        var accountCommon = Expect(0, 13);
        TestCheck.That(avatars.Body[0] == 1 && U16(avatars.Body, 1) == avatarExpansion && pets.Body[0] == 7, "special container initialization and avatar capacity");
        TestCheck.That(medals.Body[0] == 38, "medal container initialization");
        TestCheck.That(talismans.Body[0] == 46 && talismans.Body.Length == 3 + U16(talismans.Body, 1) * 123,
            "护石符文容器按普通123B物品行初始化");
        TestCheck.That(accountCommon.Body.Length >= 3 && accountCommon.Body[0] == 45 &&
            U16(accountCommon.Body, 1) <= 50 && accountCommon.Body.Length == 3 + U16(accountCommon.Body, 1) * 123,
            "账号公共45在护石后严格初始化，空容器也发送3B头，最多50个完整123B实例");
        SpecialContainers = [creatures, avatars, pets, medals, talismans, accountCommon];
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
            if (notice == ((byte)0, (ushort)22) && LoginEndpoint.Length > 3 && LoginEndpoint[3] == 55)
            {
                StrongwayHistory = Expect(0, 1491, strongwayHistory ?? new byte[16]).Body;
                TestCheck.That(StrongwayHistory.Length == 16, "强者之路选角在位置包前严格恢复四个u32历史字段");
                StrongwayAccountRewards = Expect(0, 1753, strongwayAccountRewards ?? new byte[24]).Body;
                TestCheck.That(StrongwayAccountRewards.Length == 24, "强者之路选角恢复六个u32账号奖励字段");
            }
            if (notice == ((byte)0, (ushort)427)) ReadAccountMaterialInitialization(accountMaterialInitialization);
            if (notice == ((byte)0, (ushort)124)) ReadSeriaInitialization();
            // 普通入城在124前恢复；换频道等最终24绑定后恢复。
            if (!resumeTown && notice == ((byte)0, (ushort)124)) ReadSceneInitialization();
            if (notice == ((byte)0, (ushort)3))
            {
                EpicWishInformation = Expect(0, 1292).Body;
                TestCheck.That(EpicWishInformation.Length > 0 && EpicWishInformation[0] <= 5 &&
                    EpicWishInformation.Length == 1 + EpicWishInformation[0] * 4,
                    "史诗祈愿在图鉴后初始化，保留至多五个完整u32槽");
                int[] wishes = Enumerable.Range(0, EpicWishInformation[0])
                    .Select(index => checked((int)U32(EpicWishInformation, 1 + index * 4))).ToArray();
                TestCheck.That(wishes.Where(id => id != 0).Distinct().Count() == wishes.Count(id => id != 0),
                    "祈愿初始化允许多个空槽但不重复非空装备");
                if (fixture is not null)
                {
                    var savedWishes = fixture.Query("SELECT item_ids FROM character_epic_wishes WHERE character_id=$p0", U16(prefix[0].Body, 9));
                    int[] expectedWishes = savedWishes.Length == 0 ? [] :
                        System.Text.Json.JsonSerializer.Deserialize<int[]>((string)savedWishes[0][0]!)!;
                    TestCheck.That(wishes.SequenceEqual(expectedWishes), "祈愿初始化逐槽匹配本人存档，不沿用其他角色清单");
                }
                var packet = Read();
                TestCheck.That(packet.Kind==0 && packet.Opcode==1358 && packet.Body.Length==32 && U32(packet.Body,0)==U16(prefix[0].Body,9) &&
                    packet.Body[16]==0 && packet.Body[18]==0 && packet.Body[19]==0,
                    "史诗制作32字节初始化保留独立周计数及默认保留字节");
                EpicProductionState=packet.Body;
                packet=Read();
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
                // DungeonIncountInfo 全部限次副本：强袭每日信息后逐个下发 NOTI 550（u32 副本 + u8 日剩余 + u8 周剩余）。
                var enterCounts = new List<byte[]>();
                while (packet.Kind == 0 && packet.Opcode == 550)
                {
                    TestCheck.That(packet.Body.Length == 6, "NOTI 550 固定 6 字节");
                    enterCounts.Add(packet.Body);
                    packet = Read();
                }
                if (packet is { Kind: 0, Opcode: 815 })
                {
                    ReadDimensionEntrance(packet);
                    packet = Read();
                }
                if (packet is { Kind: 0, Opcode: 772 })
                {
                    ReadAncientGate(packet);
                    packet = Read();
                }
                TestCheck.That(enterCounts.Count is 0 or 16 && enterCounts.All(body => U32(body, 0) is not (11006 or 11007)) && enterCounts.Select(body => U32(body, 0)).Distinct().Count() == enterCounts.Count,
                    "入城下发16个通用限次副本各一条550，不覆盖两祭坛专用次数: " + enterCounts.Count);
                EnterCounts = enterCounts.ToArray();
                while (packet.Kind == 0 && packet.Opcode == 97)
                {
                    mails.Add(packet.Body);
                    packet = Read();
                }
                    packet = ReadMarriageInitialization(packet, U16(prefix[0].Body, 9));
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
        TestCheck.That(U32(packets[2].Body, 115) == (weaponSkins?.Selected ?? 0),
            "首次入城及重连Actor恢复独立武器幻化，不以Player的仓库选择组代替场景字段");
        ActorAdventureName = DecodeActorAdventureName(packets[2].Body);
        string fullAdventureName = names.GetString(AdventureName, 4, AdventureName.Length - 5);
        TestCheck.That(fullAdventureName.StartsWith(ActorAdventureName, StringComparison.Ordinal) &&
            (NameWidth(fullAdventureName) <= 16 ? ActorAdventureName == fullAdventureName : NameWidth(ActorAdventureName) <= 16),
            "本人Actor与1375回放同一账号名称，窄字段不截断双字节字符");
        if (appearanceFlags.HasValue)
            TestCheck.That(packets[2].Body[^23] == appearanceFlags.Value, "Actor外观flags与设置后357一致");
        TestCheck.That(packets[7].Body[0] == 2 && packets[8].Body[0] == 12,
            "personal and account cargo initialize before locks");
        TestCheck.That(packets[4].Body[0] == 0, "main inventory initialization precedes cargo");
        ushort expansionSlots = U16(packets[4].Body, 1);
        TestCheck.That(expansionSlots % 8 == 0 && (expansionSlots >> 3) <= 3,
            "client expansion reader stays within its three-stage table");
        TestCheck.That(packets.Single(p => p is { Kind: 0, Opcode: 124 }).Body.Length == 0, "entry complete body empty");
        if (expertJobInfo is not null)
        {
            var actual = Expect(0, 205).Body;
            TestCheck.Bytes(actual, expertJobInfo, $"NOTI205 expected={Convert.ToHexString(expertJobInfo)} actual={Convert.ToHexString(actual)}");
        }
        if (recoveredPvpGrade is { } recoveredGrade)
            TownPresenceTcpTests.PvpGrade(Expect(0, 2).Body, recoveredGrade, recoveredPowerTitle);
        if (PvpEntry)
        {
            PvpMissions = Expect(0, 323).Body;
            if (fixture is not null) PvpMissionTcpChecks.Progress(fixture, Id, PvpMissions);
            else PvpMissionTcpChecks.Parse(PvpMissions);
            PvpMissionTcpChecks.Daily(this, fixture);
        }
        if (pvpTotalTeam is not null)
        {
            Expect(0, 804, pvpTotalTeam);
            var actors = Expect(0, 803).Body;
            PvpTotalActors = actors;
            TestCheck.That(actors[0] == 1 && U16(actors, 1) == Id && actors[3] == 3,
                "选角回放总力编队后安装本人的三名完整演员，满足开始前客户端门槛");
        }
        TagEvildomInfo = (tagEvildom ?? []).Select(opcode => Expect(0, opcode)).ToArray();
        ReadWeeklyDungeon();
        if (premiumPcRoom)
        {
            TestCheck.That(PremiumServices.TryGetValue(128, out long seconds) && seconds > 0,
                "首次、重选及重连均已接收本次1760的有效128，随后490才能按实际选择核验网吧菜单资格");
            var login = Expect(0, 1517).Body;
            var rental = Expect(0, 1639).Body;
            TestCheck.That(login is [0, 0, 0, 0, 0 or 1] && rental is [0 or 1],
                "网吧请求尾部按1517登录、1639租赁状态恢复完整领奖面板");
            PcRoomAccelerationState = Expect(0, 490).Body;
            TestCheck.That(PcRoomAccelerationState is [0, 0, 0 or 1],
                "有效128初始化后完整恢复490的WORD保留0及实际选择BYTE，未勾选也必须收到state0");
        }
        // 新增的空历史356已逐包校验，并由CompletedQuestHistory暴露；
        // 普通角色的返回数组保留既有仓库等索引，避免无关业务用例随初始化包插入而迁移。
        return prepared ? packets : [.. packets.Where(packet => packet is not { Kind: 0, Opcode: 356 })];

        void ReadSeriaInitialization()
        {
            InitialAccountMaterialLists = [];
            var events = Expect(0, 108);
            TestCheck.That(events.Protected, "入城的赛丽亚事件配置使用保护压缩帧");
            // 已有不渝之咏余额时，108 后会补 list44/45，商店才能读到持有量；全零不插包。
            while (true)
            {
                var material = Read();
                if (material is { Kind: 0, Opcode: 13, Body: [44 or 45, ..] })
                {
                    TestCheck.That(material.Body.Length >= 3 && material.Body.Length == 3 + U16(material.Body, 1) * 123,
                        "选角攻坚材料投影按 list44/45 的完整行到达");
                    InitialAccountMaterialLists = [.. InitialAccountMaterialLists, material];
                    continue;
                }
                pushedBack = material;
                break;
            }
            BeforeMileageInitialization?.Invoke(this);
            if (ExpectedOpenEvents.Contains(ServerEventService.MileageShopId))
                MileageLimits = Expect(0, 706).Body;
            // 312不属于108目录；按隔离库的原始开关/时段独立计算，不能用收到的action自证。
            var npcFixture = fixture ?? throw new InvalidOperationException("活动NPC初始化缺少隔离夹具");
            var npcSchedule = npcFixture.Query("SELECT starts_at,ends_at,override_active FROM server_events WHERE event_id=312");
            bool npcActive = npcSchedule is not [var npcRow] || (npcRow[2] is not DBNull
                ? Convert.ToInt64(npcRow[2]) != 0
                : npcRow[0] is DBNull && npcRow[1] is DBNull ||
                    npcRow[0] is not DBNull && npcRow[1] is not DBNull &&
                    Convert.ToInt64(npcRow[0]) <= UnixTime && UnixTime < Convert.ToInt64(npcRow[1]));
            var npc = Expect(0, 639);
            TestCheck.That(!npc.Protected, "639使用原生普通帧并位于108及可选706之后、36之前");
            Strongway2019NpcTests.AssertDisplay(npc.Body, npcActive);
            StrongwayNpcDisplay = npc.Body;
            var fatigue = Expect(0, 36);
            TestCheck.That(fatigue.Body.Length == 10, "EVENT_INFO建立城镇HUD后同步本版五个u16疲劳字段");
            using (var compressed = new MemoryStream(events.Body, writable: false))
            using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
            {
                byte[] bounded = new byte[32768];
                int length = 0;
                while (length < bounded.Length)
                {
                    int read = zlib.Read(bounded, length, bounded.Length - length);
                    if (read == 0) break;
                    length += read;
                }
                TestCheck.That(zlib.ReadByte() == -1, "事件108解压不得超32768B");
                EventInfoPayload = bounded[..length];
                if (ValidateNativePromotionEvents is { } validate)
                    validate(EventInfoPayload);
                else if (U16(EventInfoPayload, 0) != 3 + ExpectedOpenEvents.Length)
                    CashShopTcpTests.AssertPromotionNativeEvents(EventInfoPayload, null, null, ExpectedOpenEvents,
                        activeEvents: ExpectedActiveEvents, battlePassEnd: ExpectedBattlePassEnd, eventValues: ExpectedEventValues);
                else
                {
                    int expectedLength = 98 + 15 * ExpectedOpenEvents.Length + 50 * ExpectedActiveEvents.Length;
                    TestCheck.That(length == expectedLength + (ExpectedBattlePassEnd.HasValue ? 50 : 0),
                        ExpectedBattlePassEnd.HasValue && length == expectedLength
                            ? "EVENT2520_VALUE_ROW_MISSING" : "无促销页事件正文长度完整");
                    TestCheck.Bytes(EventInfoPayload, Build(writer =>
                    {
                        writer.Write((ushort)(3 + ExpectedOpenEvents.Length));
                        foreach (ushort id in (ushort[])[2037, 2378, 2395, .. ExpectedOpenEvents])
                        {
                            writer.Write(id);
                            writer.Write(1u);
                            writer.Write(0u);
                            writer.Write(0u);
                            writer.Write((byte)0);
                        }
                        writer.Write(checked((byte)(1 + ExpectedActiveEvents.Length + (ExpectedBattlePassEnd.HasValue ? 1 : 0))));
                        writer.Write((ushort)2037);
                        writer.Write(8u);
                        for (int index = 1; index < 12; index++) writer.Write(0u);
                        foreach (ushort id in ExpectedActiveEvents)
                        {
                            writer.Write(id);
                            for (int index = 0; index < 12; index++)
                                writer.Write(ExpectedEventValues.TryGetValue(id, out var values) ? values[index] : 0u);
                        }
                        if (ExpectedBattlePassEnd is uint end)
                        {
                            writer.Write((ushort)2520);
                            writer.Write(end);
                            for (int index = 1; index < 12; index++) writer.Write(0u);
                        }
                    }), "独立核对赛丽亚2037、红包2378、乔纳森2395、开放团本及完整尾部，不能跳过门禁或覆盖原事件");
                }
            }
            var gauge = Expect(0, 412);
            SeriaLuckInfo = gauge.Body;
            TestCheck.That(!gauge.Protected && gauge.Body.Length == 2 && gauge.Body[0] == gauge.Body[1] &&
                gauge.Body[0] <= 40 && gauge.Body[0] % 5 == 0, "幸运值初始通知使用普通CRC/序号，两端阶段一致且不超8");
            // 初始化使用真实持久次数；具体日周用量由各团本生命周期回归断言。
            RaidWeeklyClearInfo = Expect(0, 1446).Body;
            RaidPanelTests.ReadWeeklyClears(RaidWeeklyClearInfo);
            LukeChannelCardsInfo = [];
            var postInfo = Read();
            if (postInfo is { Kind: 0, Opcode: 1184 })
            {
                TestCheck.That(channelNumber == 20, "只有卢克频道在入城序列发送塔罗预览");
                var body = postInfo.Body;
                TestCheck.That(body.Length >= 3 && body[0] == 0 && body[1] == 3 && body[^1] == 1,
                    "频道预览为一阶段完整三图且自动展示一次");
                int offset = 2;
                for (int i = 0; i < 3; i++)
                {
                    TestCheck.That(offset + 4 < body.Length && U16(body, offset) == 5054 + i &&
                        body[offset + 2] < (i == 1 ? 4 : 2) && body[offset + 3] > 0, "频道塔罗保留三图合法布局和真实怪物");
                    int count = body[offset + 3]; offset += 4;
                    TestCheck.That(offset + count * 5 < body.Length, "频道塔罗怪物行不截断");
                    for (int row = 0; row < count; row++, offset += 5)
                        TestCheck.That(U32(body, offset) > 0 && body[offset + 4] == 0, "塔罗模板非空且保留字节为零");
                }
                TestCheck.That(offset == body.Length - 1, "频道塔罗包无多余正文");
                LukeChannelCardsInfo = body;
                postInfo = Read();
            }
            else TestCheck.That(channelNumber != 20 || fixture?.ProductionLukeCards != true,
                "生产卢克频道必须在入城时展示日牌组");
            TestCheck.That(postInfo is { Kind: 0, Opcode: 1232 }, "塔罗预览后仍须完成POST_CHARAC_INFO");
            TestCheck.Bytes(postInfo.Body, [0], "POST_CHARAC_INFO保持单字节合同");
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
            var guideFixture = fixture ?? throw new InvalidOperationException("技能引导回放必须核对实际选中角色");
            var guideCharacter = guideFixture.Query("SELECT job,level FROM characters WHERE id=$p0", Id).Single();
            var guideIds = guideFixture.SkillGuideIds(Convert.ToByte(guideCharacter[0]));
            byte guideCount = Convert.ToByte(guideCharacter[1]) >= 15 ? (byte)5 : (byte)0;
            SkillGuideInfo = guideIds.Chunk(255).Select(ids => Expect(0, 1497, Build(writer =>
            {
                writer.Write(checked((byte)ids.Length));
                foreach (ushort id in ids) { writer.Write(id); writer.Write(guideCount); }
            })).Body).ToArray();
            var expectedChatEmoticons = fixture?.ExpectedChatEmoticons(Id);
            if (expectedChatEmoticons is not null)
            {
                RecentEmoticonInfo = Expect(0, 1384, fixture!.ExpectedRecentEmoticons(Id)).Body;
                ChatEmoticonInfo = Expect(0, 411, expectedChatEmoticons).Body;
            }
            var expectedSettings = (settings ?? []).ToList();
            if (!expectedSettings.Any(option => option.Opcode == 390))
            {
                int next = expectedSettings.FindIndex(option => option.Opcode != 173);
                expectedSettings.Insert(next >= 0 ? next : expectedSettings.Count, (390, characterOptions));
            }
            ClientSettings = expectedSettings.Select(option => Expect(0, option.Opcode, option.Body)).ToArray();
            if (appearanceFlags.HasValue)
                Expect(0, 357, Build(w =>
                {
                    w.Write(U16(prefix[0].Body, 9));
                    w.Write(appearanceFlags.Value);
                }));
            AvailableQuests = Expect(0, 21).Body;
            CompletedQuestHistory = Expect(0, 356).Body;
            TestCheck.Bytes(CompletedQuestHistory, middle.Single(packet => packet.Opcode == 356).Body,
                "入城21后恢复同一角色完成历史及月轮山显示标记，空历史也不省略");
            Expect(0, 703);
            var skins = weaponSkins ?? (Owned: Array.Empty<int>(), Selected: 0);
            Expect(0, 1557, Build(w =>
            {
                w.Write((byte)4); w.Write(checked((ushort)skins.Owned.Length));
                foreach (int id in skins.Owned) w.Write(id);
            }));
            Expect(0, 1558, Build(w => { w.Write((byte)4); w.Write(skins.Selected); }));
            var accountFonts = accountSkins?.DamageFonts.ToArray() ?? [];
            var accountIllustrations = accountSkins?.Cutscenes.ToArray() ?? [];
            var accountFrames = accountSkins?.PartyFrames.ToArray() ?? [];
            var accountEmoticons = accountSkins?.Emoticons.ToArray() ?? [];
            var fonts = damageFonts ?? (Owned: Array.Empty<SkinOwnership>(), Selected: 1);
            Expect(0, 1557, Build(w =>
            {
                w.Write((byte)2); w.Write(checked((ushort)(fonts.Owned.Length + 1)));
                w.Write(1); w.Write(0);
                foreach (var font in fonts.Owned) { w.Write(font.SkinId); w.Write(font.ExpiresAt); }
                AccountList(w, accountFonts);
            }));
            Expect(0, 1558, Build(w => { w.Write((byte)2); w.Write(fonts.Selected); }));
            var illustrations = cutscenes ?? (Owned: Array.Empty<SkinOwnership>(), Selected: new[] { 30000 });
            Expect(0,1557,Build(w =>
            {
                w.Write((byte)1); w.Write(checked((ushort)(illustrations.Owned.Length+1))); w.Write(30000); w.Write(0);
                foreach(var row in illustrations.Owned) { w.Write(row.SkinId); w.Write(row.ExpiresAt); }
                AccountList(w, accountIllustrations);
            }));
            Expect(0,1558,Build(w =>
            {
                w.Write((byte)1); w.Write(checked((ushort)illustrations.Selected.Length));
                foreach(int id in illustrations.Selected) w.Write(id);
            }));
            // 组队头像框恒有原生默认20000，服务端每次选角都会下发该类别。
            var frames = partyFrames ?? (Owned: Array.Empty<SkinOwnership>(), Selected: 20000);
            Expect(0,1557,Build(w =>
            {
                w.Write((byte)0); w.Write(checked((ushort)(frames.Owned.Length+1))); w.Write(20000); w.Write(0);
                foreach(var row in frames.Owned) { w.Write(row.SkinId); w.Write(row.ExpiresAt); }
                AccountList(w, accountFrames);
            }));
            Expect(0,1558,Build(w => { w.Write((byte)0); w.Write(frames.Selected); }));
            // 即时表情没有原生默认皮肤，角色与账号都没有条目时服务端不发该类别。
            if (emoticons.HasValue || accountEmoticons.Length > 0)
            {
                var emote = emoticons ?? (Owned: Array.Empty<SkinOwnership>(), Selected: Array.Empty<int>());
                Expect(0,1557,Build(w =>
                {
                    w.Write((byte)3); w.Write(checked((ushort)emote.Owned.Length));
                    foreach(var row in emote.Owned) { w.Write(row.SkinId); w.Write(row.ExpiresAt); }
                    AccountList(w, accountEmoticons);
                }));
                Expect(0,1558,Build(w =>
                {
                    w.Write((byte)3);
                    for(int at=0;at<4;at++) w.Write(at < emote.Selected.Length ? emote.Selected[at] : 0);
                }));
            }

            byte[] recent = Expect(0, 1559).Body;
            TestCheck.That(recent.Length >= 1 && recent.Length == 1 + recent[0] * 5,
                "最近获得初始化保持u8数量和完整5B记录，空角色也清除旧列表");
            RecentSkins = Enumerable.Range(0, recent[0]).Select(at => new SkinStorageKey(recent[1 + at * 5],
                BinaryPrimitives.ReadInt32LittleEndian(recent.AsSpan(2 + at * 5)))).ToArray();
            TestCheck.That(RecentSkins.All(key => key.Type <= 4 && key.SkinId > 0) &&
                RecentSkins.Distinct().Count() == RecentSkins.Length, "最近获得类别有效且没有重复记录");
            if (recentSkins is not null)
                TestCheck.That(RecentSkins.SequenceEqual(recentSkins), "最近获得初始化逐项匹配指定归属、排序和完整内容");

            void AccountList(BinaryWriter writer, SkinOwnership[] rows)
            {
                writer.Write(checked((ushort)rows.Length));
                foreach (var row in rows) { writer.Write(row.SkinId); writer.Write(row.ExpiresAt); }
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
    public void Travel(byte town, byte area, ushort x, ushort y, ushort previousTown, ushort previousArea, bool bottom = false, uint? returnTown = null, byte kind = 0, uint npc = 0, Action<TcpPeer>? beforeLocation = null)
    {
        Send(bottom ? (ushort)1426 : (ushort)36, bottom ? [] : Area(town, area, x, y, previousTown, previousArea, kind, npc));
        SendBarrier();
        beforeLocation?.Invoke(this);
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
