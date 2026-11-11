using System.Buffers.Binary;
using System.Net;
using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Chat;
using DfoServer.Game.Session;
using DfoServer.Game.Party;
using DfoServer.Development;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer;

/// <summary>
/// 聊天 Phase 1 的单元覆盖（不启动网络、不需要 PVF）：
/// 入站 0x0011 解析口径、CP936 200 字节截断、每连接节流、显示 type 映射（普通/队伍/悄悄话=3、公会/攻坚队=6）、
/// 0x0C 正文（含文本与 255 字节截断）、0x76 喇叭整帧/三条串非空/29-511-16 字节截断、0x06AD 公告整帧与 24/600 字节截断，
/// 以及 ChatService 的四类投递规则（同场景/同公会/只投目标/全服与同频道）、
/// 物品链接 0x01A8 的 179 字节实测包解析/原样转发/非法长度容错，
/// 和 GM <c>notice</c> 的 all/定向投递、GM <c>chat-inject</c>/<c>inject-raw</c> 的诊断投递。
/// 期望值来自实测样本与反汇编结论文档（<c>Docs/协议/聊天.md</c>），不调用服务端 Builder 反算解析结果。
/// </summary>
internal static class ChatServiceTests
{
    // 实测样本：四类客户端聊天发送（普通/公会/服务器喇叭/频道喇叭），来自真实收包。
    static readonly (string Hex, byte Type, ushort Layer, uint Arg, uint TextLength, string Text)[] Samples =
    [
        ("03040000000000020000002E2E", 3, 4, 0, 2, ".."),
        ("060400000000000A000000B2E2CAD4313233343536", 6, 4, 0, 10, "测试123456"),
        ("0B0000430000000B000000B7FECEF1C6F7C0AEB0C831", 11, 0, 67, 11, "服务器喇叭1"),
        ("0D00004400000009000000C6B5B5C0C0AEB0C831", 13, 0, 68, 9, "频道喇叭1")
    ];

    sealed class Store : IChatDirectoryStore
    {
        public readonly Dictionary<ushort, string> Accounts = [];
        public readonly Dictionary<long, string[]> Guilds = [];
        public string? AccountOfCharacter(ushort characterId) => Accounts.GetValueOrDefault(characterId);
        public string[] GuildMemberAccounts(long guildId) => Guilds.GetValueOrDefault(guildId) ?? [];
    }

    /// <summary>假投递：保存调用方给出的 filter/notice，按候选快照逐个求值，用来验证"投给谁"。</summary>
    sealed class Delivery(params ChatRecipient[] candidates) : IChatDelivery
    {
        public readonly ChatRecipient[] Candidates = candidates;
        public Func<ChatRecipient, bool>? Filter { get; private set; }
        public ChatNotice? Notice { get; private set; }
        public int Deliveries { get; private set; }

        public int DeliverAll(Func<ChatRecipient, bool> filter, ChatNotice notice)
        {
            Filter = filter;
            Notice = notice;
            Deliveries++;
            return Candidates.Count(filter);
        }

        public bool DeliverTo(string account, Func<ChatRecipient, bool> filter, ChatNotice notice)
        {
            Filter = filter;
            Notice = notice;
            Deliveries++;
            ChatRecipient? target = Candidates.FirstOrDefault(candidate => candidate.Account == account && filter(candidate));
            return target is not null;
        }
    }

    static ChatScene Scene(byte channel, byte area = 1, string map = "town.map", string? room = null, Guid instance = default) =>
        new(new ChannelIdentity(2, channel), 1, area, map, room, instance);

    static ChatRecipient Recipient(string account, ushort characterId, long? guildId, ChatScene scene) =>
        new(account, characterId, guildId, scene);

    public static void Run()
    {
        int before = Count;
        Parsing();
        Truncation();
        RunRateRules();
        ChannelMapping();
        OfficialMegaphoneRules();
        Wire();
        MegaphoneWire();
        ItemLinkWire();
        ExtendedItemLinks();
        Routing();
        ProbeBodies();
        NoticeWire();
        ClientPromptTests.Run();
        DeliveryPath().GetAwaiter().GetResult();
        ItemHyperlink().GetAwaiter().GetResult();
        ChatInjectCommand();
        InjectRawCommand();
        Console.WriteLine($"PASS {Count - before} 聊天解析、截断、节流、频道映射、正文、物品链接、喇叭、公告、投递规则、诊断正文与 GM 注入断言");
    }

    static void ExtendedItemLinks()
    {
        byte[] text = ChatText.Encoding.GetBytes("\u007f\u001e M\u001f \u007f[装备]\u007f \u007f[药剂]\u007f");
        byte[][] records = [Enumerable.Range(0,148).Select(i=>(byte)(19+i*37)).ToArray(),
            Enumerable.Range(0,148).Select(i=>(byte)(83+i*17)).ToArray()];
        BinaryPrimitives.WriteInt32LittleEndian(records[0].AsSpan(4),100300325);
        BinaryPrimitives.WriteInt32LittleEndian(records[1].AsSpan(4),1113);
        byte[] rawRecords = [..records[0],..records[1]];
        void Name(BinaryWriter w,string value) { byte[] bytes=ChatText.Encoding.GetBytes(value);w.Write((uint)bytes.Length);w.Write(bytes); }
        byte[] Expected(byte type,bool megaphone,bool linked) => Build(w=>
        {
            w.Write(type);
            if (megaphone) { w.Write(new byte[]{2,0,10});w.Write((ushort)19);Name(w,"QuoteRole"); }
            else
            {
                w.Write((byte)0);
                if(type==6) w.Write(new byte[]{3,1});
                Name(w,"QuoteRole");w.Write((byte)2);
            }
            w.Write((uint)text.Length);w.Write(text);
            if(linked) { w.Write((byte)2);foreach(var record in records)w.Write(record); }
            Name(w,megaphone?"X":"TeamA");
            if(!linked && type==36) Name(w,"QuoteRole");
        });
        foreach(byte type in new byte[]{1,36,40,41})
        {
            byte[] body=Build(w=>
            {
                w.Write(type);w.Write((ushort)9999);w.Write(0u);w.Write((uint)text.Length);w.Write(text);
                Name(w,"Peer");w.Write((byte)2);w.Write((byte)2);foreach(var record in records)w.Write(record);
            });
            That(ChatRequests.TryParseHyperlink(body,out var parsed,out _) && parsed.Request.TargetName=="Peer" &&
                parsed.Request.TargetServer==2 && parsed.Request.Layer==9999 && parsed.Records.SequenceEqual(rawRecords),
                "私聊引用先解析目标名字和分组，再读取完整物品记录，不把名字长度当记录数");
            byte[] bad=(byte[])body.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(11+text.Length),13);
            That(!ChatRequests.TryParseHyperlink(bad,out _,out var error) && error==ChatHyperlinkError.InvalidTarget,
                "非法私聊目标独立拒绝，不尝试借用上一目标");
            That(!ChatRequests.TryParseHyperlink([..body,0],out _,out error) && error==ChatHyperlinkError.TrailingData,
                "记录之后不能携带无法解释的尾部");
            var mode=NamedWhisperService.ResponseType(type)!.Value;
            var named=new NamedChatMessage(mode,0,"QuoteRole",2,"") { AdventureName="TeamA" };
            var plan=ItemLinkPackets.DesignNamed(named,text,rawRecords);
            Bytes(plan.Body,Expected((byte)mode,false,true),"371保留原始混合正文、两条记录及附加名字，不追加65的一对一会话尾串");
            That(plan.Opcode==NotiPacketType.MESSAGE_OTHER_CHANNEL_HYPER_LINK && plan.MarkerPairs==2 && !plan.Degraded,
                "具名私聊与回复使用专用371而不是普通370");
            var fallback=ItemLinkPackets.DesignNamed(named,text,[]);
            Bytes(fallback.Body,Expected((byte)mode,false,false),"记录不足降级65仍保留私聊类型、发言人及一对一会话对象");
            That(fallback.Opcode==NotiPacketType.MESSAGE_OTHER_CHANNEL,"私聊引用退化不能变成公开普通聊天");
        }
        var guild=ItemLinkPackets.DesignNamed(new(ChatMessageType.Guild,0,"QuoteRole",2,"")
            { GuildFlag=3,GuildColorFlag=1,AdventureName="TeamA" },text,rawRecords);
        Bytes(guild.Body,Expected(6,false,true),"公会引用371携带两字节标志和附加名字，来源不依赖同屏Actor");
        foreach(var kind in new[]{ChatKind.ServerMegaphone,ChatKind.ChannelMegaphone})
        {
            byte type=kind==ChatKind.ServerMegaphone?(byte)11:(byte)13;
            var message=new MegaphoneText("QuoteRole","") {Channel=new(2,10),Actor=19};
            var plan=ItemLinkPackets.DesignMegaphone(kind,message,text,rawRecords);
            Bytes(plan.Body,Expected(type,true,true),"372喇叭引用保留真实分组、频道、角色编号及两条完整物品记录");
            That(plan.Opcode==NotiPacketType.MEGAPHONE_MSG_HYPER_LINK,"喇叭引用不能使用370或纯文字118");
            var fallback=ItemLinkPackets.DesignMegaphone(kind,message,text,[]);
            Bytes(fallback.Body,Expected(type,true,false),"喇叭无可用记录时只退化原喇叭通道，仍保留表情和来源");
            That(fallback.Opcode==NotiPacketType.MEGAPHONE_MSG,"喇叭降级不改变显示页签或广播类型");
        }
        byte[] clipped=[..Enumerable.Repeat((byte)'a',254),0x7f,0x1e,0x20,77,0x1f];
        var boundary=ItemLinkPackets.Design(ChatKind.Normal,19,"QuoteRole",clipped,[]);
        That(boundary.Truncated && BinaryPrimitives.ReadUInt32LittleEndian(boundary.Body.AsSpan(4))==254,
            "链接截断不能留下半个内嵌表情标记");
        byte[] hornRequest=Build(w=>{w.Write((byte)13);w.Write((ushort)0);w.Write(118u);w.Write((uint)text.Length);w.Write(text);
            w.Write((byte)2);foreach(var record in records)w.Write(record);});
        That(ChatRequests.TryParseHyperlink(hornRequest,out var horn,out _) && horn.Request.Arg==118 && horn.Kind==13,
            "424喇叭头部必须保留实际扣费槽位");
    }

    static void Parsing()
    {
        foreach (var sample in Samples)
        {
            byte[] body = Convert.FromHexString(sample.Hex);
            That(ChatRequests.TryParse(body, out var send), "真实 0x0011 样本应当解析成功: " + sample.Hex);
            That(send.Type == sample.Type && send.Layer == sample.Layer && send.Arg == sample.Arg,
                "样本 type/layer/arg 保留原值: " + sample.Hex);
            That(send.TextLength == sample.TextLength && send.Text == sample.Text, "样本文本长度与 CP936 文本: " + sample.Hex);
            That(body.Length == ChatRequests.HeaderLength + send.TextLength, "正文 = 11 字节头 + textLen 字节文本: " + sample.Hex);
        }

        // 不足 11 字节头、textLen 超出实际字节都保持 Unhandled，不猜测文本。
        That(!ChatRequests.TryParse(Convert.FromHexString("0304000000000002000000"), out _), "10 字节正文不足 11 字节头");
        That(!ChatRequests.TryParse(Convert.FromHexString("03040000000000020000002E"), out _), "textLen 超出实际字节拒绝");
        That(!ChatRequests.TryParse([], out _), "空正文拒绝");

        // 非法 CP936 字节在聊天路径替换为回退字符，不让玩家输入断开连接。
        That(ChatRequests.TryParse(Convert.FromHexString("030400000000000100000081"), out var broken) &&
            broken.TextLength == 1 && broken.Text == "?", "非法 CP936 字节宽松解码不抛异常");
        // 严格 CP936：中文样本必须用代码页 936 解出，不能按 UTF-8 或当前区域设置解。
        That(ChatText.Encoding.CodePage == 936 && ChatText.Encoding.GetString(Convert.FromHexString("B2E2CAD4")) == "测试",
            "聊天文本固定 CP936 解码");
    }

    static void Truncation()
    {
        That(ChatText.MaxBytes == 200, "单条聊天文本上限固定 200 字节");
        string ascii200 = new('a', 200);
        That(ChatText.Truncate(ascii200, out bool asciiTruncated) == ascii200 && !asciiTruncated, "200 字节 ASCII 不截断");
        string ascii201 = new('a', 201);
        string kept = ChatText.Truncate(ascii201, out bool truncated);
        That(truncated && kept.Length == 200 && kept == new string('a', 200), "201 字节 ASCII 截断到 200 字节");

        // 双字节汉字必须按字符边界截断：101 个汉字 = 202 字节 ⇒ 只保留前 100 个（200 字节）。
        string chinese = new('测', 101);
        string clipped = ChatText.Truncate(chinese, out bool clippedFlag);
        That(clippedFlag && clipped.Length == 100 && ChatText.ByteCount(clipped) == 200 && clipped == new string('测', 100),
            "超长中文按字符边界截断，不产生半个汉字的字节序列");
        string exactText = new('测', 100);
        That(ChatText.Truncate(exactText, out bool exactFlag) == exactText && !exactFlag && ChatText.ByteCount(exactText) == 200,
            "恰好 200 字节的中文不截断");

        // 混排：ASCII + 汉字同样以 CP936 字节数为准。
        string mixed = new string('a', 199) + "测";
        string mixedKept = ChatText.Truncate(mixed, out bool mixedTruncated);
        That(mixedTruncated && ChatText.ByteCount(mixedKept) == 199 && mixedKept == new string('a', 199),
            "混排截断不会切开末尾的双字节字符");
    }

    // 独立确定性门禁复用核心原断言：只使用显式单调时钟，不启动网络或读取资源。
    internal static void RunRateRules()
    {
        var clock = new ConsumableUseSessionTests.FakeClock();
        var limiter = new ChatRateLimiter(clock);
        for (int index = 0; index < ChatRateLimiter.ShortWindowLimit; index++)
            That(limiter.Allow(), $"1 秒窗口内第 {index + 1} 条允许");
        That(!limiter.Allow(), "同一连接 1 秒内第 6 条被节流");
        clock.Advance(TimeSpan.FromMilliseconds(1100));
        That(limiter.Allow(), "短窗口过期后允许发送");

        var longWindow = new ChatRateLimiter(clock);
        for (int batch = 0; batch < 5; batch++)
        {
            if (batch != 0) clock.Advance(TimeSpan.FromSeconds(2));
            for (int index = 0; index < ChatRateLimiter.ShortWindowLimit; index++)
                That(longWindow.Allow(), $"10 秒窗口第 {batch * 5 + index + 1} 条允许");
        }
        // 此刻 25 条仍在 10 秒窗口内：再发 5 条到达 30 条上限，第 31 条被节流。
        clock.Advance(TimeSpan.FromSeconds(1));
        for (int index = 0; index < 5; index++)
            That(longWindow.Allow(), $"第 {26 + index} 条允许（未超过 30 条/10 秒）");
        That(!longWindow.Allow(), "同一连接 10 秒内第 31 条被节流");
        // 10 秒窗口整体过期后恢复。
        clock.Advance(TimeSpan.FromSeconds(11));
        That(longWindow.Allow(), "10 秒窗口过期后恢复发送");
    }

    /// <summary>
    /// 既有普通/公会显示保持，队伍按匹配原码使用2；不能从其他显示type推断队伍必须映射成普通。
    /// </summary>
    static void ChannelMapping()
    {
        That(ChatWire.TypeOf(ChatChannel.Normal) == 3 && ChatWire.TypeOf(ChatChannel.Party) == 2 &&
            ChatWire.TypeOf(ChatChannel.Whisper) == 3 && ChatWire.TypeOf(ChatChannel.Guild) == 6,
            "普通和悄悄话保持3，队伍使用原生2，公会保持6");
        That(ChatWire.ChannelOf(ChatKind.Normal) == ChatChannel.Normal && ChatWire.ChannelOf(ChatKind.Party) == ChatChannel.Party &&
            ChatWire.ChannelOf(ChatKind.Whisper) == ChatChannel.Whisper &&
            ChatWire.ChannelOf(ChatKind.Guild) == ChatChannel.Guild,
            "入站 kind 归到对应显示频道（普通/悄悄话/公会）");
        Reject<ArgumentOutOfRangeException>(() => ChatWire.TypeOf(ChatKind.ServerMegaphone),
            "喇叭不走 0x000C，没有显示 type 映射而不是静默发出一个聊天行");
        Reject<ArgumentOutOfRangeException>(() => ChatWire.TypeOf(ChatKind.ChannelMegaphone),
            "频道喇叭同样没有 0x000C 显示 type");
        Reject<ArgumentOutOfRangeException>(() => ChatWire.TypeOf(ChatChannel.Raid),"未核定的攻坚显示通道不能借用公会6");
        foreach(var type in new[]{ChatRequestType.Context5,ChatRequestType.Context8,ChatRequestType.Context9,
            ChatRequestType.Named7,ChatRequestType.Named64,ChatRequestType.NamedReply66,ChatRequestType.Item898Megaphone})
            That(!ChatService.TryKind((byte)type,out _),"有wire枚举不代表业务已接入，不能自动广播未知上下文");
    }

    static void Wire()
    {
        // 0x0C MESSAGE：[u8 type][u16 层 id][u8 v=0][u32 CP936 字节数][CP936 文本]，长度 1..255。
        // 前两条是 22:4x 实机 inject-raw 的成功样本，直接当期望值，不调用服务端 Builder 反算。
        Bytes(ChatPackets.Message(ChatKind.Normal, 4, "TEST-A"),
            Convert.FromHexString("0304000006000000544553542D4100000000"), "普通聊天正文及原生必读尾串长度头");
        Bytes(ChatPackets.Message(ChatKind.Normal, 4, "测试中文"),
            Convert.FromHexString("0304000008000000B2E2CAD4D6D0CEC400000000"), "普通聊天中文正文及原生尾串");
        Bytes(ChatPackets.Message(ChatKind.Guild, 0x1234, "GUILD"),
            Convert.FromHexString("06341200050000004755494C4400000000"), "公会聊天正文及原生尾串");
        // 实测（2026-10-21）「普通」页签只渲染 type ∈ {3,6,0x37,0x38}：悄悄话必须与普通同色，
        // 不能再用 0x2E（客户端不渲染），因此正文首字节是映射后的 3。
        Bytes(ChatPackets.Message(ChatKind.Whisper, 0, "hi"), Convert.FromHexString("0300000002000000686900000000"),
            "悄悄话正文首字节映射为 3（实测「普通」页签只渲染 3/6/0x37/0x38）、文本 hi");

        // 空正文仍保留正文及发言人的两个长度头。
        Bytes(ChatPackets.Message(ChatKind.Normal, 4), Convert.FromHexString("030400000000000000000000"), "空正文保留两个DSTR长度头");
        Bytes(ChatPackets.Message(ChatKind.Normal, 4, ""), Convert.FromHexString("030400000000000000000000"), "空正文与空尾串均有长度头");
        That(ChatPackets.HeaderLength == 8 && ChatPackets.MaxTextBytes == 0xFF &&
            ChatPackets.Message(ChatKind.Normal, 4).Length == 12 && ChatPackets.Message(ChatKind.Normal, 4, "").Length == 12,
            "0x0C 头固定 8 字节（type + 层 id + v + 长度），正文长度上限 0xFF");

        // 255 字节上限：300 个 ASCII 只保留前 255 字节，长度字段同步为 255。
        byte[] longAscii = ChatPackets.Message(ChatKind.Normal, 1, new string('a', 300), out bool asciiCut);
        That(asciiCut && longAscii.Length == 12 + 255 &&
            BinaryPrimitives.ReadUInt32LittleEndian(longAscii.AsSpan(4)) == 255 && longAscii.AsSpan(8,255).IndexOfAnyExcept((byte)'a') < 0,
            "300 字节 ASCII 按 255 字节截断，长度字段 255、帧长 263");

        // 汉字按字符边界截断：200 个汉字 = 400 字节 ⇒ 只保留 127 个完整汉字（254 字节），不切半个字。
        byte[] longCn = ChatPackets.Message(ChatKind.Normal, 1, new string('汉', 200), out bool cnCut);
        int cnLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(longCn.AsSpan(4));
        That(cnCut && cnLength == 254 && longCn.Length == 12 + cnLength &&
            ChatText.Encoding.GetString(longCn, 8, cnLength) == new string('汉', 127),
            "超长中文按字符边界截到 254 字节（127 个完整汉字），解码无半个字");
    }

    /// <summary>
    /// 喇叭通道 <c>NOTI 0x0076 MEGAPHONE_MSG</c>：实测整帧逐字节、三条串必须非空（len=0 会让客户端整帧丢弃）、
    /// 29/511/16 字节按字符边界截断，以及服务器/频道喇叭的 <c>type</c> 分别是 <c>0x0E</c>/<c>0x0B</c>。
    /// </summary>
    static void MegaphoneWire()
    {
        // 2026-10-20 23:5x 实机截图确认的整帧（type=0x0B、str1=AAA、str2=MEGA-11-WHITELIST、str3=BBB）：
        // [0B][B=00][C=00][D=00][E=0000] + [03000000 AAA] + [11000000 MEGA-11-WHITELIST] + [03000000 BBB]
        Bytes(MegaphonePackets.Build(ChatKind.ServerMegaphone, "AAA", "MEGA-11-WHITELIST", "BBB"),
            Convert.FromHexString("0B000000000003000000414141110000004D4547412D31312D57484954454C49535403000000424242"),
            "历史type11显示样本保持完整三条DSTR，不据此把道具37误映射到11");

        // 两种喇叭的确切整帧（角色名「测试2」5 字节、正文「喇叭测试」8 字节、占位 X 1 字节），用于实机验收对照。
        Bytes(MegaphonePackets.Build(ChatKind.ServerMegaphone, "测试2", "喇叭测试"),
            Convert.FromHexString("0B000000000005000000B2E2CAD43208000000C0AEB0C8B2E2CAD40100000058"),
            "服务器喇叭整帧：type11、角色名5B、正文8B、兼容尾串1B");
        Bytes(MegaphonePackets.Build(ChatKind.ChannelMegaphone, "测试2", "喇叭测试"),
            Convert.FromHexString("0D000000000005000000B2E2CAD43208000000C0AEB0C8B2E2CAD40100000058"),
            "频道喇叭整帧：type13，其余三串与服务器喇叭相同");

        That(MegaphonePackets.HeaderLength == 6 && MegaphonePackets.SpeakerMaxBytes == 29 &&
            MegaphonePackets.TextMaxBytes == 511 && MegaphonePackets.TagMaxBytes == 16,
            "0x76 头 6 字节；三条串上限分别是 29/511/16 字节（客户端要求 len < 0x1E/0x200/0x11）");
        That(MegaphonePackets.TypeOf(ChatKind.ServerMegaphone) == 11 &&
            MegaphonePackets.TypeOf(ChatKind.ChannelMegaphone) == 13,
            "玩家服务器喇叭type11，频道喇叭type13，不混入官方type14");
        Reject<ArgumentOutOfRangeException>(() => MegaphonePackets.TypeOf(ChatKind.Normal),
            "非喇叭种类没有 0x76 类型，直接抛出而不是静默按频道喇叭发出去");
        var source = new MegaphoneText("测试2","喇叭测试") { Channel = new(2,31), Actor = 0x1234 };
        Bytes(MegaphonePackets.Build(ChatKind.ServerMegaphone,source),
            Convert.FromHexString("0B02001F341205000000B2E2CAD43208000000C0AEB0C8B2E2CAD40100000058"),
            "服务器喇叭携带真实来源分组、频道及小端角色编号");
        Bytes(MegaphonePackets.Build(ChatKind.ChannelMegaphone,source),
            Convert.FromHexString("0D02001F341205000000B2E2CAD43208000000C0AEB0C8B2E2CAD40100000058"),
            "频道喇叭不能用全0或接收者频道代替来源身份");

        // 空发言人 / 空正文 / 空占位都回退成单字节：client 在任一条 len=0 时丢弃整帧。
        var empty = MegaphoneFields(MegaphonePackets.Build(ChatKind.ChannelMegaphone, "", ""));
        That(empty.Speaker == "?" && empty.SpeakerBytes == 1 && empty.Text == " " && empty.TextBytes == 1 &&
            empty.Tag == "X" && empty.TagBytes == 1,
            "空发言人/空正文/空 str3 都回退成单字节（? / 空格 / X），三条串长度都 ≥ 1");
        var nulls = MegaphoneFields(MegaphonePackets.Build(ChatKind.ServerMegaphone, null!, null!));
        That(nulls.SpeakerBytes >= 1 && nulls.TextBytes >= 1 && nulls.TagBytes >= 1, "null 输入同样不会生成 len=0");
        That(MegaphoneFields(MegaphonePackets.Build(ChatKind.ChannelMegaphone, "AAA", "MEGA", "")).Tag == "X",
            "显式传空 str3 也回退成占位 X，保证非空");

        // 截断：str1 20 个汉字 = 40 字节 ⇒ 14 个完整汉字（28 字节）；str2 300 个汉字 = 600 字节 ⇒ 255 个（510 字节）。
        var clipped = MegaphoneFields(MegaphonePackets.Build(ChatKind.ChannelMegaphone,
            new string('测', 20), new string('汉', 300)));
        That(clipped.Speaker == new string('测', 14) && clipped.SpeakerBytes == 28 &&
            clipped.Text == new string('汉', 255) && clipped.TextBytes == 510,
            "超长发言人/正文按字符边界截断到 28/510 字节，不产生半个汉字");
        That(clipped.SpeakerBytes <= MegaphonePackets.SpeakerMaxBytes &&
            clipped.TextBytes <= MegaphonePackets.TextMaxBytes && clipped.TagBytes >= 1,
            "截断后长度不超过 29/511 字节上限，且三条串仍非空");

        // 边界对照：29/511 字节整好不截断，30/512 字节各让出一字节。
        var exact = MegaphoneFields(MegaphonePackets.Build(ChatKind.ChannelMegaphone,
            new string('a', 29), new string('b', 511)));
        That(exact.SpeakerBytes == 29 && exact.TextBytes == 511, "恰好 29/511 字节的发言人/正文不截断");
        var over = MegaphoneFields(MegaphonePackets.Build(ChatKind.ChannelMegaphone,
            new string('a', 30), new string('b', 512)));
        That(over.Speaker == new string('a', 29) && over.Text == new string('b', 511),
            "30/512 字节的发言人/正文各截到 29/511 字节");
        // 首字符就是双字节汉字的极端：截断逻辑退到空串时必须回退成单字节，绝不发 len=0。
        That(MegaphoneFields(MegaphonePackets.Build(ChatKind.ChannelMegaphone, "测", "汉")).SpeakerBytes == 2,
            "单个汉字发言人整段保留（2 字节），不会被切成半个字");

        // 服务器喇叭 0x0E / 频道喇叭 0x0B：str3 由 Builder 保证非空，正文与发言人各自编码。
        var server = MegaphoneFields(MegaphonePackets.Build(ChatKind.ServerMegaphone, "测试2", "喇叭测试"));
        var channel = MegaphoneFields(MegaphonePackets.Build(ChatKind.ChannelMegaphone, "测试2", "喇叭测试"));
        That(server.Type == 11 && channel.Type == 13, "服务器喇叭发11、频道喇叭发13");
        That(server.Speaker == "测试2" && server.Text == "喇叭测试" && server.SpeakerBytes == 5 && server.TextBytes == 8,
            "服务器喇叭 str1=发言人、str2=正文，长度按 CP936 字节数");
        That(channel.Speaker == "测试2" && channel.Text == "喇叭测试" &&
            server.TagBytes >= 1 && channel.TagBytes >= 1,
            "频道喇叭同样带发言人与正文，两种喇叭的 str3 都非空");
        That(server.B == 0 && server.C == 0 && server.D == 0 && server.E == 0,
            "未指定来源的底层诊断构包保留0，正式投递使用MegaphoneText来源身份");
        var measured = MegaphonePackets.Measure(ChatKind.ServerMegaphone, "测试2", "喇叭测试");
        That(measured.Type == 11 && measured.SpeakerBytes == 5 && measured.TextBytes == 8,
            "Measure 与真实帧一致（投递日志用它记类型与段落长度，不写正文）");
    }

    /// <summary>按 <c>Docs/协议/聊天.md</c> 的布局解析一帧 <c>NOTI 0x0076</c> 正文，并校验三段长度自洽且都非空。</summary>
    static (byte Type, byte B, byte C, byte D, ushort E, string Speaker, string Text, string Tag,
        int SpeakerBytes, int TextBytes, int TagBytes) MegaphoneFields(byte[] body)
    {
        int offset = MegaphonePackets.HeaderLength;
        int ReadLength(out string value)
        {
            int bytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset));
            That(bytes >= 1, "喇叭每条串都必须 len ≥ 1（len=0 会让客户端丢弃整帧）");
            value = ChatText.Encoding.GetString(body, offset + 4, bytes);
            offset += 4 + bytes;
            return bytes;
        }

        int speakerBytes = ReadLength(out string speaker);
        int textBytes = ReadLength(out string text);
        int tagBytes = ReadLength(out string tag);
        That(offset == body.Length, "喇叭三段长度字段与整帧长度一致");
        return (body[0], body[1], body[2], body[3], BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)),
            speaker, text, tag, speakerBytes, textBytes, tagBytes);
    }

    /// <summary>
    /// 物品链接 <c>NOTI 0x0172 MESSAGE_HYPER_LINK</c>：实测 19 字节链接文本 + 1 条 148 字节记录 + 发言人名字尾串的
    /// 整帧逐字节、记录 <c>+0x04</c> 物品 id 不被改写、<c>0x7F</c>/记录配对规则，以及四条退化/防御边界：
    /// 记录为空 → <c>0x0C</c> 纯文本；记录条数少于收尾标记 → 退化；记录长度不等于 148 → 丢弃且不写非法帧；
    /// 截断造成标记不成对 → 剔除全部标记后按纯文本（<c>cnt=0</c>），不产生不成对标记。
    /// 尾串是 handler 读完记录后的必填段（<c>[u32 len2][CP936]</c>），内容为发言人名字：
    /// 客户端存进 <c>msg+0x44</c> → link run <c>+0xB4</c> 后补行首「角色名：」；空名字回落单字节 <c>?</c>。
    /// 期望值来自实测样本与客户端布局器反汇编，不调用服务端 Builder 反算解析结果。
    /// </summary>
    static void ItemLinkWire()
    {
        byte[] text = Convert.FromHexString("7F7F5BB2D0C6C6B5C4C5A3C6A4B6CCBFE35D7F");
        byte[] record = MeasuredLinkRecord();
        const string speaker = "测试2";
        byte[] expected = HyperLinkBody(3, 4, speaker, text, record);

        // 逐字节：03 04 0000 13 000000 <19B 文本> 01 <148B 记录> 05000000 B2E2CAD432。
        Bytes(ItemLinkPackets.Build(ChatKind.Normal, 4, 3, speaker, text, new[] { record }), expected,
            "实测 0x172 正文逐字节：type=3、layer=4、v=0、文本 19B、cnt=1、记录 148B、尾串=发言人 测试2（5B）");
        That(expected.Length == 8 + 19 + 1 + 148 + 4 + 5 && expected[0] == 3 &&
            BinaryPrimitives.ReadUInt16LittleEndian(expected.AsSpan(1)) == 4 && expected[3] == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(4)) == 19 && expected[27] == 1,
            "0x172 头 = [type][layer][v=0][u32 文本字节数]，文本后 1 字节 cnt，整帧 185 字节");
        That((ushort)NotiPacketType.MESSAGE_HYPER_LINK == 0x172, "0x172 是 MESSAGE_HYPER_LINK");
        That(Convert.ToHexString(expected) ==
            "0304000013000000" + "7F7F5BB2D0C6C6B5C4C5A3C6A4B6CCBFE35D7F" + "01" +
            Convert.ToHexString(record) + "05000000" + "B2E2CAD432",
            "185 字节整帧 hex = 实测头/19B 文本/148B 记录 + 尾串 05000000B2E2CAD432（发言人 测试2）");

        // 记录 +0x04 = 物品 id（样本 0x3070 = 12400），原样写出、不被服务端改写。
        int recordOffset = 8 + 19 + 1;
        That(expected.AsSpan(recordOffset, ChatRequests.HyperlinkRecordLength).SequenceEqual(record) &&
            BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(recordOffset + 4)) == 12400 &&
            Convert.ToHexString(expected.AsSpan(recordOffset, 8)) == "FFFFFFFF70300000",
            "记录原样写出，+0x04 物品 id 仍是 12400（70 30 00 00）");

        // 记录之后是发言人名字：[u32 len2=5][B2E2CAD432]（测试2 的 CP936 字节）。
        int tailOffset = recordOffset + 148;
        That(BinaryPrimitives.ReadUInt32LittleEndian(expected.AsSpan(tailOffset)) == 5 &&
            expected.AsSpan(tailOffset + 4, 5).SequenceEqual(Convert.FromHexString("B2E2CAD432")) &&
            ChatText.Encoding.GetString(expected, tailOffset + 4, 5) == speaker,
            "0x172 记录之后追加 [u32 5][B2E2CAD432]：尾串是发言人名字（必填、非空）");

        // 名字为空：尾串回落单字节 ?（len2=1、0x3F），整帧回到 181 字节。
        byte[] fallback = HyperLinkBody(3, 4, ItemLinkPackets.FallbackSpeaker, text, record);
        Bytes(ItemLinkPackets.Build(ChatKind.Normal, 4, 3, "", text, new[] { record }), fallback,
            "发言人名字为空时尾串回落 \"?\"，整帧 181 字节");
        That(fallback.Length == 8 + 19 + 1 + 148 + 4 + 1 &&
            Convert.ToHexString(fallback.AsSpan(fallback.Length - 5)) == "010000003F",
            "空名字 181 字节帧 = 实测头/19B 文本/148B 记录 + 尾串 [1][0x3F]");

        // 尾串上限：≤ 0x11 wchar 且 ≤ 0x21 字节，按字符边界截断、不切半个汉字。
        That(ItemLinkPackets.SpeakerBytes(speaker).SequenceEqual(Convert.FromHexString("B2E2CAD432")),
            "SpeakerBytes(测试2) = CP936 B2E2CAD432（5 字节）");
        byte[] longBytes = ItemLinkPackets.SpeakerBytes(new string('测', 30));
        That(longBytes.Length == 16 && ChatText.Encoding.GetString(longBytes) == new string('测', 8),
            "17是03BE25F0的字节接收容量，中文尾串最多16字节，不能误放大为33字节");
        That(ItemLinkPackets.SpeakerBytes(new string('a', 30)).Length == 16,
            "ASCII尾串也必须小于17字节");
        That(ItemLinkPackets.SpeakerBytes("").SequenceEqual(new byte[] { 0x3F }) &&
            ItemLinkPackets.SpeakerBytes(null).SequenceEqual(new byte[] { 0x3F }),
            "空/缺失名字编码为单字节 ?（字段必填非空）");

        // 配对口径：客户端链接块是 7F 7F [名] 7F，只有收尾 7F 消费记录。
        That(ItemLinkPackets.CountLinkPairs(text, out int pairs) && pairs == 1,
            "实测链接文本的收尾标记数 = 1（对应 1 条记录）");
        That(!ItemLinkPackets.CountLinkPairs(Convert.FromHexString("7F7F5B41"), out _),
            "只有开头 7F 7F、没有收尾 7F 的文本视为标记不成对");

        // 记录为空：退化 0x0C，文本原样（含 0x7F），长度 19。
        var empty = ItemLinkPackets.Design(ChatKind.Normal, 4, speaker, text, null);
        Bytes(empty.Body, ChatPackets.MessageBytes(ChatKind.Normal, 4, text, out _, speaker),
            "记录为空时退化 0x0C 正文，文本逐字节原样");
        That(!empty.HyperLink && empty.Degraded && empty.MarkerPairs == 1 && empty.Records == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(empty.Body.AsSpan(4)) == 19,
            "记录为空：Degraded=true、MarkerPairs=1、Records=0、0x0C 长度 19");

        // 两条链接、只带 1 条记录：退化，日志应看到 MarkerPairs=2、Records=1、Degraded=true。
        byte[] twoLinks = Convert.FromHexString("7F7F5B415D7F7F7F5B425D7F");
        That(ItemLinkPackets.CountLinkPairs(twoLinks, out int twoPairs) && twoPairs == 2,
            "两条相邻链接文本的收尾标记数 = 2");
        var shortfall = ItemLinkPackets.Design(ChatKind.Normal, 4, speaker, twoLinks, record);
        Bytes(shortfall.Body, ChatPackets.MessageBytes(ChatKind.Normal, 4, twoLinks, out _, speaker),
            "记录条数少于标记条数时退化 0x0C，文本原样");
        That(!shortfall.HyperLink && shortfall.Degraded && shortfall.MarkerPairs == 2 && shortfall.Records == 1,
            "记录不足：Degraded=true、MarkerPairs=2、Records=1");

        // 记录长度不是 148：被丢弃；合法记录仍够时照常 0x172，不够时退化，绝不写 cnt 与实际条数不一致的帧。
        byte[] bad = new byte[147];
        var dropped = ItemLinkPackets.Design(ChatKind.Normal, 4, speaker, text, bad);
        That(!dropped.HyperLink && dropped.Degraded && dropped.Records == 0,
            "只有 147 字节记录时被丢弃，退化为 0x0C");
        byte[] mixedRecords = new byte[148 + 147];
        record.CopyTo(mixedRecords, 0);
        bad.CopyTo(mixedRecords, 148);
        var mixed = ItemLinkPackets.Design(ChatKind.Normal, 4, speaker, text, mixedRecords);
        That(mixed.HyperLink && mixed.Records == 1 &&
            mixed.Body.AsSpan(8 + 19 + 1, ChatRequests.HyperlinkRecordLength).SequenceEqual(record),
            "合法 148 字节记录保留、尾部 147 字节残片丢弃，仍按 1 条记录发 0x172");
        Reject<ArgumentException>(() => ItemLinkPackets.Build(ChatKind.Normal, 4, 3, speaker, text, new[] { bad }),
            "合法记录不足时 Build 拒绝写帧，不生成 cnt 与实际记录数不一致的正文");

        // 截断：250 个 'a' + 6 字节链接块 = 256 字节，截到 255 时链接块缺收尾 7F（不成对）。
        // 按规则剔除全部 7F，退化为纯文本 [A] 且 cnt=0；不产生一半的链接。
        byte[] longText = new byte[250 + 6];
        longText.AsSpan(0, 250).Fill((byte)'a');
        Convert.FromHexString("7F7F5B415D7F").CopyTo(longText, 250);
        var cut = ItemLinkPackets.Design(ChatKind.Normal, 4, speaker, longText, null);
        That(cut.Degraded && cut.Truncated && cut.MarkersStripped && cut.MarkerPairs == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(cut.Body.AsSpan(4)) == 253 &&
            cut.Body.AsSpan(8).IndexOf((byte)0x7F) < 0 &&
            cut.Body.AsSpan(8 + 250, 3).SequenceEqual(Convert.FromHexString("5B415D")),
            "截断后标记不成对：剔除全部 0x7F、259→253 字节纯文本、无残留标记");
        byte[] cutHyper = ItemLinkPackets.Build(ChatKind.Normal, 4, 3, speaker, longText, Array.Empty<byte[]>());
        That(cutHyper.Length == 8 + 253 + 1 + 4 + 5 && cutHyper[8 + 253] == 0 &&
            cutHyper.AsSpan(8, 253).IndexOf((byte)0x7F) < 0 &&
            cutHyper.AsSpan(8 + 253 + 1).SequenceEqual(Convert.FromHexString("05000000B2E2CAD432")),
            "截断退化的 0x172 正文：文本 253 字节、cnt=0、无不成对标记、尾串仍为发言人 测试2");

        // 对照：链接块完整落在 255 字节以内、只截掉后面的普通文本时，配对仍完整，照常 0x172。
        byte[] tail = new byte[240 + 6 + 20];
        tail.AsSpan(0, 240).Fill((byte)'a');
        Convert.FromHexString("7F7F5B415D7F").CopyTo(tail, 240);
        tail.AsSpan(246).Fill((byte)'b');
        var kept = ItemLinkPackets.Design(ChatKind.Normal, 4, speaker, tail, record);
        That(kept.HyperLink && kept.Truncated && kept.MarkerPairs == 1 && kept.Records == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(kept.Body.AsSpan(4)) == 255 &&
            kept.Body[8 + 255] == 1 && kept.Body.Length == 8 + 255 + 1 + 148 + 4 + 5,
            "截断只切到链接块之后的文本时配对完整：0x172 文本 255B、cnt=1、记录 1 条、尾串 5B");
    }

    /// <summary>实测 148 字节物品记录：前 15 字节非零（<c>+0x04=0x3070</c>=12400），其余 0。</summary>
    static byte[] MeasuredLinkRecord()
    {
        byte[] record = new byte[ChatRequests.HyperlinkRecordLength];
        Convert.FromHexString("FFFFFFFF70300000FEC99A3B00001B").CopyTo(record, 0);
        return record;
    }

    /// <summary>
    /// 按 <c>NOTI 0x172</c> 布局手工拼正文：
    /// <c>[type][layer][v=0][u32 文本字节数][文本][cnt][记录][u32 len2][尾串]</c>；
    /// 尾串是发言人名字（CP936 字节，与生产 <see cref="ItemLinkPackets.SpeakerBytes"/> 同规则编码）。
    /// 测试侧自行编码，不调用服务端 Builder。
    /// </summary>
    static byte[] HyperLinkBody(byte type, ushort layer, string speaker, byte[] text, params byte[][] records)
    {
        byte[] tail = ChatText.Encoding.GetBytes(speaker);
        byte[] body = new byte[8 + text.Length + 1 + records.Length * ChatRequests.HyperlinkRecordLength + 4 + tail.Length];
        body[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), layer);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), checked((uint)text.Length));
        text.CopyTo(body, 8);
        body[8 + text.Length] = checked((byte)records.Length);
        for (int index = 0; index < records.Length; index++)
            records[index].CopyTo(body, 9 + text.Length + index * ChatRequests.HyperlinkRecordLength);
        int tailOffset = 9 + text.Length + records.Length * ChatRequests.HyperlinkRecordLength;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(tailOffset), checked((uint)tail.Length));
        tail.CopyTo(body, tailOffset + 4);
        return body;
    }

    /// <summary>
    /// 按实测布局手工拼 <c>CMD 0x01A8</c> 正文：<c>[kind][u16][u32 arg][u32 文本字节数][文本][cnt][记录]</c>；
    /// 头部 1..6 字节只复刻实测样本（<c>03 00 00 00 00 00</c>），不解释语义。
    /// </summary>
    static byte[] HyperlinkRequestBody(byte kind, byte[] text, params byte[][] records)
    {
        byte[] body = new byte[11 + text.Length + 1 + records.Length * ChatRequests.HyperlinkRecordLength];
        body[0] = kind;
        body[1] = 3; // 实测头部第 1 字节；1..6 语义未定，测试只复刻样本
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), checked((uint)text.Length));
        text.CopyTo(body, 11);
        body[11 + text.Length] = checked((byte)records.Length);
        for (int index = 0; index < records.Length; index++)
            records[index].CopyTo(body, 12 + text.Length + index * ChatRequests.HyperlinkRecordLength);
        return body;
    }

    /// <summary>
    /// 正式公告通道 <see cref="ServerNoticePackets"/>（<c>NOTI 0x06AD</c>）：
    /// 实测整帧、默认标题、24/600 字节上限与按字符边界截断，并校验 UTF-8 合法。
    /// </summary>
    static void NoticeWire()
    {
        // 实测确认的整帧：标题「系统公告」UTF-8 12 字节、正文「测试」6 字节、type=0。
        Bytes(ServerNoticePackets.Build("系统公告", "测试"),
            Convert.FromHexString("0C000000E7B3BBE7BB9FE585ACE5918A06000000E6B58BE8AF9500"),
            "公告整帧 = [标题字节数][标题 UTF-8][正文字节数][正文 UTF-8][type=0]");
        That(ServerNoticePackets.TitleMaxBytes == 0x18 && ServerNoticePackets.BodyMaxBytes == 0x258 &&
            ServerNoticePackets.Type == 0 && ServerNoticePackets.DefaultTitle == "系统公告",
            "公告上限固定标题 24 / 正文 600 字节，type=0，默认标题系统公告");

        // 空标题回落到默认「系统公告」，与显式标题编码完全一致。
        Bytes(ServerNoticePackets.Build("", "测试"), ServerNoticePackets.Build(ServerNoticePackets.DefaultTitle, "测试"),
            "标题为空时使用默认标题系统公告");
        Bytes(ServerNoticePackets.Build(null!, "测试"), ServerNoticePackets.Build(ServerNoticePackets.DefaultTitle, "测试"),
            "标题为 null 时同样使用默认标题");
        var empty = NoticeFields(ServerNoticePackets.Build("", ""));
        That(empty.Title == "系统公告" && empty.BodyBytes == 0 && empty.Type == 0,
            "正文允许为空，标题仍取默认值，type 仍为 0");

        // 400 个汉字 = 1200 UTF-8 字节 ⇒ 按字符边界截到 600 字节 = 200 个完整汉字。
        byte[] longFrame = ServerNoticePackets.Build("系统公告", new string('啊', 400), out bool bodyCut);
        var longNotice = NoticeFields(longFrame);
        That(bodyCut && longNotice.TitleBytes == 12 && longNotice.BodyBytes <= ServerNoticePackets.BodyMaxBytes &&
            longNotice.Body.Length == 200 && longNotice.Body.All(c => c == '啊') && longNotice.Type == 0,
            "400 个汉字正文按字符边界截到 ≤600 字节，解码后是完整汉字而不是半个字的字节序列");
        That(longFrame.Length == 9 + longNotice.TitleBytes + longNotice.BodyBytes,
            "截断后帧长 = 9 字节头 + 标题 + 正文");

        // 标题 9 个汉字 = 27 字节 ⇒ 截到 24 字节 = 8 个完整汉字；正文不受影响。
        byte[] longTitleFrame = ServerNoticePackets.Build(new string('天', 9), "x", out bool titleCut);
        var longTitle = NoticeFields(longTitleFrame);
        That(titleCut && longTitle.TitleBytes == 24 && longTitle.Title == new string('天', 8) &&
            longTitle.Body == "x" && longTitle.Type == 0,
            "标题超 24 字节按字符边界截到 24 字节，正文不截断");

        // 恰好 600 字节的正文不截断、不标记 truncated。
        var exact = NoticeFields(ServerNoticePackets.Build("系统公告", new string('啊', 200), out bool exactCut));
        That(!exactCut && exact.BodyBytes == 600 && exact.Body.Length == 200, "恰好 600 字节的正文不截断");
    }

    /// <summary>按公告布局解析一帧正文，并用严格 UTF-8 解码（非法字节直接抛异常，作为用例失败）。</summary>
    static (uint TitleBytes, uint BodyBytes, string Title, string Body, byte Type) NoticeFields(byte[] packet)
    {
        uint titleBytes = BinaryPrimitives.ReadUInt32LittleEndian(packet);
        uint bodyBytes = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4 + (int)titleBytes));
        var strict = new UTF8Encoding(false, true);
        return (titleBytes, bodyBytes,
            strict.GetString(packet, 4, (int)titleBytes),
            strict.GetString(packet, 8 + (int)titleBytes, (int)bodyBytes),
            packet[^1]);
    }

    // chat定向组复用这组无网络规则，不调用整套核心自测。
    internal static void OfficialMegaphoneRules()
    {
        int[] retained = [4, 5, 8, 13, 22, 23, 24, 32, 39, 40, 45, 50, 53, 54, 55, 64];
        int[] allowed = [22, 23, 32, 39, 40, 45, 50, 53, 54, 55, 64];
        foreach (int type in Enumerable.Range(0, 256))
        {
            var channel = new ChannelEntry(new(2, 10), type, "[test]");
            byte expected = retained.Contains(type) ? (byte)type : (byte)0;
            That(channel.LoginType == expected && ChannelPackets.LoginChannelType(channel) == expected &&
                ChannelPackets.Endpoint("127.0.0.1", 10011, channelType: channel.LoginType)[3] == expected,
                $"共享频道类型映射保持原LOGIN保留/fallback输出：{type}");
            That(ChatService.AllowsOfficialMegaphone(channel) == allowed.Contains(type),
                $"GM喇叭使用客户端实际模式，包含raw25回退0及4/5拒绝：{type}");
        }
        That(!ChatService.AllowsOfficialMegaphone(null), "缺少真实频道不能使用GM喇叭");
        That(ChatService.TryKind(14, out var kind) && kind == ChatKind.OfficialMegaphone &&
            ChatService.MegaphoneItemId(kind) == 1120 && MegaphonePackets.TypeOf(kind) == 14,
            "1120使用独立业务种类和原生14，不借普通36/37");
        That(!ChatService.TryKind(53, out _), "同心53资格未闭合，保留未接入拒绝");
        ChatRecipient[] recipients =
        [
            Recipient("same", 100, null, Scene(10)),
            Recipient("other-channel", 101, null, Scene(11)),
            Recipient("other-server", 102, null, Scene(10) with { Channel = new(3, 10) })
        ];
        var delivery = new Delivery(recipients);
        var origin = new ChatOrigin(100, "GMHolder", null, Scene(10));
        var dispatch = new ChatService(new Store(), delivery).Broadcast(new(14, 9999, 71, 2, "hi"), origin);
        That(dispatch.Accepted && dispatch.Recipients == 2 && delivery.Filter!(recipients[1]) && !delivery.Filter(recipients[2]) &&
            delivery.Notice is { Kind: ChatKind.OfficialMegaphone, Megaphone.Actor: 100, Megaphone.Speaker: "GMHolder" },
            "GM喇叭同分组跨频道投递，跨分组隔离且保留真实发言人");
        byte[] expectedBody = MegaphoneBody(14, "GMHolder", "hi");
        expectedBody[1] = 2;
        expectedBody[3] = 10;
        BinaryPrimitives.WriteUInt16LittleEndian(expectedBody.AsSpan(4), 100);
        Bytes(MegaphonePackets.Build(kind, delivery.Notice!.Megaphone!), expectedBody,
            "GM14的118保留6B来源头、真实名字与三串，不在服务端伪造官方显示名");
    }

    static void Routing()
    {
        var store = new Store();
        ChatOrigin Sender(ushort characterId = 100, long? guildId = null, ChatScene? scene = null) =>
            new(characterId, "发送者", guildId, scene ?? Scene(10));

        // 普通聊天：同场景（含自己）投递；同 t/area 但不同房间账号、不同频道、不同地图都不投。
        {
            var delivery = new Delivery(
                Recipient("self", 100, null, Scene(10)),
                Recipient("peer", 101, null, Scene(10)),
                Recipient("room-mate", 102, null, Scene(10, room: "self")),
                Recipient("other-channel", 103, null, Scene(11)),
                Recipient("other-map", 104, null, Scene(10, area: 2)),
                Recipient("other-instance", 105, null, Scene(10, instance: Guid.NewGuid())));
            var service = new ChatService(store, delivery);
            var dispatch = service.Broadcast(new ChatSend(3, 4, 0, 2, "hi"), Sender());
            That(dispatch.Accepted && dispatch.Recipients == 2, "普通聊天只投同场景在线角色（自己 + 同场景一人）");
            That(delivery.Notice is { Kind: ChatKind.Normal, Layer: 100, Channel: 10, Text: "hi", Origin.Scene.Channel.ServerIndex:2 },
                "普通聊天通知带请求层 id、发送者频道与实际正文");
            That(!dispatch.LayerFallback,"正式通知使用真实发言人身份，不再把编号解释为收件人层id");
        }
        {
            // 正文按 CP936 200 字节上限截断后才进通知：下行与 chat_send 日志看到的是同一份文本。
            var delivery = new Delivery(Recipient("self", 100, null, Scene(10)));
            var dispatch = new ChatService(store, delivery).Broadcast(new ChatSend(3, 4, 0, 250, new string('x', 250)), Sender());
            That(dispatch.Recipients == 1 && delivery.Notice?.Text == new string('x', 200) &&
                ChatText.ByteCount(delivery.Notice.Text) == ChatText.MaxBytes,
                "普通聊天正文在投递前按 200 字节截断");
        }
        {
            // 单人（只有自己）：层 id 就是发送者自己的，不构成兜底缺陷。
            var delivery = new Delivery(Recipient("self", 100, null, Scene(10)));
            var dispatch = new ChatService(store, delivery).Broadcast(new ChatSend(3, 9, 0, 2, "hi"), Sender());
            That(dispatch.Recipients == 1 && !dispatch.LayerFallback, "单人普通聊天只投自己，层 id 不标兜底");
        }

        // 公会聊天：成员表（只读 SQL）给账号集合，投递前再核对目标会话的公会快照一致。
        store.Guilds[7] = ["guild-a", "guild-b", "stale-member"];
        {
            var delivery = new Delivery(
                Recipient("guild-a", 200, 7, Scene(10)),
                Recipient("guild-b", 201, 7, Scene(10)),
                Recipient("stale-member", 202, null, Scene(10)),
                Recipient("guild-other", 203, 8, Scene(10)));
            var dispatch = new ChatService(store, delivery).Broadcast(new ChatSend(6, 3, 0, 2, "hi"), Sender(guildId: 7));
            That(dispatch.Accepted && dispatch.Recipients == 2,
                "公会聊天只投同公会：成员表内且会话快照公会 id 一致（成员表已删/未同步的会话不投）");
            That(delivery.Notice is { Kind: ChatKind.Guild, Layer: 100, Named.Mode:ChatMessageType.Guild, Named.Text:"hi" },
                "公会文本使用带明确名字、分组及公会标志的65/type6");
        }
        {
            var delivery = new Delivery(Recipient("peer", 101, null, Scene(10)));
            var dispatch = new ChatService(store, delivery).Broadcast(new ChatSend(6, 3, 0, 2, "hi"), Sender(guildId: null));
            That(!dispatch.Accepted && dispatch.Rejection == "not-in-guild" && delivery.Deliveries == 0,
                "未加入公会时公会聊天拒绝且不投递");
        }

        // 悄悄话：u32 arg 是目标角色 id，只投目标角色；同账号另一个角色与其他人都不投。
        store.Accounts[300] = "whisper-target";
        {
            var delivery = new Delivery(
                Recipient("whisper-target", 300, null, Scene(10)),
                Recipient("whisper-target", 301, null, Scene(10)),
                Recipient("bystander", 302, null, Scene(10)));
            var dispatch = new ChatService(store, delivery).Broadcast(new ChatSend(0x2E, 2, 300, 2, "hi"), Sender());
            That(dispatch.Accepted && dispatch.Recipients == 1, "悄悄话只投目标角色（同账号其他角色/旁人都不投）");
            That(delivery.Notice is { Kind: ChatKind.Whisper, Layer: 100, Text: "hi" },"原有角色ID私聊兼容入口仍保留真实发言人");
        }
        {
            var delivery = new Delivery(Recipient("bystander", 302, null, Scene(10)));
            var dispatch = new ChatService(store, delivery).Broadcast(new ChatSend(0x2E, 2, 999, 2, "hi"), Sender());
            That(!dispatch.Accepted && dispatch.TargetOffline && dispatch.Rejection is null && delivery.Deliveries == 0,
                "目标角色不存在/不在线：只记 chat_whisper_offline，不投递也不自造失败包");
        }
        {
            var delivery = new Delivery();
            var dispatch = new ChatService(store, delivery).Broadcast(new ChatSend(0x2E, 2, 70000, 2, "hi"), Sender());
            That(!dispatch.Accepted && dispatch.Rejection == "target-range", "悄悄话目标超出 u16 角色 id 时拒绝");
        }

        // 两种喇叭：服务器喇叭投全服在线；频道喇叭只投同频道。
        store.Accounts.Clear();
        ChatRecipient[] online =
        [
            Recipient("self", 100, null, Scene(10)),
            Recipient("same-channel", 101, null, Scene(10)),
            Recipient("other-channel", 102, null, Scene(11)),
            Recipient("other-server", 103, null, Scene(10) with { Channel = new(3,10) })
        ];
        {
            var delivery = new Delivery(online);
            var dispatch = new ChatService(store, delivery).Broadcast(new ChatSend(11, 0, 67, 2, "hi"), Sender());
            That(dispatch.Accepted && dispatch.Recipients == 3 && delivery.Filter!(online[2]) && !delivery.Filter(online[3]),
                "服务器喇叭覆盖2分组全部频道，但不得广播到其他服务器分组");
            That(delivery.Notice is { Kind: ChatKind.ServerMegaphone, Layer: 100, Channel: 10,
                    Megaphone: { Speaker: "发送者", Text: "hi", Channel.ServerIndex:2, Channel.Number:10, Actor:100 } },
                "服务器喇叭通知带发言人（角色名）与正文，走 0x76 喇叭页签");
        }
        {
            var delivery = new Delivery(online);
            var dispatch = new ChatService(store, delivery).Broadcast(new ChatSend(13, 0, 68, 2, "hi"), Sender());
            That(dispatch.Accepted && dispatch.Recipients == 2, "频道喇叭只投同频道在线角色");
            That(delivery.Notice is { Kind: ChatKind.ChannelMegaphone, Channel: 10,
                    Megaphone: { Speaker: "发送者", Text: "hi" } },
                "频道喇叭通知同样带发言人与正文，只投同频道");
        }

        // 未定义的 type 不猜语义。
        {
            var delivery = new Delivery();
            var dispatch = new ChatService(store, delivery).Broadcast(new ChatSend(9, 0, 0, 2, "hi"), Sender());
            That(!dispatch.Accepted && dispatch.Rejection == "unsupported-type" && delivery.Deliveries == 0, "未定义 type 拒绝且不投递");
        }
        {
            var delivery = new Delivery();
            var dispatch = new ChatService(store, delivery).Broadcast(new ChatSend(2, 100, 0, 2, "hi"), Sender());
            That(!dispatch.Accepted && dispatch.Rejection == "not-in-party" && delivery.Deliveries == 0,
                "队伍聊天没有实际队伍上下文时拒绝，不广播给同场景旁观者");
            Bytes(ChatPackets.Message(ChatKind.Party,100,"hi","Sender"),
                Convert.FromHexString("026400000200000068690600000053656E646572"),"队伍通知使用原生type2而非普通3");
        }

        That(ChatService.MegaphoneItemId(ChatKind.ServerMegaphone) == 36 && ChatService.MegaphoneItemId(ChatKind.ChannelMegaphone) == 37 &&
            ChatService.MegaphoneItemId(ChatKind.Normal) == 0, "服务器喇叭/频道喇叭分别对应背包道具 36/37");
    }

    /// <summary>
    /// 诊断工具 <see cref="ChatProbes.Body"/>：七个测试正文（文本 TEST/HI、层 id 4 举例）。
    /// L1/L2/L3 是 0x0C 的正文，与生产 builder 共用同一段编码（差别只在 GM 的用法约定：ASCII/中文/超长）；
    /// P3E/AAS 分别是 0x03E2 制裁弹窗与 0x06AD 弹框的正文，用于验证服务端能否让客户端显示任意文本。
    /// 这些正文都不进入正式投递。
    /// </summary>
    static void ProbeBodies()
    {
        // L1/L2/L3 与生产 builder 逐字节一致，文本 TEST、layer=4 时的确切正文：
        // [03][0400][00][04000000] + 54455354
        foreach (var variant in new[] { ChatProbeVariant.L1, ChatProbeVariant.L2, ChatProbeVariant.L3 })
        {
            Bytes(ChatProbes.Body(variant, 4, "TEST"), Convert.FromHexString("03040000040000005445535400000000"),
                variant + " 正文 = [03][layer][v=0][u32 CP936 字节数][CP936 文本]");
            Bytes(ChatProbes.Body(variant, 4, "TEST"), ChatPackets.Message(ChatKind.Normal, 4, "TEST"),
                variant + " 对英文文本与生产 builder 逐字节一致");
            Bytes(ChatProbes.Body(variant, 4, "测试中文"), ChatPackets.Message(ChatKind.Normal, 4, "测试中文"),
                variant + " 对中文文本与生产 builder 逐字节一致");
        }
        That(ChatProbes.Body(ChatProbeVariant.L1, 4, "TEST").Length == 16,
            "0x0C 诊断正文 = 8 字节头 + 4 字节文本");
        // L3 约定传超长文本：与生产 builder 一样按字符边界截到 255 字节并标记截断。
        byte[] longProbe = ChatProbes.Body(ChatProbeVariant.L3, 4, new string('a', 300), out bool probeCut);
        That(probeCut, "L3 超长文本标记截断");
        Bytes(longProbe, ChatPackets.Message(ChatKind.Normal, 4, new string('a', 300)),
            "L3 超长文本与生产 builder 逐字节一致（含 255 字节截断）");
        That(longProbe.Length == 12 + ChatPackets.MaxTextBytes, "L3截断后保留尾串长度头");

        // P3E 文本 TEST：A=1、B=1、字符数 4、UTF-16LE 8 字节、CP936 字节数 4、CP936 4 字节。
        // [01000000][01000000][04000000] + 5400450053005400 + [04000000] + 54455354
        Bytes(ChatProbes.Body(ChatProbeVariant.P3E, 4, "TEST"),
            Convert.FromHexString("01000000010000000400000054004500530054000400000054455354"),
            "P3E 正文 = [A=1][B=1][字符数][UTF-16LE][CP936 字节数][CP936]");
        // AAS 文本 HI：标题「系统公告」CP936 8 字节（CFB5CDB3B9ABB8E6），正文 2 字节，type=0。
        Bytes(ChatProbes.Body(ChatProbeVariant.AAS, 4, "HI"),
            Convert.FromHexString("08000000CFB5CDB3B9ABB8E602000000484900"),
            "AAS 正文 = [标题字节数][标题 CP936][正文字节数][正文 CP936][type=0]");

        // 三种编码变体的确切正文（文本「测试」；AAS 系不带层 id，layer 传 0）：
        // AAS  = 标题 CP936 8 字节 CFB5CDB3B9ABB8E6 + 正文 B2E2CAD4；
        // AAS8 = 标题 UTF-8 12 字节 E7B3BBE7BB9FE585ACE5918A + 正文 6 字节 E6B58BE8AF95；
        // AASU = 标题 UTF-16LE 8 字节 FB7CDF7E6C514A54 + 正文 4 字节 4B6DD58B（无 BOM、无结尾 \0）。
        Bytes(ChatProbes.Body(ChatProbeVariant.AAS, 0, "测试"),
            Convert.FromHexString("08000000CFB5CDB3B9ABB8E604000000B2E2CAD400"),
            "AAS 保持 CP936：正文 B2E2CAD4、长度按字节数");
        Bytes(ChatProbes.Body(ChatProbeVariant.AAS8, 0, "测试"),
            Convert.FromHexString("0C000000E7B3BBE7BB9FE585ACE5918A06000000E6B58BE8AF9500"),
            "AAS8 = 同布局，标题/正文改 UTF-8：正文 E6B58BE8AF95");
        Bytes(ChatProbes.Body(ChatProbeVariant.AASU, 0, "测试"),
            Convert.FromHexString("08000000FB7CDF7E6C514A54040000004B6DD58B00"),
            "AASU = 同布局，标题/正文改 UTF-16LE：正文 4B6DD58B、长度仍是字节数");
        var (aasTitleBytes, aasTextBytes, aasType) = AasFields(ChatProbes.Body(ChatProbeVariant.AAS, 0, "测试"));
        var (aas8TitleBytes, aas8TextBytes, aas8Type) = AasFields(ChatProbes.Body(ChatProbeVariant.AAS8, 0, "测试"));
        var (aasuTitleBytes, aasuTextBytes, aasuType) = AasFields(ChatProbes.Body(ChatProbeVariant.AASU, 0, "测试"));
        That(aasTitleBytes == 8 && aasTextBytes == 4 && aasType == 0 &&
            aas8TitleBytes == 12 && aas8TextBytes == 6 && aas8Type == 0 &&
            aasuTitleBytes == 8 && aasuTextBytes == 4 && aasuType == 0,
            "三种编码的长度字段都是编码后的字节数（8/4、12/6、8/4），type 固定 0");

        That(ChatProbes.Body(ChatProbeVariant.P3E, 4, "TEST").Length == 28 &&
            ChatProbes.Body(ChatProbeVariant.AAS, 4, "HI").Length == 19,
            "P3E/AAS 正文长度分别是 28/19 字节");
        That(ChatProbes.AasTitleMaxBytes == 0x18 && ChatProbes.AasBodyMaxBytes == 0x258,
            "AAS 标题/正文上限分别是 0x18(24) 与 0x258(600) 字节");

        That(ChatProbes.TryParseVariant("p3e", out var p3e) && p3e == ChatProbeVariant.P3E &&
            ChatProbes.TryParseVariant("Aas", out var aas) && aas == ChatProbeVariant.AAS &&
            ChatProbes.TryParseVariant("aas8", out var aas8) && aas8 == ChatProbeVariant.AAS8 &&
            ChatProbes.TryParseVariant("AasU", out var aasu) && aasu == ChatProbeVariant.AASU &&
            !ChatProbes.TryParseVariant("L4", out _) && !ChatProbes.TryParseVariant("AAS2", out _),
            "变体名大小写不敏感，只接受 L1/L2/L3/P3E/AAS/AAS8/AASU");
        That(ChatProbes.Noti(ChatProbeVariant.L2) == NotiPacketType.MESSAGE &&
            ChatProbes.Noti(ChatProbeVariant.P3E) == NotiPacketType.REVENUE_SANCTION &&
            ChatProbes.Noti(ChatProbeVariant.AAS) == NotiPacketType.AAS_TIPS &&
            ChatProbes.Noti(ChatProbeVariant.AAS8) == NotiPacketType.AAS_TIPS &&
            ChatProbes.Noti(ChatProbeVariant.AASU) == NotiPacketType.AAS_TIPS &&
            ChatProbes.IsAas(ChatProbeVariant.AASU) && !ChatProbes.IsAas(ChatProbeVariant.L1),
            "变体分别对应 0x000C/0x03E2/0x06AD，AAS8/AASU 与 AAS 同 opcode");

        // AAS 超长正文：400 个汉字 = 800 CP936 字节，截到 600 字节（300 个完整汉字）；标题固定 8 字节不被放大。
        byte[] longBody = ChatProbes.Body(ChatProbeVariant.AAS, 0, new string('啊', 400), out bool truncated);
        uint titleLength = BinaryPrimitives.ReadUInt32LittleEndian(longBody);
        uint textLength = BinaryPrimitives.ReadUInt32LittleEndian(longBody.AsSpan(4 + (int)titleLength));
        string content = ChatText.Encoding.GetString(longBody, 8 + (int)titleLength, (int)textLength);
        That(truncated && titleLength == 8 && textLength == 600 && content.Length == 300 && content.All(c => c == '啊'),
            "AAS 超长正文按字符边界截到 600 字节，标题仍是 8 字节");
        That(longBody.Length == 9 + 8 + 600 && longBody[^1] == 0,
            "AAS 截断后正文总长 = 9 字节头 + 8 字节标题 + 600 字节正文，type=0");

        // AASU 超长正文：400 个汉字 = 800 UTF-16LE 字节，按字符边界截到 600 字节（300 个完整汉字，字节数仍为偶数）。
        byte[] wideBody = ChatProbes.Body(ChatProbeVariant.AASU, 0, new string('啊', 400), out bool wideCut);
        var (wideTitle, wideText, wideType) = AasFields(wideBody);
        That(wideCut && wideTitle == 8 && wideText <= ChatProbes.AasBodyMaxBytes && wideText % 2 == 0 && wideType == 0 &&
            Encoding.Unicode.GetString(wideBody, 8 + (int)wideTitle, (int)wideText) == new string('啊', (int)wideText / 2),
            "AASU 超长正文按字符边界截到不超过 600 字节、偶数个字节且无半个字符");
        That(wideBody.Length == 9 + 8 + wideText && wideBody[^1] == 0,
            "AASU 截断后正文总长 = 9 字节头 + 8 字节标题 + 正文字节数，type=0");

        ChatProbes.Body(ChatProbeVariant.AAS, 0, "HI", out bool hiCut);
        That(!hiCut, "AAS 未超长时不标记截断");
    }

    /// <summary>读 AAS 系正文的两个长度字段与末尾 <c>type</c>，供三个编码变体共用同一套校验。</summary>
    static (uint TitleBytes, uint TextBytes, byte Type) AasFields(byte[] body)
    {
        uint titleLength = BinaryPrimitives.ReadUInt32LittleEndian(body);
        uint textLength = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4 + (int)titleLength));
        return (titleLength, textLength, body[^1]);
    }

    /// <summary>
    /// 回归：生产路径不识别聊天文本前缀。以 <c>L1 </c>/<c>L2 </c>/<c>L3 </c> 开头的普通聊天
    /// 必须与普通文本一样逐条进入正式投递，且不产生 <c>chat_probe</c> 帧/日志——
    /// 防止把 GM 诊断探针重新塞回 <see cref="ChatHandler"/>。
    /// 同时覆盖两种喇叭的正式路径：扣减失败不投递、也不发 <c>0x76</c> 喇叭帧；扣减后的喇叭投影是
    /// <c>0x76</c>（发言人 + 正文，服务器喇叭 <c>0x0E</c>），旧 <c>0x06AD</c> 弹框不再用于喇叭。
    /// 使用真实会话与真实发送队列（写进 MemoryStream），不需要 socket 或 PVF。
    /// </summary>
    static async Task DeliveryPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-chat-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("chat-probe.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var owners = new AccountOwners();
            using var owner = owners.Acquire("chat-probe", () => { });
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            repository.Create(owner, new NewCharacter("ProbeRole", point, point));
            var session = new GameSession(repository, towns, owner);
            session.Select(0);
            session.MarkReady();

            var actor = session.CaptureSocialActor()!;
            PartySnapshot? currentParty = new(1,[new(actor,session.PublicCharacter(actor.Character.Id)!,0)],0,1);
            session.CurrentParty = () => currentParty;
            var partyDelivery = new Delivery(Recipient(owner.Account,actor.Character.Id,null,Scene(10)));
            var partyChat = new ChatService(new Store(),partyDelivery);
            var partyResult = partyChat.Broadcast(new ChatSend(2,9999,0,2,"hi"),session.ChatOrigin()!,session);
            var queuedParty = partyDelivery.Notice!;
            That(partyResult.Accepted && partyResult.Recipients==1 && queuedParty.Layer==actor.Character.Id && queuedParty.Guard!(),
                "单人队伍从现有会话名册投递，不能冒用请求中的发言人编号");
            currentParty = currentParty with { Revision = 2 };
            That(queuedParty.Guard!(),"无成员变化的队伍快照更新不丢弃已排队聊天");
            session.AdvancePartyMembership();
            That(!queuedParty.Guard!(),"退出并重新进入队伍后旧成员版本的排队聊天不发布");
            currentParty = null;
            That(partyChat.Broadcast(new ChatSend(2,0,0,2,"hi"),session.ChatOrigin()!,session).Rejection=="not-in-party",
                "离队后不能继续使用旧队伍名册");

            ChatNotice? heldMegaphone = null;
            session.ChatNoticeArrived = notice => heldMegaphone = notice;
            var foreignHorn = new ChatNotice(ChatKind.ChannelMegaphone,1,10,
                Megaphone:new("Speaker","hi") { Channel = new(0,10), Actor = 1 });
            session.TryDeliverChat(_=>true,foreignHorn);
            That(!heldMegaphone!.Guard!(),"初筛误放行时，发送前仍拒绝非本频道的频道喇叭");
            session.TryDeliverChat(_=>true,foreignHorn with { Kind = ChatKind.ServerMegaphone });
            That(heldMegaphone!.Guard!(),"服务器喇叭在相同分组内不按接收者频道限制");
            session.TryDeliverChat(_=>true,foreignHorn with { Kind = ChatKind.OfficialMegaphone });
            That(heldMegaphone!.Guard!(),"GM喇叭同样复用服务器范围的出队身份门禁");
            var queuedMegaphone = heldMegaphone;
            session.TryDeliverChat(_=>true,foreignHorn with { Kind = ChatKind.ServerMegaphone,
                Megaphone = foreignHorn.Megaphone! with { Channel = new(2,10) } });
            That(!heldMegaphone!.Guard!(),"即使初筛误放行，服务器喇叭也不能跨服务器分组写出");
            var origin = session.ChatOrigin()!;
            var ordinary = new ChatNotice(ChatKind.Normal,origin.CharacterId,origin.ChannelNumber,Text:"hi") { Origin = origin };
            session.TryDeliverChat(_=>true,ordinary with { Origin = origin with { Scene = origin.Scene with { Map = "other.map" } } });
            That(!heldMegaphone!.Guard!(),"普通聊天写出前仍校验完整场景，不向其他房间或副本实例泄漏");
            session.TryDeliverChat(_=>true,ordinary with { Origin = origin with { Scene = origin.Scene with { Channel = new(0,2) } } });
            That(!heldMegaphone!.Guard!(),"普通消息仍不能在出队时跨频道写入");
            session.TryDeliverChat(_=>true,ordinary with { Kind = ChatKind.Guild, Origin = origin with { GuildId = 7 } });
            That(!heldMegaphone!.Guard!(),"公会消息写出前复核当前角色公会，不沿用入队时的旧资格");
            session.TryDeliverChat(_=>true,ordinary);
            var queuedOrdinary = heldMegaphone;
            That(queuedOrdinary!.Guard!(),"合法同场景普通消息可发布");
            session.ReturnToRoster();
            That(session.ChatOrigin() is null,"未选择角色不能冒用旧角色发送消息");
            session.Select(0);
            That(!queuedMegaphone!.Guard!(),"同连接重新选择同角色后旧喇叭投递失效");
            That(!queuedOrdinary.Guard!(),"同连接重选后普通聊天旧排队消息也失效");
            var entering = session.ChatOrigin()!;
            var heldBeforeEntering = heldMegaphone;
            bool deliveredEntering = session.TryDeliverChat(_=>true,new(ChatKind.Normal,entering.CharacterId,entering.ChannelNumber,Text:"entering")
                { Origin = entering, Guard = entering.IsCurrent });
            That(!session.Ready && entering.IsCurrent!() && !deliveredEntering && ReferenceEquals(heldBeforeEntering,heldMegaphone),
                "发送来源只需选中频道角色，但接收仍保留原Ready门禁");
            session.ChatNoticeArrived = null;

            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var frames = new List<(byte Kind, ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0,
                (kind, opcode, _, body, action) => frames.Add((kind, opcode, body.ToArray(), action)));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            var delivery = new Delivery();
            using var log = new ServerLog(Path.Combine(root, "Logs"));
            var handler = new ChatHandler(new ChatService(new Store(), delivery), null, log);
            var commands = new GameCommandRegistry();
            handler.Register(commands);
            commands.Seal();

            // 等发送队列把更早的 Post 写完：屏障包跑完即表示前面的帧已经落盘。
            async Task DrainAsync()
            {
                await context.Sender.Send(1, byte.MaxValue, [], "test_drain");
                frames.RemoveAll(frame => frame.Action == "test_drain");
            }

            // 这三条都是普通聊天内容（不是诊断指令）：每条都必须进入一次正式投递。
            foreach (string text in new[] { "L1 TEST", "L2 TEST", "L3 TEST" })
            {
                await commands.DispatchAsync(context, new Packet(1, (ushort)CmdPacketType.SEND_MESSAGE, SendBody(3, 4, 0, text)));
                await DrainAsync();
            }

            That(delivery.Deliveries == 3, "L1/L2/L3 前缀文本仍按普通聊天逐条进入正式投递");
            That(delivery.Notice is { Kind: ChatKind.Normal, Layer: 1, Text: "L3 TEST" },
                "正式投递仍使用普通聊天 0x0C 通知，并带上真正的聊天正文");
            That(frames.All(frame => frame.Action != "chat_probe"), "生产路径不再发送 chat_probe 诊断帧");
            That(context.Session.LastChatLayer == 4, "ChatHandler 记录该连接最近一次聊天层 id");
            That(!session.Ready && delivery.Notice!.Guard!(),"真实命令分派使用CharacterSelected门禁，不把进入世界阶段当作禁言");
            ChatProjection.Notice(context,delivery.Notice!);
            await DrainAsync();
            That(frames.All(frame=>frame.Action!="chat_message"),"原生聊天投影仍不向未Ready角色写出消息");
            session.MarkReady();
            ChatProjection.Notice(context,delivery.Notice!);
            await DrainAsync();
            That(frames.Count(frame=>frame.Action=="chat_message")==1,"接收上下文Ready后按原协议正常写出聊天消息");
            frames.RemoveAll(frame=>frame.Action=="chat_message");

            // 服务器喇叭扣减失败（该会话主背包没有喇叭）：不进入投递，也不发任何 0x76 喇叭帧。
            await commands.DispatchAsync(context, new Packet(1, (ushort)CmdPacketType.SEND_MESSAGE, SendBody(11, 0, 67, "喇叭")));
            await DrainAsync();
            That(delivery.Deliveries == 3, "服务器喇叭扣减失败时不进入投递（不广播）");
            That(frames.All(frame => frame.Opcode != (ushort)NotiPacketType.MEGAPHONE_MSG), "扣减失败不发 0x76 喇叭帧");

            // 投影：带喇叭文本的通知发 0x76（type 0x0E = 服务器喇叭），三段长度按 CP936 字节数。
            ChatProjection.Notice(context, new ChatNotice(ChatKind.ServerMegaphone, 0, 10,
                Megaphone: new MegaphoneText("ChatA", "hi") { Channel = new(2,10), Actor = 24 }));
            await DrainAsync();
            var noticeFrame = frames.Single(frame => frame.Action == "chat_megaphone");
            var expectedMegaphone = MegaphoneBody(11, "ChatA", "hi");
            expectedMegaphone[1]=2;expectedMegaphone[3]=10;
            BinaryPrimitives.WriteUInt16LittleEndian(expectedMegaphone.AsSpan(4),24);
            That(noticeFrame.Opcode == (ushort)NotiPacketType.MEGAPHONE_MSG &&
                Convert.ToHexString(noticeFrame.Body) == Convert.ToHexString(expectedMegaphone),
                "服务器喇叭投影为118/type11，保留来源身份、角色名和正文");
            That(frames.All(frame => frame.Opcode != (ushort)NotiPacketType.AAS_TIPS), "喇叭不再发 0x06AD 公告弹框");

            // 投影：普通聊天的 0x0C 带正文，长度是 CP936 字节数（与实测帧同布局）。
            ChatProjection.Notice(context, new ChatNotice(ChatKind.Normal, 4, 10, Text: "测试中文"));
            await DrainAsync();
            var messageFrame = frames.Single(frame => frame.Action == "chat_message");
            That(messageFrame.Opcode == (ushort)NotiPacketType.MESSAGE &&
                Convert.ToHexString(messageFrame.Body) == "0304000008000000B2E2CAD4D6D0CEC400000000",
                "普通聊天投影为 0x0C：正文 = [03][layer][v=0][CP936 字节数][测试中文]");

            await context.Reply(ClientFailurePackets.Create(ClientClassChangeError.AdvancementQuestInProgress),
                "native_prompt_test", () => true);
            var native = frames.Single(frame => frame.Action == "native_prompt_test");
            That(native.Kind == 1 && native.Opcode == 502 && native.Body.SequenceEqual(new byte[] { 0, 22, 0 }),
                "引用原生错误枚举后由现有发送队列发业务ACK，旧dstr仍由客户端业务处理器触发");
            await context.Reply(CmdPacketType.AUCTION_REGIST_ITEM, AuctionPackets.RegisterFailure(0xd2), "legacy_auction_prompt_test", () => true);
            var auction = frames.Single(frame => frame.Action == "legacy_auction_prompt_test");
            That(auction.Kind == 1 && auction.Opcode == 183 && auction.Body.SequenceEqual(new byte[] { 0, 210, 0 }),
                "旧拍卖业务入口同样通过发送队列写完整u16失败码，不只修正新类型工厂");
            foreach (var display in Enum.GetValues<ServerNoticeDisplay>().Where(display => (byte)display <= 3))
            {
                string action = "notice_prompt_test_" + (byte)display;
                await context.NotifyNotice(display, "材料不足", action, () => true);
                var sent = frames.Single(frame => frame.Action == action);
                byte[] expected = Convert.FromHexString("0008000000B2C4C1CFB2BBD7E3"); expected[0] = (byte)display;
                That(sent.Kind == 0 && sent.Opcode == 502 && sent.Body.SequenceEqual(expected),
                    "通用502与业务CMD502通过kind分开，显示动作和正文均由调用方明确选择");
            }
            int sentCount = frames.Count;
            await context.Reply(ClientFailurePackets.Create(ClientMailQueryError.RecipientMissing), "stale_native_prompt", () => false);
            await context.NotifyNotice(ServerNoticeDisplay.Chat, "材料不足", "stale_notice_prompt", () => false);
            That(frames.Count == sentCount, "原生提示与通用502都复用发送guard，失效业务不发布旧提示");
            Reject<ArgumentNullException>(() => context.NotifyNotice(ServerNoticeDisplay.Chat, "x", "missing_prompt_guard", null!),
                "新通用提示入口要求调用方提供发送身份校验");
            Reject<ArgumentNullException>(() => context.Reply(ClientFailurePackets.Create(ClientSkillError.SpInsufficient), "missing_native_guard", null!),
                "新原生提示入口同样不能省略发送身份校验");

            log.Flush();
            // 日志写入器仍以「追加 + FileShare.Read」持有文件，读取端必须显式共享写句柄。
            using var logFile = new FileStream(Path.Combine(root, "Logs", "server.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var logReader = new StreamReader(logFile);
            string events = logReader.ReadToEnd();
            That(!events.Contains("\"Kind\":\"chat_probe\""), "生产路径不再记录 chat_probe 日志");
            That(events.Contains("\"Kind\":\"chat_send\"") && events.Contains("\"Text\":\"L1 TEST\""),
                "L1 前缀文本按普通聊天记录 chat_send");

            // 用真实会话索引、投递和发送队列覆盖入城边界，不靠延时推断无消息。
            using var receivingOwner = owners.Acquire("chat-entry-boundary", () => { });
            repository.Create(receivingOwner,new NewCharacter("BoundaryRole",point,point));
            var receiving = new GameSession(repository,towns,receivingOwner);
            using var receivingStop = new CancellationTokenSource();
            using var receivingStream = new MemoryStream();
            var received = new List<byte[]>();
            await using var receivingSender = new GameConnectionSender(receivingOwner,receivingStream,receivingStop,0,
                (kind,opcode,_,body,_)=> { if(kind==0 && opcode==12) received.Add(body.ToArray()); });
            var receivingContext = new GameConnectionContext(new(2,IPAddress.Loopback,receivingStream,receivingStop),
                receivingOwner,receiving,receivingSender);
            var online = new OnlineSessionDirectory();
            online.Register(owner,session); online.Register(receivingOwner,receiving);
            var broadcast = new ChatService(new Store(),new ChatDelivery(online));
            session.ChatNoticeArrived = notice=>ChatProjection.Notice(context,notice);
            receiving.ChatNoticeArrived = notice=>ChatProjection.Notice(receivingContext,notice);
            async Task CheckBroadcast(string phase,bool shouldReceive)
            {
                int before=received.Count;
                var origin=session.ChatOrigin()!;
                var result=broadcast.Broadcast(new(3,9999,0,2,"hi"),origin,session);
                await receivingSender.DrainAsync(); await sender.DrainAsync();
                That(result.Accepted && result.Recipients==(session.Ready?1:0)+(shouldReceive?1:0),phase+"：投递数量只包含就绪接收者");
                That(received.Count==before+(shouldReceive?1:0),phase+"：实际写出的广播帧符合接收门禁");
            }
            try
            {
                await CheckBroadcast("未选择角色",false);
                receiving.Select(0);
                That(receiving.GetState()==UserState.EnteringWorld && !receiving.Ready,"选角已装载角色但入城投影尚未完成");
                await CheckBroadcast("选角加载未MarkReady",false);
                receiving.MarkReady();
                await CheckBroadcast("入城完成",true);
                receiving.ReturnToRoster();
                await CheckBroadcast("返回选角",false);
                receiving.Select(0);
                await CheckBroadcast("同连接重选加载中",false);
                receiving.MarkReady();
                await CheckBroadcast("同连接重选完成",true);
                session.ReturnToRoster(); session.Select(0);
                await CheckBroadcast("发送者已选择角色但尚未Ready",true);
                session.MarkReady();

                using var heldStop=new CancellationTokenSource();
                using var heldStream=new GameSenderBudgetTests.HeldStream();
                int heldMessages=0;
                await using var heldSender=new GameConnectionSender(receivingOwner,heldStream,heldStop,0,
                    (kind,opcode,_,_,_)=> { if(kind==0 && opcode==12) heldMessages++; });
                var heldContext=new GameConnectionContext(new(3,IPAddress.Loopback,heldStream,heldStop),receivingOwner,receiving,heldSender);
                receiving.ChatNoticeArrived=notice=>ChatProjection.Notice(heldContext,notice);
                var held=heldSender.Send(1,ushort.MaxValue,[],"chat_boundary_hold");
                try
                {
                    await heldStream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var accepted=broadcast.Broadcast(new(3,9999,0,2,"hi"),session.ChatOrigin()!,session);
                    That(accepted.Recipients==2 && heldSender.PendingBytes>0,"消息在接收者Ready时确实进入发送队列");
                    receiving.ReturnToRoster(); receiving.Select(0); receiving.MarkReady();
                }
                finally { heldStream.Release.TrySetResult(); }
                await held; await heldSender.DrainAsync();
                That(heldMessages==0,"排队后回选角并重新就绪，旧角色epoch消息仍不能写入新上下文");
                var current=broadcast.Broadcast(new(3,9999,0,2,"hi"),session.ChatOrigin()!,session);
                await heldSender.DrainAsync(); await sender.DrainAsync();
                That(current.Recipients==2 && heldMessages==1,"重选完成的新消息恢复接收，不遗留禁收状态");

                int receivedBeforeBlocked = received.Count;
                int framesBeforeBlocked = frames.Count;
                var blockedHandler = new ChatHandler(broadcast, null, log, named: new NamedWhisperService(online, repository), isChatBlocked: _ => true);
                var blockedCommands = new GameCommandRegistry();
                blockedHandler.Register(blockedCommands);
                blockedCommands.Seal();
                byte[] targetNameBytes = ChatText.Encoding.GetBytes("ChatA");
                byte[] whisperHead = SendBody(1, 4, 0, "blocked-whisper");
                byte[] whisperBody = new byte[whisperHead.Length + 4 + targetNameBytes.Length + 1];
                whisperHead.CopyTo(whisperBody, 0);
                BinaryPrimitives.WriteInt32LittleEndian(whisperBody.AsSpan(whisperHead.Length), targetNameBytes.Length);
                targetNameBytes.CopyTo(whisperBody, whisperHead.Length + 4);
                var blockedWhisper = await blockedCommands.DispatchAsync(receivingContext, new Packet(1, (ushort)CmdPacketType.SEND_MESSAGE, whisperBody));
                var blockedNormal = await blockedCommands.DispatchAsync(receivingContext, new Packet(1, (ushort)CmdPacketType.SEND_MESSAGE, SendBody(3, 4, 0, "blocked-normal")));
                await receivingSender.DrainAsync(); await sender.DrainAsync();
                log.Flush();
                using var blockedLogFile = new FileStream(Path.Combine(root, "Logs", "server.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var blockedLogReader = new StreamReader(blockedLogFile);
                string blockedEvents = blockedLogReader.ReadToEnd();
                That(blockedWhisper == CommandResult.Handled && blockedNormal == CommandResult.Handled &&
                    received.Count == receivedBeforeBlocked && frames.Count == framesBeforeBlocked &&
                    blockedEvents.Split('\n').Count(line => line.Contains("\"Kind\":\"chat_blocked\"", StringComparison.Ordinal)) == 2,
                    "禁言拦截在具名私聊与常规广播前生效，记chat_blocked且不向目标投递");
            }
            finally { session.ChatNoticeArrived=null; receiving.ChatNoticeArrived=null; }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    /// <summary>
    /// 物品链接 <c>CMD 0x01A8</c>：实测 179 字节正文（kind=3、文本 19 字节、cnt=1、148 字节记录）。
    /// 覆盖完整解析、文本与记录逐字节保真、<c>0x0C</c> 正文原样转发（不重新编码）、
    /// 长度/记录非法时只记录日志并丢弃（返回 Handled，不断线），以及成功投递的 <c>item_hyperlink</c> 日志。
    /// </summary>
    static async Task ItemHyperlink()
    {
        byte[] body = MeasuredHyperlinkBody();
        byte[] expectedText = Convert.FromHexString("7F7F5BB2D0C6C6B5C4C5A3C6A4B6CCBFE35D7F");

        That(ChatRequests.TryParseHyperlink(body, out var link, out var error) && error == ChatHyperlinkError.None,
            "实测 179 字节 CMD 0x01A8 正文解析成功");
        That(link.Kind == 3, "第 0 字节 kind=3（普通频道）");
        That(link.Text.Length == 19, "偏移 7 的 u32 长度 = 19");
        Bytes(link.Text, expectedText, "19 字节链接文本逐字节保真（含 0x7F 标记）");
        That(ChatText.Encoding.GetString(link.Text) == "\u007F\u007F[残破的牛皮短裤]\u007F",
            "链接文本按 CP936 是已格式化好的 [残破的牛皮短裤]（仅核对，不参与转发）");
        That(link.Records.Length == ChatRequests.HyperlinkRecordLength,
            "cnt=1、记录正好 148 字节");
        byte[] expectedRecord = new byte[ChatRequests.HyperlinkRecordLength];
        Convert.FromHexString("FFFFFFFF70300000FEC99A3B00001B").CopyTo(expectedRecord, 0);
        Bytes(link.Records, expectedRecord, "148 字节物品记录逐字节保真（前 15 字节为实测值，其余 00）");
        That(body.Length == 179 && link.Text.Length + link.Records.Length + 12 == body.Length,
            "整包 179 字节 = 11 字节头 + 19 字节文本 + 1 字节 cnt + 148 字节记录");

        // 下行 0x172：8 字节头 + 原样链接字节 + cnt=1 + 148 字节记录 + [u32 8][LinkRole] 尾串。
        // 发言人名字取会话角色名（LinkRole，8 字节），不再是占位空格。
        byte[] expectedHyper = HyperLinkBody(3, 4, "LinkRole", link.Text, expectedRecord);
        Bytes(ItemLinkPackets.Build(ChatKind.Normal, 4, 3, "LinkRole", link.Text, new[] { link.Records }), expectedHyper,
            "0x172 正文 = [03][layer][v=0][u32 19][原样 19 字节链接文本][cnt=1][148 字节记录][u32 8][LinkRole]");
        That(expectedHyper.Length == 8 + 19 + 1 + 148 + 4 + 8 &&
            BinaryPrimitives.ReadUInt32LittleEndian(expectedHyper.AsSpan(8 + 19 + 1 + 4)) == 12400 &&
            BinaryPrimitives.ReadUInt32LittleEndian(expectedHyper.AsSpan(8 + 19 + 1 + 148)) == 8 &&
            ChatText.Encoding.GetString(expectedHyper, 8 + 19 + 1 + 148 + 4, 8) == "LinkRole",
            "0x172 正文携带完整记录与发言人名字尾串，记录 +0x04 物品 id 仍是 12400");

        // cnt=0 合法（只转发文本）；cnt=3 且带满 3 条记录同样合法；记录不足必须拒绝。
        byte[] noRecords = body[..31];
        noRecords[30] = 0;
        That(ChatRequests.TryParseHyperlink(noRecords, out var none, out _) && none.Records.Length == 0,
            "cnt=0 可解析：没有记录字节");
        byte[] threeRecords = new byte[31 + 3 * ChatRequests.HyperlinkRecordLength];
        body.AsSpan(0, 31).CopyTo(threeRecords);
        threeRecords[30] = 3;
        for (int index = 0; index < 3; index++)
            body.AsSpan(31, ChatRequests.HyperlinkRecordLength)
                .CopyTo(threeRecords.AsSpan(31 + index * ChatRequests.HyperlinkRecordLength));
        That(ChatRequests.TryParseHyperlink(threeRecords, out var three, out _) &&
            three.Records.Length == 3 * ChatRequests.HyperlinkRecordLength,
            "cnt=3 且带满 3 × 148 字节记录时完整解析");
        byte[] threeCount = body.ToArray();
        threeCount[30] = 3;
        That(!ChatRequests.TryParseHyperlink(threeCount, out _, out var countError) && countError == ChatHyperlinkError.RecordsOverrun,
            "cnt=3 但只带 1 条记录（148 < 444）拒绝");
        That(!ChatRequests.TryParseHyperlink(body[..178], out _, out var cutError) && cutError == ChatHyperlinkError.RecordsOverrun,
            "记录被截断（147 < 148）拒绝");

        // 长度非法：0、>4096、文本越界、头部不足。全部返回 false 并给出原因，调用方只记日志。
        byte[] zeroLength = body[..12];
        BinaryPrimitives.WriteUInt32LittleEndian(zeroLength.AsSpan(7), 0);
        That(!ChatRequests.TryParseHyperlink(zeroLength, out _, out var zeroError) && zeroError == ChatHyperlinkError.LengthZero,
            "文本长度 0 拒绝");
        byte[] hugeLength = body[..12];
        BinaryPrimitives.WriteUInt32LittleEndian(hugeLength.AsSpan(7), 4097);
        That(!ChatRequests.TryParseHyperlink(hugeLength, out _, out var hugeError) && hugeError == ChatHyperlinkError.LengthTooLarge,
            "文本长度 4097 超过 4096 上限拒绝");
        That(!ChatRequests.TryParseHyperlink(body[..30], out _, out var textError) && textError == ChatHyperlinkError.TextOverrun,
            "声明 19 字节文本但缺少 cnt 字节拒绝");
        That(!ChatRequests.TryParseHyperlink(body[..10], out _, out var shortError) && shortError == ChatHyperlinkError.TooShort,
            "不足 11 字节头拒绝");

        // Handler：用真实会话与发送队列跑一次完整分派 → 投递通知 → 0x0C 投影。
        string root = Path.Combine(Path.GetTempPath(), "S5A9-chat-hyperlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("chat-hyperlink.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var owners = new AccountOwners();
            using var owner = owners.Acquire("chat-hyperlink", () => { });
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            repository.Create(owner, new NewCharacter("LinkRole", point, point));
            var session = new GameSession(repository, towns, owner);
            session.Select(0);
            session.MarkReady();
            // 424 头部没有可用的层 id：投递用最近一次 0x0011 上报值，这里模拟先发过一条聊天。
            session.RememberChatLayer(7);

            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var frames = new List<(byte Kind, ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0,
                (kind, opcode, _, data, action) => frames.Add((kind, opcode, data.ToArray(), action)));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            var origin = session.ChatOrigin()!;
            // 同场景一个在线候选：模拟"发送者自己 + 同场景他人"的普通投递范围（这里取 1 人便于计数）。
            var delivery = new Delivery(new ChatRecipient("chat-hyperlink", origin.CharacterId, null, origin.Scene));
            using var log = new ServerLog(Path.Combine(root, "Logs"));
            var handler = new ChatHandler(new ChatService(new Store(), delivery), null, log);
            var commands = new GameCommandRegistry();
            handler.Register(commands);
            commands.Seal();

            async Task DrainAsync()
            {
                await context.Sender.Send(1, byte.MaxValue, [], "test_drain");
                frames.RemoveAll(frame => frame.Action == "test_drain");
            }

            var result = await commands.DispatchAsync(context,
                new Packet(1, (ushort)CmdPacketType.ITEM_HYPERLINK_MESSAGE, body));
            await DrainAsync();
            That(result == CommandResult.Handled && delivery.Deliveries == 1,
                "0x01A8 分派为 Handled 并进入一次普通聊天投递");
            That(delivery.Notice is { Kind: ChatKind.Normal, Layer: 1, Text: "", Speaker: "LinkRole" } &&
                delivery.Notice.TextBytes is { } noticeBytes &&
                noticeBytes.SequenceEqual(expectedText) && delivery.Notice.ItemRecords is { } noticeRecords &&
                noticeRecords.SequenceEqual(expectedRecord),
                "投递通知带 kind=Normal、层 id 7（最近一次聊天上报值）、发送者角色名、逐字节相同的 TextBytes 与 148 字节记录");
            That(session.LastItemHyperlink is { } stored && stored.Text.SequenceEqual(expectedText) &&
                stored.Records.Length == 148,
                "会话保留最近一次链接文本与完整 148 字节记录（供诊断/人工复核复用）");

            ChatProjection.Notice(context, delivery.Notice!);
            await DrainAsync();
            var messageFrame = frames.Single(frame => frame.Action == "chat_hyperlink");
            That(messageFrame.Opcode == (ushort)NotiPacketType.MESSAGE_HYPER_LINK &&
                messageFrame.Body.AsSpan().SequenceEqual(HyperLinkBody(3, 1, "LinkRole", expectedText, expectedRecord)),
                "投影出的 0x172 正文逐字节保留 0x7F 链接文本与 148 字节记录，尾串为发送者角色名，没有 CP936 往返改写");
            That(Convert.ToHexString(messageFrame.Body) ==
                "0301000013000000" + Convert.ToHexString(expectedText) + "01" + Convert.ToHexString(expectedRecord) +
                "080000004C696E6B526F6C65",
                "投影帧十六进制：type=3、层 id=7、长度 19、cnt=1、原样记录、尾串 [8][LinkRole]");

            // 退化：两条链接只带 1 条记录 → 不发 0x172，改发 0x0C 纯文本，日志标记 Degraded。
            byte[] twoLinks = Convert.FromHexString("7F7F5B415D7F7F7F5B425D7F");
            var degradedResult = await commands.DispatchAsync(context,
                new Packet(1, (ushort)CmdPacketType.ITEM_HYPERLINK_MESSAGE, HyperlinkRequestBody(3, twoLinks, expectedRecord)));
            await DrainAsync();
            That(degradedResult == CommandResult.Handled && delivery.Deliveries == 2,
                "记录不足的链接仍进入一次普通投递（由投递侧决定退化）");
            ChatProjection.Notice(context, delivery.Notice!);
            await DrainAsync();
            var degradedFrame = frames.Last(frame => frame.Action == "chat_hyperlink");
            That(degradedFrame.Opcode == (ushort)NotiPacketType.MESSAGE &&
                degradedFrame.Body.AsSpan().SequenceEqual(ChatPackets.MessageBytes(ChatKind.Normal, 1, twoLinks, out _, delivery.Notice!.Speaker)),
                "记录条数少于收尾标记时投影为 0x0C 纯文本（文本原样、无记录段）");

            // 非法正文：四种情况都只丢弃并记日志，绝不 CloseConnection，也不投递。
            int beforeInvalid = delivery.Deliveries;
            foreach (byte[] invalid in new[] { zeroLength, hugeLength, body[..30], body[..10] })
            {
                var rejected = await commands.DispatchAsync(context,
                    new Packet(1, (ushort)CmdPacketType.ITEM_HYPERLINK_MESSAGE, invalid));
                await DrainAsync();
                That(rejected == CommandResult.Handled, "非法 0x01A8 正文只丢弃、不断线（长度 " + invalid.Length + "）");
            }
            That(delivery.Deliveries == beforeInvalid, "非法 0x01A8 正文不进入投递");

            log.Flush();
            using var logFile = new FileStream(Path.Combine(root, "Logs", "server.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var logReader = new StreamReader(logFile);
            string events = logReader.ReadToEnd();
            string[] linkLogs = events.Split('\n')
                .Where(line => line.Contains("\"Kind\":\"item_hyperlink\"", StringComparison.Ordinal)).ToArray();
            string success = linkLogs.Single(line => line.Contains("\"Degraded\":false", StringComparison.Ordinal));
            That(success.Contains("\"Kind\":\"Normal\"", StringComparison.Ordinal) &&
                success.Contains("\"Opcode\":370", StringComparison.Ordinal) &&
                success.Contains("\"TextBytes\":19", StringComparison.Ordinal) &&
                success.Contains("\"Records\":1", StringComparison.Ordinal) &&
                success.Contains("\"MarkerPairs\":1", StringComparison.Ordinal) &&
                success.Contains("\"Recipients\":1", StringComparison.Ordinal) &&
                success.Contains("\"RecordHex\":\"FFFFFFFF70300000FEC99A3B00001B", StringComparison.Ordinal),
                "item_hyperlink 日志记 Opcode=0x172/MarkerPairs/Records/Recipients 与记录前 32 字节 hex，不写完整文本");
            string degraded = linkLogs.Single(line => line.Contains("\"Degraded\":true", StringComparison.Ordinal));
            That(degraded.Contains("\"Kind\":\"Normal\"", StringComparison.Ordinal) &&
                degraded.Contains("\"Opcode\":12", StringComparison.Ordinal) &&
                degraded.Contains("\"MarkerPairs\":2", StringComparison.Ordinal) &&
                degraded.Contains("\"Records\":1", StringComparison.Ordinal) &&
                degraded.Contains("\"Recipients\":1", StringComparison.Ordinal),
                "记录少于标记时日志记 Opcode=0x0C、MarkerPairs=2、Records=1、Degraded=true");
            That(events.Split('\n').Count(line => line.Contains("\"Kind\":\"item_hyperlink_rejected\"", StringComparison.Ordinal)) == 4,
                "四种非法正文各记一条 item_hyperlink_rejected");
            That(!events.Contains("\"Kind\":\"unimplemented\"", StringComparison.Ordinal),
                "0x01A8 已注册，不再产生 unimplemented 日志");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    /// <summary>
    /// 实测 179 字节物品链接正文：<c>[kind=3][u16=3][u32=0][u32 19][19B 文本][cnt=1][148B 记录]</c>；
    /// 记录前 15 字节非零、其余 133 字节为 0。测试侧自行拼装，不调用服务端 Builder 反算。
    /// </summary>
    static byte[] MeasuredHyperlinkBody()
    {
        byte[] body = new byte[179];
        Convert.FromHexString("03030000000000130000007F7F5BB2D0C6C6B5C4C5A3C6A4B6CCBFE35D7F01" +
            "FFFFFFFF70300000FEC99A3B00001B").CopyTo(body, 0);
        return body;
    }

    /// <summary>
    /// GM <c>chat-inject</c>：参数解析、变体→正文（TEST）、按连接投递、层 id 兜底与连接不存在时返回 ERR。
    /// 不需要游戏客户端或 PVF；投递入口用内存列表替代发送泵。
    /// </summary>
    static void ChatInjectCommand()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-chat-inject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("chat-inject.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var owners = new AccountOwners();
            using var owner = owners.Acquire("chat-inject", () => { });
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            repository.Create(owner, new NewCharacter("InjectRole", point, point));
            var session = new GameSession(repository, towns, owner);
            session.Select(0);
            session.MarkReady();
            session.RememberChatLayer(9);

            var parsed = DevelopmentRequest.Parse("chat-inject 7 l2 hello world  again", "chat-inject");
            That(parsed.Action == DevelopmentAction.ChatInject && parsed.Primary == 7 && parsed.Variant == "L2" &&
                parsed.Text == "hello world  again", "chat-inject 解析连接号、大小写不敏感变体与剩余整行文本");
            foreach (string invalid in new[] { "chat-inject 7 L4 TEST", "chat-inject 7 P3F TEST", "chat-inject -1 L1 TEST",
                "chat-inject 7 L1", "chat-inject x L1 TEST", "account chat-inject 7 L1 TEST" })
            {
                bool rejected = false;
                try { DevelopmentRequest.Parse(invalid, "chat-inject"); }
                catch (InvalidDataException) { rejected = true; }
                That(rejected, "chat-inject 参数非法被拒绝: " + invalid);
            }

            using var log = new ServerLog(Path.Combine(root, "Logs"));
            var posted = new List<(ushort Opcode, byte[] Body, string Action)>();
            var target = new ChatProbeConnection(7, session, (opcode, body, action) => posted.Add((opcode, body, action)));
            string expected = "03090000040000005445535400000000";
            string reply = DevelopmentGm.Answer("chat-inject 7 L2 TEST", "chat-inject", repository, owners, _ => null,
                findConnection: id => id == 7 ? target : null, log: log);
            That(reply == "OK injected variant=L2 bytes=16 body=" + expected + " connection=7 layer=9",
                "chat-inject 回复实际正文、连接号与连接最近层 id");
            That(posted.Count == 1 && posted[0].Action == "gm_chat_inject" &&
                posted[0].Opcode == (ushort)NotiPacketType.MESSAGE && Convert.ToHexString(posted[0].Body) == expected,
                "chat-inject 只向目标连接发一帧 gm_chat_inject");

            // P3E：0x03E2 制裁弹窗；正文不带层 id，回复改标实际 opcode。
            string sanctionExpected = "01000000010000000400000054004500530054000400000054455354";
            string sanction = DevelopmentGm.Answer("chat-inject 7 p3e TEST", "chat-inject", repository, owners, _ => null,
                findConnection: id => id == 7 ? target : null, log: log);
            That(sanction == "OK injected variant=P3E bytes=28 body=" + sanctionExpected + " connection=7 opcode=0x03E2",
                "P3E 变体发 0x03E2 并在回复里标出 opcode");
            That(posted.Count == 2 && posted[^1].Opcode == (ushort)NotiPacketType.REVENUE_SANCTION &&
                posted[^1].Action == "gm_chat_inject" && Convert.ToHexString(posted[^1].Body) == sanctionExpected,
                "P3E 正文按 A=1/B=1/UTF-16+CP936 双段文本入队");

            // AAS：0x06AD 弹框；标题固定「系统公告」8 字节，正文 HI。
            string aasExpected = "08000000CFB5CDB3B9ABB8E602000000484900";
            string aas = DevelopmentGm.Answer("chat-inject 7 AAS HI", "chat-inject", repository, owners, _ => null,
                findConnection: id => id == 7 ? target : null, log: log);
            That(aas == "OK injected variant=AAS bytes=19 body=" + aasExpected + " connection=7 opcode=0x06AD",
                "AAS 变体发 0x06AD 并在回复里标出 opcode");
            That(posted.Count == 3 && posted[^1].Opcode == (ushort)NotiPacketType.AAS_TIPS &&
                Convert.ToHexString(posted[^1].Body) == aasExpected,
                "AAS 标题/正文按 CP936 字节数入队，type=0");

            // AAS 超长正文：400 个汉字 = 800 字节 → 截到 600 字节，回复与日志都标记截断。
            string longAas = DevelopmentGm.Answer("chat-inject 7 AAS " + new string('啊', 400), "chat-inject",
                repository, owners, _ => null, findConnection: id => id == 7 ? target : null, log: log);
            That(longAas.EndsWith(" opcode=0x06AD (truncated)", StringComparison.Ordinal) && posted[^1].Body.Length == 9 + 8 + 600,
                "AAS 超长正文按 600 字节截断，回复标记 (truncated)");

            // 0x000C 中文正文：GM 与生产 builder 共用同一段编码，长度按 CP936 字节数。
            string chineseExpected = "0309000008000000B2E2CAD4D6D0CEC400000000";
            string chinese = DevelopmentGm.Answer("chat-inject 7 L2 测试中文", "chat-inject", repository, owners, _ => null,
                findConnection: id => id == 7 ? target : null, log: log);
            That(chinese == "OK injected variant=L2 bytes=20 body=" + chineseExpected + " connection=7 layer=9" &&
                posted[^1].Opcode == (ushort)NotiPacketType.MESSAGE &&
                Convert.ToHexString(posted[^1].Body) == chineseExpected,
                "chat-inject 中文正文按 CP936 字节数编码，与生产 builder 逐字节一致");

            // 0x000C 超长正文：300 个 ASCII 截到 255 字节，回复标记 (truncated)。
            string longChat = DevelopmentGm.Answer("chat-inject 7 L3 " + new string('a', 300), "chat-inject",
                repository, owners, _ => null, findConnection: id => id == 7 ? target : null, log: log);
            That(longChat.EndsWith(" layer=9 (truncated)", StringComparison.Ordinal) &&
                posted[^1].Body.Length == 12 + ChatPackets.MaxTextBytes &&
                BinaryPrimitives.ReadUInt32LittleEndian(posted[^1].Body.AsSpan(4)) == ChatPackets.MaxTextBytes,
                "chat-inject 超长 0x000C 正文按 255 字节截断并在回复标记 (truncated)");

            string missing = DevelopmentGm.Answer("chat-inject 99 L1 TEST", "chat-inject", repository, owners, _ => null,
                findConnection: _ => null, log: log);
            That(missing.StartsWith("ERR ", StringComparison.Ordinal) && missing.Contains("connection 99 not found"),
                "连接不存在返回 ERR 而不是异常");
            That(DevelopmentGm.Answer("chat-inject 7 L1 TEST", "chat-inject", repository, owners, _ => null, log: log)
                == "ERR 聊天帧注入仅支持在线服务", "未装配在线连接入口时返回 ERR");

            using var second = owners.Acquire("chat-inject-2", () => { });
            repository.Create(second, new NewCharacter("InjectAlt", point, point));
            var fallbackSession = new GameSession(repository, towns, second);
            fallbackSession.Select(0);
            fallbackSession.MarkReady();
            var fallbackTarget = new ChatProbeConnection(8, fallbackSession, (_, _, _) => { });
            string fallback = DevelopmentGm.Answer("chat-inject 8 L3 TEST", "chat-inject-2", repository, owners, _ => null,
                findConnection: id => id == 8 ? fallbackTarget : null, log: log);
            That(fallback == "OK injected variant=L3 bytes=16 body=03000000040000005445535400000000 connection=8 layer=0(fallback)",
                "没有聊天层 id 时使用 0 并标注 fallback");

            // GM notice：复用在线连接目录（真实 GameSession），按 all/角色名 定向投 0x06AD 公告弹框。
            var online = new OnlineSessionDirectory();
            online.RegisterConnection(7, session, (opcode, body, action) => posted.Add((opcode, body, action)));
            var postedAlt = new List<(ushort Opcode, byte[] Body, string Action)>();
            online.RegisterConnection(8, fallbackSession, (opcode, body, action) => postedAlt.Add((opcode, body, action)));
            var noticeSender = new GmNoticeSender(online);

            var noticeParsed = DevelopmentRequest.Parse("notice all 维护公告 22:00", "chat-inject");
            That(noticeParsed.Action == DevelopmentAction.Notice && noticeParsed.Name.Length == 0 &&
                noticeParsed.Text == "维护公告 22:00", "notice all 解析为全服广播并保留文本内部空格");
            var namedParsed = DevelopmentRequest.Parse("notice InjectRole 你好", "chat-inject");
            That(namedParsed.Name == "InjectRole" && namedParsed.Text == "你好", "notice 角色名解析为目标角色");
            foreach (string invalid in new[] { "notice InjectRole", "notice all", "account chat-inject notice all 公告" })
            {
                bool rejected = false;
                try { DevelopmentRequest.Parse(invalid, "chat-inject"); }
                catch (InvalidDataException) { rejected = true; }
                That(rejected, "notice 参数非法被拒绝: " + invalid);
            }

            int postedBefore = posted.Count;
            string allReply = DevelopmentGm.Answer("notice all 维护公告：今晚 22 点停机", "chat-inject", repository, owners,
                _ => null, noticeSender: noticeSender, log: log);
            That(allReply == "OK notice all recipients=2 opcode=0x06AD", "GM notice all 广播给全部在线连接并回执接收数");
            var allFrame = posted.Skip(postedBefore).Single(frame => frame.Action == GmNoticeSender.Action);
            var allNotice = NoticeFields(allFrame.Body);
            That(allFrame.Opcode == (ushort)NotiPacketType.AAS_TIPS && allNotice.Title == "系统公告" &&
                allNotice.Body == "维护公告：今晚 22 点停机" && allNotice.Type == 0,
                "GM notice all 每帧都是 0x06AD、标题系统公告、正文 UTF-8");
            var altFrame = postedAlt.Single(frame => frame.Action == GmNoticeSender.Action);
            That(Convert.ToHexString(altFrame.Body) == Convert.ToHexString(allFrame.Body), "全服公告也投给第二个在线连接");

            int postedBeforeOne = posted.Count;
            int altBeforeOne = postedAlt.Count;
            string oneReply = DevelopmentGm.Answer("notice InjectRole 定向公告", "chat-inject", repository, owners,
                _ => null, noticeSender: noticeSender, log: log);
            That(oneReply == "OK notice InjectRole recipients=1 opcode=0x06AD", "GM notice 角色名只投该角色连接并回执");
            var oneFrame = posted.Skip(postedBeforeOne).Single(frame => frame.Action == GmNoticeSender.Action);
            That(NoticeFields(oneFrame.Body).Body == "定向公告" && postedAlt.Count == altBeforeOne,
                "定向公告只发目标角色连接，不波及其他在线连接");

            string missingNotice = DevelopmentGm.Answer("notice Nobody 公告", "chat-inject", repository, owners,
                _ => null, noticeSender: noticeSender, log: log);
            That(missingNotice.StartsWith("ERR ", StringComparison.Ordinal) && missingNotice.Contains("不在线或不存在"),
                "GM notice 角色不在线返回 ERR 而不是发帧");
            string offlineNotice = DevelopmentGm.Answer("notice all 公告", "chat-inject", repository, owners, _ => null, log: log);
            That(offlineNotice == "ERR 公告仅支持在线服务", "未装配在线连接目录时 notice 返回 ERR");

            string longNotice = DevelopmentGm.Answer("notice all " + new string('啊', 400), "chat-inject", repository, owners,
                _ => null, noticeSender: noticeSender, log: log);
            That(longNotice.EndsWith(" opcode=0x06AD (truncated)", StringComparison.Ordinal) &&
                posted[^1].Body.Length == 9 + 12 + 600, "notice 超长正文按 600 字节截断并在回执标注 (truncated)");

            log.Flush();
            using var logFile = new FileStream(Path.Combine(root, "Logs", "server.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var logReader = new StreamReader(logFile);
            string events = logReader.ReadToEnd();
            That(events.Contains("\"Kind\":\"chat_inject\"") && events.Contains("\"Variant\":\"L2\"") &&
                events.Contains("\"Body\":\"" + expected + "\""), "chat_inject 日志记录 Connection/Variant/Body 十六进制");
            That(events.Contains("\"Variant\":\"P3E\"") && events.Contains("\"Opcode\":\"0x03E2\"") &&
                events.Contains("\"Variant\":\"AAS\"") && events.Contains("\"Opcode\":\"0x06AD\"") &&
                events.Contains("\"Truncated\":true"),
                "chat_inject 日志记录新变体的 opcode 与截断标记");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    /// <summary>
    /// GM <c>inject-raw</c>：任意 opcode + 任意正文原样投一帧。覆盖十六进制/opcode 解析边界（大小写、0x 前缀、
    /// 嵌入空格、<c>-</c> 空 body）、奇数长度/非法字符/超长/opcode 越界的明确 ERR、连接不存在或未就绪的 ERR，
    /// 以及发送字节与输入逐字节一致。投递入口用内存列表替代发送泵，不需要客户端或 PVF。
    /// </summary>
    static void InjectRawCommand()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-inject-raw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("inject-raw.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new TownDock(0, [], []), [], new Dictionary<uint, uint[]>());
            var owners = new AccountOwners();
            using var owner = owners.Acquire("inject-raw", () => { });
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            repository.Create(owner, new NewCharacter("RawInject", point, point));
            var session = new GameSession(repository, towns, owner);
            session.Select(0);
            session.MarkReady();

            // 解析：十进制/0x 十六进制 opcode、嵌入空格的 hex、'-' 空 body、4096 字节上限。
            var parsed = DevelopmentRequest.Parse("inject-raw 7 12 0304C8F4C4E3 00000000", "inject-raw");
            That(parsed.Action == DevelopmentAction.InjectRaw && parsed.Primary == 7 && parsed.RawOpcode == 0x0C &&
                Convert.ToHexString(parsed.RawBody) == "0304C8F4C4E300000000",
                "inject-raw 解析连接号、十进制 opcode 与含空格 hex 正文");
            var opcodeHex = DevelopmentRequest.Parse("inject-raw 7 0x06ad 08000000CFB5CDB3B9ABB8E6", "inject-raw");
            That(opcodeHex.RawOpcode == 0x06AD && opcodeHex.RawBody.Length == 12,
                "inject-raw 接受 0x 前缀十六进制 opcode（大小写不敏感）");
            That(DevelopmentRequest.Parse("inject-raw 7 12 -", "inject-raw").RawBody.Length == 0,
                "inject-raw 用 - 表示空 body");
            That(DevelopmentRequest.Parse("inject-raw 7 65535 " + new string('A', 8192), "inject-raw").RawBody.Length
                == DevelopmentRequest.RawInjectBodyMaxBytes, "inject-raw 接受 4096 字节上限正文");

            using var log = new ServerLog(Path.Combine(root, "Logs"));
            var posted = new List<(ushort Opcode, byte[] Body, string Action)>();
            var target = new ChatProbeConnection(7, session, (opcode, body, action) => posted.Add((opcode, body, action)));
            Func<int, ChatProbeConnection?> find = id => id == 7 ? target : null;

            // 非法输入只在命令层回 ERR（不抛异常），不发帧。
            foreach ((string line, string hint) in new[]
            {
                ("inject-raw 7 12 030", "偶数"), ("inject-raw 7 12 03G4", "0-9a-f"),
                ("inject-raw 7 12 0x03", "0-9a-f"), ("inject-raw 7 12 " + new string('A', 8194), "4096"),
                ("inject-raw 7 12", "用法"), ("inject-raw -1 12 03", "用法"),
                ("inject-raw 7 65536 03", "65535"), ("inject-raw 7 0x10000 03", "65535"),
                ("inject-raw 7 -1 03", "opcode"), ("inject-raw 7 0x 03", "opcode"),
                ("account inject-raw inject-raw 7 12 03", "用法")
            })
            {
                string rejected = DevelopmentGm.Answer(line, "inject-raw", repository, owners, _ => null,
                    findConnection: find, log: log);
                That(rejected.StartsWith("ERR ", StringComparison.Ordinal) && rejected.Contains(hint, StringComparison.Ordinal),
                    "inject-raw 非法输入返回 ERR 并说明原因: " + line);
            }
            That(posted.Count == 0, "inject-raw 非法输入不发帧");

            // 正常发送：NOTI 0x000C + 命令行给的 10 字节正文，逐字节原样入队。
            string expectedHex = "0304C8F4C4E300000000";
            byte[] expected = Convert.FromHexString(expectedHex);
            string reply = DevelopmentGm.Answer("inject-raw 7 12 " + expectedHex, "inject-raw", repository, owners,
                _ => null, findConnection: find, log: log);
            That(reply == "OK inject-raw opcode=0x000C bytes=" + expected.Length + " hex=" + expectedHex + " connection=7",
                "inject-raw 回复实际 opcode、字节数与规范化 hex");
            That(posted.Count == 1 && posted[0].Action == "inject_raw" && posted[0].Opcode == 0x0C &&
                posted[0].Body.SequenceEqual(expected), "inject-raw 只向目标连接逐字节原样发一帧 NOTI");

            // 任意 opcode：0x06AD 弹框字节原样重放（服务端不按 opcode 重新构造正文）。
            byte[] replayed = Convert.FromHexString("08000000CFB5CDB3B9ABB8E602000000484900");
            string replay = DevelopmentGm.Answer("inject-raw 7 0x06AD " + Convert.ToHexString(replayed), "inject-raw",
                repository, owners, _ => null, findConnection: find, log: log);
            That(replay == "OK inject-raw opcode=0x06AD bytes=" + replayed.Length + " hex=" + Convert.ToHexString(replayed) +
                " connection=7" && posted[^1].Opcode == 0x06AD && posted[^1].Body.SequenceEqual(replayed),
                "inject-raw 用 0x06AD 原样重放弹框字节");

            // 空 body：仍发一帧 0 字节 NOTI，回复用 '-' 标出，便于区分"没发"和"发空"。
            string emptyReply = DevelopmentGm.Answer("inject-raw 7 12 -", "inject-raw", repository, owners,
                _ => null, findConnection: find, log: log);
            That(emptyReply == "OK inject-raw opcode=0x000C bytes=0 hex=- connection=7" && posted[^1].Body.Length == 0,
                "inject-raw 的 - 发送 0 字节正文");

            // 上限正文：4096 字节整帧透传，不被截断或改写。
            string maxReply = DevelopmentGm.Answer("inject-raw 7 12 " + new string('A', 8192), "inject-raw", repository,
                owners, _ => null, findConnection: find, log: log);
            That(maxReply.Contains("bytes=4096 ", StringComparison.Ordinal) &&
                posted[^1].Body.Length == DevelopmentRequest.RawInjectBodyMaxBytes && posted[^1].Body.All(b => b == 0xAA),
                "inject-raw 允许并原样发送 4096 字节正文");

            int sent = posted.Count;
            string missing = DevelopmentGm.Answer("inject-raw 99 12 03", "inject-raw", repository, owners,
                _ => null, findConnection: find, log: log);
            That(missing == "ERR connection 99 not found", "inject-raw 连接不存在返回 ERR 而不是异常");
            using var idleOwner = owners.Acquire("inject-raw-idle", () => { });
            var idleSession = new GameSession(repository, towns, idleOwner);
            var idle = new ChatProbeConnection(8, idleSession, (_, _, _) => { });
            string notReady = DevelopmentGm.Answer("inject-raw 8 12 03", "inject-raw", repository, owners, _ => null,
                findConnection: id => id == 8 ? idle : null, log: log);
            That(notReady == "ERR connection 8 not ready", "inject-raw 未进入世界的连接返回 ERR");
            That(DevelopmentGm.Answer("inject-raw 7 12 03", "inject-raw", repository, owners, _ => null, log: log)
                == "ERR 原始包体注入仅支持在线服务", "未装配在线连接入口时 inject-raw 返回 ERR");
            That(posted.Count == sent, "inject-raw 连接不存在或未就绪时不发帧");

            log.Flush();
            using var logFile = new FileStream(Path.Combine(root, "Logs", "server.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var logReader = new StreamReader(logFile);
            string events = logReader.ReadToEnd();
            That(events.Split('\n').Count(line => line.Contains("\"Kind\":\"inject_raw\"", StringComparison.Ordinal)) == 4,
                "inject-raw 每次成功发送只记一条 inject_raw 日志");
            That(events.Contains("\"Connection\":7", StringComparison.Ordinal) &&
                events.Contains("\"Opcode\":\"0x000C\"", StringComparison.Ordinal) &&
                events.Contains("\"Body\":\"" + expectedHex + "\"", StringComparison.Ordinal),
                "inject_raw 日志记录连接号、opcode 与正文十六进制");
            That(!events.Contains("\"Kind\":\"unimplemented\"", StringComparison.Ordinal),
                "inject-raw 不产生 unimplemented 噪音日志");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    /// <summary>CMD 0x0011 正文 = <c>[u8 type][u16 层 id][u32 arg][u32 textLen][CP936 文本]</c>，测试侧自己编码。</summary>
    static byte[] SendBody(byte type, ushort layer, uint arg, string text)
    {
        byte[] bytes = ChatText.Encoding.GetBytes(text);
        byte[] body = new byte[ChatRequests.HeaderLength + bytes.Length];
        body[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), layer);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), arg);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), checked((uint)bytes.Length));
        bytes.CopyTo(body, ChatRequests.HeaderLength);
        return body;
    }

    /// <summary>
    /// <c>NOTI 0x0076</c> 正文 = <c>[u8 type][u8 B][u8 C][u8 D][u16 E]</c> +
    /// 三段 <c>[u32 CP936 字节数][CP936 字节]</c>。测试侧按文档布局自行编码，不调用服务端 Builder；
    /// <paramref name="tag"/> 用生产占位 <c>X</c>。
    /// </summary>
    static byte[] MegaphoneBody(byte type, string speaker, string text, string tag = "X")
    {
        byte[] speakerBytes = ChatText.Encoding.GetBytes(speaker);
        byte[] textBytes = ChatText.Encoding.GetBytes(text);
        byte[] tagBytes = ChatText.Encoding.GetBytes(tag);
        byte[] body = new byte[6 + 3 * sizeof(uint) + speakerBytes.Length + textBytes.Length + tagBytes.Length];
        body[0] = type;
        int offset = WriteMegaphoneString(body, 6, speakerBytes);
        offset = WriteMegaphoneString(body, offset, textBytes);
        WriteMegaphoneString(body, offset, tagBytes);
        return body;
    }

    static int WriteMegaphoneString(byte[] body, int offset, byte[] bytes)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), (uint)bytes.Length);
        bytes.CopyTo(body, offset + sizeof(uint));
        return offset + sizeof(uint) + bytes.Length;
    }
}
