using System.Buffers.Binary;
using DfoServer.Game.Chat;

namespace DfoServer.Network.Builders;

/// <summary>
/// 聊天行下行的固定正文。
/// 依据（反汇编 + 实机收包，见 <c>Docs/协议/聊天.md</c>）：
/// <list type="bullet">
/// <item><c>NOTI 0x000C MESSAGE</c>（handler <c>0x23D0BA0</c>）读 <c>u8 type</c>、<c>u16 层 id</c>、<c>u8 v</c>，
/// 之后的 <c>0x023D0C5A call 0x3BE26C0</c> 是文本包装函数：内部先 <c>0x3BE25F0 ReadU32</c> 取字节长度，
/// <c>0 &lt; len &lt; 0x100</c> 时 <c>ReadBytes</c> 读 len 字节并按 CP936 转宽写入正文槽，否则回落到串表
/// <c>0x27F</c>「信息长度有误」。正文后仍有一条发言人DSTR，不能省略其长度头，
/// 长度按<b>字节</b>、上限 <c>0xFF</c>；<c>len == 0</c> 表示不带正文。
/// 聊天过滤依据客户端设置，u16是发言人引用，不要求强制等于接收者编号。</item>
/// <item>正文首字节不是入站 <see cref="ChatKind"/> 的原值，而是显示 type：
/// 队伍 = <c>2</c>，普通/悄悄话 = <c>3</c>，公会 = <c>6</c>（映射见 <see cref="ChatWire"/>；
/// 蓝/绿/紫 kind 在「普通」页签不被渲染，是实测的客户端限制）。</item>
/// <item><c>NOTI 0x0076 MEGAPHONE_MSG</c> 是「喇叭」页签通道（见 <see cref="MegaphonePackets"/>），
/// 不在这里构造：本文件只负责聊天行 <c>0x000C</c>。</item>
/// </list>
/// </summary>
internal static class ChatPackets
{
    // 023B0210：正文后必读附加名字串（023B0365），type36再读接收端的会话对象（023B0386）。
    public static byte[] NamedMessage(NamedChatMessage message) => NamedMessage(message, ChatText.Encoding.GetBytes(message.Text));

    public static byte[] NamedMessage(NamedChatMessage message, ReadOnlySpan<byte> text)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteNamedHeader(writer, message, text);
        PacketEncoding.WriteString(writer, ChatText.Truncate(message.AdventureName, 16, out _));
        // 65/type36在附加名字后还读取会话对象；371没有这一字段。
        if (message.Mode == ChatMessageType.OneToOne) PacketEncoding.WriteString(writer, message.Name);
        return stream.ToArray();
    }

    internal static void WriteNamedHeader(BinaryWriter writer, NamedChatMessage message, ReadOnlySpan<byte> text)
    {
        if (message.Mode is not (ChatMessageType.Whisper or ChatMessageType.WhisperReply or
            ChatMessageType.OneToOne or ChatMessageType.Guild))
            throw new ArgumentOutOfRangeException(nameof(message),"尚未核定的65类型不能套用现有载荷");
        writer.Write((byte)message.Mode);
        writer.Write(message.Status);
        if (message.Mode == ChatMessageType.Guild)
        {
            writer.Write(message.GuildFlag);
            writer.Write(message.GuildColorFlag);
        }
        PacketEncoding.WriteString(writer, message.Name);
        writer.Write(message.Server);
        writer.Write(checked((uint)text.Length));
        writer.Write(text);
    }

    /// <summary><c>0x000C</c> 正文头长度：<c>[u8 type][u16 层 id][u8 v][u32 字节长度]</c>。</summary>
    public const int HeaderLength = 8;

    /// <summary>
    /// 正文长度上限（字节）。客户端只在 <c>0 &lt; len &lt; 0x100</c> 时读文本，达到 <c>0x100</c> 会显示占位串
    /// 「信息长度有误」，因此绝不能写到 <c>0x100</c>。
    /// </summary>
    public const int MaxTextBytes = 0xFF;

    /// <summary>
    /// 历史v字段是来源服务器分组，正式投递写真实值；0仅保留为无来源诊断的兼容值。
    /// 不用于控制头顶气泡。
    /// </summary>

    /// <summary>不带正文的 <c>NOTI 0x000C</c>，正文和发言人均保留零长度头。</summary>
    public static byte[] Message(ChatKind kind, ushort layer) => Message(kind, layer, null, out _);

    /// <summary><c>NOTI 0x000C</c>：固定头、正文DSTR、发言人DSTR；此重载的发言人为空。</summary>
    public static byte[] Message(ChatKind kind, ushort layer, string? text) => Message(kind, layer, text, out _);
    public static byte[] Message(ChatKind kind, ushort layer, string? text, string speaker, byte server = 0) => Message(kind, layer, text, out _, speaker, server);

    /// <summary>
    /// <c>NOTI 0x000C</c> 正文的唯一编码入口。长度取 CP936 编码后的<b>字节数</b>，
    /// 超过 <see cref="MaxTextBytes"/> 时复用 <see cref="ChatText.Truncate(string,int,System.Text.Encoding,out bool)"/>
    /// 按字符边界截断（不切半个汉字），并把 <paramref name="truncated"/> 置 true。
    /// </summary>
    public static byte[] Message(ChatKind kind, ushort layer, string? text, out bool truncated, string speaker = "", byte server = 0)
    {
        byte[] bytes = EncodeText(text, out truncated);
        return Body(kind, layer, bytes, speaker, server);
    }

    /// <summary>
    /// 已编码字节正文（<c>CMD 0x01A8</c> 物品链接）：原样写进 <c>0x000C</c>，不做 CP936 解码再编码，
    /// 因此 <c>0x7F</c> 标记与客户端给出的字节序列保持不变。超过 <see cref="MaxTextBytes"/> 时按
    /// CP936 双字节边界回退（不切开半个汉字），<paramref name="truncated"/> 表示确有截断。
    /// </summary>
    public static byte[] MessageBytes(ChatKind kind, ushort layer, ReadOnlySpan<byte> text, out bool truncated, string speaker = "", byte server = 0)
    {
        int length = Cp936Boundary(text, MaxTextBytes);
        truncated = length < text.Length;
        return Body(kind, layer, text[..length], speaker, server);
    }

    static byte[] Body(ChatKind kind, ushort layer, ReadOnlySpan<byte> text, string speaker, byte server)
    {
        // 023D0BA0在正文后还读取一条DSTR；角色名小于原生17字节接收容量，不能省略长度头。
        byte[] name = ChatText.Encoding.GetBytes(ChatText.Truncate(speaker, 16, out _));
        byte[] body = new byte[HeaderLength + text.Length + 4 + name.Length];
        body[0] = ChatWire.TypeOf(kind);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), layer);
        body[3] = server;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)text.Length);
        text.CopyTo(body.AsSpan(HeaderLength));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(HeaderLength + text.Length), checked((uint)name.Length));
        name.CopyTo(body.AsSpan(HeaderLength + text.Length + 4));
        return body;
    }

    /// <summary>
    /// 把 CP936 字节序列截到不超过 <paramref name="max"/> 字节，且不切开双字节字符。
    /// 只按 CP936 的 lead byte 范围（<c>0x81..0xFE</c>）推进，不解码：用于必须保留原字节的链接正文。
    /// 物品链接 <c>0x172</c> 与 <c>0x0C</c> 共用同一边界规则。
    /// </summary>
    internal static int Cp936Boundary(ReadOnlySpan<byte> text, int max)
    {
        int at = 0;
        while (at < text.Length)
        {
            int width = IsCp936Lead(text[at]) && at + 1 < text.Length ? 2 : 1;
            int marker = at + (text[at] == 0x7f && at + 1 < text.Length && text[at + 1] == 0x1e ? 1 : 0);
            if (text[marker] == 0x1e && marker + 3 < text.Length && text[marker + 1] == 0x20)
            {
                int idWidth = IsCp936Lead(text[marker + 2]) ? 2 : 1;
                int end = marker + 2 + idWidth;
                if (end < text.Length && text[end] == 0x1f) width = end - at + 1;
            }
            if (at + width > max)
                break;
            at += width;
        }
        return at;
    }

    static bool IsCp936Lead(byte value) => value is >= 0x81 and <= 0xFE;

    /// <summary>
    /// <c>0x000C</c> 正文文本段的编码：CP936、按字节上限 <see cref="MaxTextBytes"/> 截断、不加结尾 <c>\0</c>。
    /// 生产 builder 与 GM 诊断探针共用这一段，避免各写一份长度/编码规则。
    /// </summary>
    internal static byte[] EncodeText(string? text, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(text))
            return [];
        string kept = ChatText.Truncate(text, MaxTextBytes, ChatText.Encoding, out truncated);
        return ChatText.Encoding.GetBytes(kept);
    }
}
