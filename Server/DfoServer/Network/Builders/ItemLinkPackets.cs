using System.Buffers.Binary;
using DfoServer.Game.Chat;

namespace DfoServer.Network.Builders;

/// <summary>
/// 聊天引用370/371/372及原通道降级；下述原码布局说明针对普通聊天370。
/// <para>
/// 依据（反汇编 <c>0x2E31740</c>，见 <c>Docs/协议/聊天.md</c> 第 7 节）：handler 读取
/// <c>ReadU8 type(&lt;0x45)</c> → <c>ReadU16 layer</c> → <c>ReadU8 v(&lt;0xFA)</c> →
/// 文本壳 <c>0x3BE26C0</c>（内部 <c>ReadU32 len</c> + <c>ReadBytes</c>，<c>0 &lt; len &lt; 0x100</c>）→
/// <c>0x2E1BD90</c>（<c>ReadU8 cnt</c> + <c>cnt × ReadBytes(0x94)</c>）→ 文本壳 <c>0x3BE26C0</c>
/// （再次 <c>ReadU32 len2</c> + <c>ReadBytes</c>，max <c>0x11</c> wchar，读不到回落 <c>0x27F</c>）→
/// <c>AddMessage 0x2E31450</c>。
/// 因此正文布局是
/// <c>[u8 type][u16 layer][u8 v=0][u32 文本字节数][CP936 文本][u8 cnt][cnt × 148 字节记录][u32 尾串字节数][尾串 CP936]</c>。
/// 尾串是 handler 读完记录后再次调用 <c>0x3BE26C0</c>（max <c>0x11</c> wchar）读取的必填段，
/// 内容为**发言人名字**：handler 存进 <c>msg+0x44</c>，<c>0x1FC19C0</c> 再深拷进 link run 的 <c>+0xB4</c>，
/// 客户端渲染时用它补行首「角色名：」。漏发会让客户端越界读 4 字节并回落成串表 <c>0x27F</c>「信息长度有误」；
/// 发空格等占位则整行显示为无名（实测）。
/// </para>
/// <para>
/// 与 <c>NOTI 0x000C</c> 的区别：<c>0x0C</c> 正文在 <c>u32 长度</c> 后直接是文本；<c>0x172</c> 在文本后多
/// <c>u8 cnt</c>、<c>cnt × 148</c> 字节记录与尾串。入站 <c>CMD 0x01A8</c> 的头部是
/// <c>[kind][u16][u32 arg][u32 len]</c>，比 <c>0x172</c> 多一个 <c>arg u32</c>，所以不能把 <c>0x1A8</c>
/// 正文直接当 <c>0x172</c> 发送，必须按本类的布局重写。
/// </para>
/// <para>
/// <c>0x7F</c> 与记录同序配对（客户端布局器 <c>0x2592818</c>）：遇到 <c>0x7F</c> 进入链接态并消费下一个
/// 宽字符；链接态中再遇到 <c>0x7F</c> 就关闭链接并消费一条记录（<c>0x1FC19C0</c> 取暂存向量）。
/// 客户端自己生成的链接块是 <c>7F 7F [物品名] 7F</c>，即 1 个收尾标记对应 1 条记录；记录 <c>+0x04</c>
/// 是物品 id（样本 <c>70 30 00 00</c> = 12400），由客户端 <c>0x2E615B0</c> 本地解析，服务端不改写记录。
/// </para>
/// </summary>
internal static class ItemLinkPackets
{
    /// <summary>文本之前的固定头：<c>[u8 type][u16 layer][u8 v][u32 文本字节数]</c>；文本之后还有 1 字节 <c>cnt</c>。</summary>
    public const int HeaderLength = 8;

    /// <summary>单条物品记录长度（实测 0x94 = 148 字节，原样转发）。</summary>
    public const int RecordLength = 148;

    /// <summary>文本字节数上限：客户端文本壳只在 <c>0 &lt; len &lt; 0x100</c> 时读取。</summary>
    public const int MaxTextBytes = 0xFF;

    /// <summary>客户端链接标记（宽字符 <c>0x7F</c>；CP936 中不会作为双字节字符的尾字节）。</summary>
    public const byte LinkMarker = 0x7F;

    /// <summary>记录数上限由 <c>u8 cnt</c> 决定。</summary>
    public const int MaxRecords = 0xFF;

    /// <summary>
    /// 记录之后的尾串上限（wchar 数）：handler 以 <c>max = 0x11</c> 调用文本壳
    /// <c>0x3BE26C0</c>（内部 <c>ReadU32 len + ReadBytes</c>），超出会让客户端读不到。
    /// </summary>
    public const int TailMaxChars = 0x10;

    /// <summary>03BE25F0要求尾串小于17字节。</summary>
    public const int TailMaxBytes = 0x10;

    /// <summary>
    /// 尾串（发言人名字）的回落值：单字节 <c>?</c>（CP936 <c>0x3F</c>，<c>len2 = 1</c>）。
    /// 该字段必须非空（空串会让客户端读不到、回落「信息长度有误」），名字为空或截空时用它占位。
    /// </summary>
    public const string FallbackSpeaker = "?";

    /// <summary>历史v字段为来源服务器分组，正式投递不写死为0。</summary>

    /// <summary>
    /// 一次投递的最终形态：<paramref name="HyperLink"/> 为 true 时 <paramref name="Body"/> 是 <c>0x172</c> 正文；
    /// 为 false 表示退化到 <c>0x0C</c> 纯文本（正文同样是 <see cref="ChatPackets.MessageBytes"/> 的布局）。
    /// <paramref name="Records"/> 是输入里长度合法的记录条数，<paramref name="MarkerPairs"/> 是实际写出的链接条数
    /// （也等于写出的记录条数）；<paramref name="Degraded"/> 表示走了纯文本回退，
    /// <paramref name="MarkersStripped"/> 表示截断造成标记不成对、文本里的 <c>0x7F</c> 已被剔除。
    /// </summary>
    public readonly record struct Plan(NotiPacketType Opcode, byte[] Body, int Records, int MarkerPairs,
        bool Degraded, bool MarkersStripped, bool Truncated)
    {
        public bool HyperLink => Opcode is NotiPacketType.MESSAGE_HYPER_LINK or
            NotiPacketType.MESSAGE_OTHER_CHANNEL_HYPER_LINK or NotiPacketType.MEGAPHONE_MSG_HYPER_LINK;
    }

    /// <summary>
    /// 构造 <c>NOTI 0x0172</c> 正文，布局严格按
    /// <c>[type][layer][v=0][u32 文本字节数][文本][cnt][记录][u32 尾串字节数][尾串]</c>。
    /// <para>
    /// <paramref name="textBytes"/> 是客户端给出的 CP936 字节，<b>逐字节原样</b>写入，不做解码再编码；
    /// 超过 <see cref="MaxTextBytes"/> 时按 CP936 字符边界截断。截断若让 <c>0x7F</c> 标记不成对，整段退化为
    /// 无链接纯文本（剔除全部 <c>0x7F</c>，<c>cnt = 0</c>），避免客户端按不存在的记录越界消费。
    /// <paramref name="records"/> 中长度不等于 <see cref="RecordLength"/> 的条目被丢弃；剩余条数不足链接条数时
    /// 抛 <see cref="ArgumentException"/>（调用方应改用 <see cref="Design"/> 走 <c>0x0C</c> 退化路径），
    /// 绝不写出 <c>cnt</c> 与实际记录数不一致的非法帧。
    /// </para>
    /// <para>
    /// <paramref name="speaker"/> 是发言人名字（记录之后的必填尾串，handler 读完记录后还会再调一次
    /// <c>0x3BE26C0</c>，max 为 <c>0x11</c> wchar）；由 <see cref="SpeakerBytes"/> 编码并按
    /// <c>0x11</c> wchar / <c>0x21</c> 字节上限截断，空名字回落 <see cref="FallbackSpeaker"/>。
    /// 漏发会让客户端越界读 4 字节、把 <c>msg+0x44</c> 回落成串表 <c>0x27F</c>「信息长度有误」；
    /// 发占位空格则行首不显示角色名。
    /// </para>
    /// <para>
    /// <paramref name="channel"/> 只用于 <paramref name="kindType"/> 为 0 时按显示频道推导 type；正常调用都显式给
    /// <paramref name="kindType"/>（普通/队伍/悄悄话 = 3、公会/攻坚队 = 6）。
    /// </para>
    /// </summary>
    public static byte[] Build(ChatKind channel, ushort layer, byte kindType, string speaker,
        ReadOnlyMemory<byte> textBytes, IReadOnlyList<byte[]> records)
    {
        byte type = kindType != 0 ? kindType : ChatWire.TypeOf(channel);
        byte[] text = Truncate(textBytes.Span, out _);
        if (!CountLinkPairs(text, out int pairs))
        {
            // 截断留下未闭合的链接：剔除标记后作为无链接纯文本，cnt=0，避免客户端多消费一条记录。
            text = RemoveMarkers(text);
            pairs = 0;
        }
        if (text.Length == 0)
            throw new ArgumentException("0x172 文本长度必须为 1..255 字节", nameof(textBytes));
        if (pairs > MaxRecords)
            throw new ArgumentException("链接条数超过 u8 cnt 上限", nameof(textBytes));

        List<byte[]> valid = [];
        foreach (byte[] record in records)
        {
            if (record.Length == RecordLength)
                valid.Add(record);
        }
        if (valid.Count < pairs)
            throw new ArgumentException("长度合法的物品记录少于链接标记条数", nameof(records));
        return Write(type, layer, text, valid, pairs, speaker);
    }

    /// <summary>
    /// 投递决策：能配对且记录够用时返回 <c>0x172</c>；否则返回 <c>0x0C</c> 纯文本（现有"看得见但不可点"行为）。
    /// <para>
    /// 退化条件：文本里没有完整链接（<c>MarkerPairs == 0</c>）、长度合法的记录少于链接条数、或截断后标记不成对。
    /// 最后一种先把文本里的 <c>0x7F</c> 全部剔除，再走 <c>0x0C</c>；其余退化保留原始字节（含不可见标记）。
    /// </para>
    /// </summary>
    public static Plan Design(ChatKind kind, ushort layer, string speaker, ReadOnlyMemory<byte> textBytes,
        byte[]? records, byte server = 0)
    {
        var content = Prepare(textBytes.Span, records, MaxTextBytes);
        if (content.Degraded)
        {
            byte[] fallback = ChatPackets.MessageBytes(kind, layer, content.Text, out _, speaker, server);
            return Result(content, NotiPacketType.MESSAGE, fallback);
        }

        byte[] body = Write(ChatWire.TypeOf(kind), layer, content.Text, content.Records, content.Pairs, speaker, server);
        return Result(content, NotiPacketType.MESSAGE_HYPER_LINK, body);
    }

    public static Plan DesignNamed(NamedChatMessage message, ReadOnlySpan<byte> text, byte[]? records)
    {
        var content = Prepare(text, records, MaxTextBytes);
        if (content.Degraded)
            return Result(content, NotiPacketType.MESSAGE_OTHER_CHANNEL, ChatPackets.NamedMessage(message, content.Text));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        ChatPackets.WriteNamedHeader(writer, message, content.Text);
        WriteRecords(writer, content);
        PacketEncoding.WriteString(writer, ChatText.Truncate(message.AdventureName, 16, out _));
        return Result(content, NotiPacketType.MESSAGE_OTHER_CHANNEL_HYPER_LINK, stream.ToArray());
    }

    public static Plan DesignMegaphone(ChatKind kind, MegaphoneText message, ReadOnlySpan<byte> text, byte[]? records)
    {
        var content = Prepare(text, records, MegaphonePackets.TextMaxBytes);
        if (content.Degraded)
            return Result(content, NotiPacketType.MEGAPHONE_MSG, MegaphonePackets.BuildBytes(kind, message, content.Text));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(MegaphonePackets.TypeOf(kind));
        writer.Write(message.Channel.ServerIndex);
        writer.Write((byte)0);
        writer.Write(message.Channel.Number);
        writer.Write(message.Actor);
        PacketEncoding.WriteString(writer, ChatText.Truncate(message.Speaker, MegaphonePackets.SpeakerMaxBytes, out _));
        writer.Write(checked((uint)content.Text.Length));
        writer.Write(content.Text);
        WriteRecords(writer, content);
        PacketEncoding.WriteString(writer, MegaphonePackets.Tag);
        return Result(content, NotiPacketType.MEGAPHONE_MSG_HYPER_LINK, stream.ToArray());
    }

    readonly record struct Content(byte[] Text, List<byte[]> Records, int Pairs, bool Degraded, bool Stripped, bool Truncated);

    static Content Prepare(ReadOnlySpan<byte> text, byte[]? records, int maximum)
    {
        int length = ChatEmoticonRules.ByteBoundary(text, maximum);
        bool truncated = length < text.Length;
        byte[] clipped = text[..length].ToArray();
        bool paired = CountLinkPairs(clipped, out int pairs);
        if (!paired)
        {
            clipped = RemoveMarkers(clipped);
            pairs = 0;
        }
        var valid = ValidRecords(records);
        return new(clipped, valid, pairs, pairs == 0 || valid.Count < pairs || clipped.Length == 0, !paired, truncated);
    }

    static Plan Result(Content content, NotiPacketType opcode, byte[] body) =>
        new(opcode, body, content.Records.Count, content.Pairs, content.Degraded, content.Stripped, content.Truncated);

    static void WriteRecords(BinaryWriter writer, Content content)
    {
        writer.Write(checked((byte)content.Pairs));
        for (int index = 0; index < content.Pairs; index++) writer.Write(content.Records[index]);
    }

    /// <summary>
    /// 按客户端布局器的语义数链接条数：<c>0x7F</c> 进入链接态并消费下一个字符；链接态中的下一个
    /// <c>0x7F</c> 关闭链接并消费一条记录。返回 false 表示文本结束时仍有未闭合的链接（标记不成对），
    /// 此时 <paramref name="pairs"/> 只是已经闭合的条数，不能作为下发依据。
    /// </summary>
    public static bool CountLinkPairs(ReadOnlySpan<byte> text, out int pairs)
    {
        pairs = 0;
        bool open = false;
        // 02591E30在整段首位跳过格式标志7F；它本身不是一条物品链接的开头。
        int at = !text.IsEmpty && text[0] == LinkMarker ? 1 : 0;
        while (at < text.Length)
        {
            if (text[at] != LinkMarker)
            {
                at++;
                continue;
            }
            if (open)
            {
                pairs++;
                open = false;
                at++;
                continue;
            }
            open = true;
            at++;
            if (at >= text.Length)
                return false;
            // 客户端在进入链接态时会连带消费并跳过标记后的那一个宽字符；这里按 CP936 字符宽度同样跳过。
            at += CharWidth(text, at);
        }
        return !open;
    }

    /// <summary>丢弃长度不等于 <see cref="RecordLength"/> 的记录（含尾部不完整记录），其余保持原顺序。</summary>
    static List<byte[]> ValidRecords(byte[]? records)
    {
        List<byte[]> valid = [];
        if (records is null)
            return valid;
        for (int offset = 0; offset + RecordLength <= records.Length; offset += RecordLength)
        {
            byte[] record = new byte[RecordLength];
            records.AsSpan(offset, RecordLength).CopyTo(record);
            valid.Add(record);
        }
        return valid;
    }

    /// <summary>
    /// 写 <c>0x172</c> 正文；<paramref name="used"/> 必须等于实际写出的记录条数（<c>u8 cnt</c>）。
    /// 记录之后追加 <c>[u32 尾串字节数][尾串 CP936]</c>：handler 在记录后固定再读一次
    /// <c>0x3BE26C0</c>（max <c>0x11</c> wchar），<paramref name="speaker"/> 按 <see cref="SpeakerBytes"/>
    /// 编码（必填非空、<c>1..0x21</c> 字节）。
    /// </summary>
    static byte[] Write(byte type, ushort layer, ReadOnlySpan<byte> text, List<byte[]> records, int used,
        string speaker, byte server = 0)
    {
        byte[] tail = SpeakerBytes(speaker);

        byte[] body = new byte[HeaderLength + text.Length + 1 + used * RecordLength + sizeof(uint) + tail.Length];
        body[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), layer);
        body[3] = server;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)text.Length);
        text.CopyTo(body.AsSpan(HeaderLength));
        int offset = HeaderLength + text.Length;
        body[offset++] = (byte)used;
        for (int index = 0; index < used; index++)
        {
            records[index].CopyTo(body, offset);
            offset += RecordLength;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), (uint)tail.Length);
        offset += sizeof(uint);
        tail.CopyTo(body, offset);
        return body;
    }

    /// <summary>
    /// 将来源名字编码为CP936尾串；容量按字节校验。
    /// 最多16字节，按字符边界截断，不切开汉字。
    /// 名字为空或截空时回落 <see cref="FallbackSpeaker"/>（单字节 <c>0x3F</c>）：该字段必填非空。
    /// </summary>
    public static byte[] SpeakerBytes(string? speaker)
    {
        string name = speaker ?? "";
        if (name.Length > TailMaxChars)
            name = name[..TailMaxChars];
        name = ChatText.Truncate(name, TailMaxBytes, out _);
        return name.Length == 0 ? ChatText.Encoding.GetBytes(FallbackSpeaker) : ChatText.Encoding.GetBytes(name);
    }

    /// <summary>按 CP936 字节上限截断，且不切开双字节字符；<paramref name="truncated"/> 表示确有截断。</summary>
    static byte[] Truncate(ReadOnlySpan<byte> text, out bool truncated)
    {
        int length = ChatEmoticonRules.ByteBoundary(text, MaxTextBytes);
        truncated = length < text.Length;
        return text[..length].ToArray();
    }

    /// <summary>剔除全部 <c>0x7F</c>（只删标记字节，不解码/重编码，保留其余 CP936 字节）。</summary>
    static byte[] RemoveMarkers(ReadOnlySpan<byte> text)
    {
        bool keepFormat = !text.IsEmpty && text[0] == LinkMarker && text.Contains((byte)0x1e);
        using var output = new MemoryStream(text.Length);
        for (int at = 0; at < text.Length; at++)
            if (text[at] != LinkMarker || at == 0 && keepFormat) output.WriteByte(text[at]);
        return output.ToArray();
    }

    /// <summary>CP936 下一个字符的字节宽度：lead byte 且后面还有字节时是 2，否则 1。</summary>
    static int CharWidth(ReadOnlySpan<byte> text, int at) =>
        text[at] is >= 0x81 and <= 0xFE && at + 1 < text.Length ? 2 : 1;
}
