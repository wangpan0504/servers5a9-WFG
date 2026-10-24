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
/// <c>0x27F</c>「信息长度有误」。因此正文是 <c>[u8 type][u16 层 id][u8 v][u32 字节长度][CP936 文本]</c>，
/// 长度按<b>字节</b>、上限 <c>0xFF</c>；<c>len == 0</c> 表示不带正文。
/// <c>type==3</c> 时客户端还要求层 id 等于收件人自己的当前层 id，否则丢弃。</item>
/// <item>正文首字节不是入站 <see cref="ChatKind"/> 的原值，而是显示 type：
/// 普通/队伍/悄悄话 = <c>3</c>，公会/攻坚队 = <c>6</c>（映射见 <see cref="ChatWire"/>；
/// 蓝/绿/紫 kind 在「普通」页签不被渲染，是实测的客户端限制）。</item>
/// <item><c>NOTI 0x0076 MEGAPHONE_MSG</c> 是「喇叭」页签通道（见 <see cref="MegaphonePackets"/>），
/// 不在这里构造：本文件只负责聊天行 <c>0x000C</c>。</item>
/// </list>
/// </summary>
internal static class ChatPackets
{
    // 023B0210：正文后必读附加名字串（023B0365），type36随后再读接收者名字（023B0386）。
    public static byte[] NamedMessage(NamedChatMessage message)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(message.Mode);
        writer.Write(message.Status);
        PacketEncoding.WriteString(writer, message.Name);
        writer.Write(message.Server);
        PacketEncoding.WriteString(writer, message.Text);
        PacketEncoding.WriteString(writer, message.AdventureName);
        if (message.Mode == 36) PacketEncoding.WriteString(writer, message.Recipient);
        return stream.ToArray();
    }

    /// <summary><c>0x000C</c> 正文头长度：<c>[u8 type][u16 层 id][u8 v][u32 字节长度]</c>。</summary>
    public const int HeaderLength = 8;

    /// <summary>
    /// 正文长度上限（字节）。客户端只在 <c>0 &lt; len &lt; 0x100</c> 时读文本，达到 <c>0x100</c> 会显示占位串
    /// 「信息长度有误」，因此绝不能写到 <c>0x100</c>。
    /// </summary>
    public const int MaxTextBytes = 0xFF;

    /// <summary>
    /// <c>u8 v</c> 固定 0。客户端在 <c>v == 0</c> 时用层 id 属主补行首发言人（不是服务端下发的名字），
    /// 这是服务端目前唯一确证的取值，沿用既有语义。
    /// </summary>
    const byte SpeakerFromLayer = 0;

    /// <summary>不带正文的 <c>NOTI 0x000C</c>（长度字段写 0，与"无正文"兼容）。</summary>
    public static byte[] Message(ChatKind kind, ushort layer) => Message(kind, layer, null, out _);

    /// <summary><c>NOTI 0x000C</c> 正文：<c>[type][层 id][v=0][u32 字节长度][CP936 文本]</c>；空文本时长度为 0。</summary>
    public static byte[] Message(ChatKind kind, ushort layer, string? text) => Message(kind, layer, text, out _);

    /// <summary>
    /// <c>NOTI 0x000C</c> 正文的唯一编码入口。长度取 CP936 编码后的<b>字节数</b>，
    /// 超过 <see cref="MaxTextBytes"/> 时复用 <see cref="ChatText.Truncate(string,int,System.Text.Encoding,out bool)"/>
    /// 按字符边界截断（不切半个汉字），并把 <paramref name="truncated"/> 置 true。
    /// </summary>
    public static byte[] Message(ChatKind kind, ushort layer, string? text, out bool truncated)
    {
        byte[] bytes = EncodeText(text, out truncated);
        return Body(kind, layer, bytes);
    }

    /// <summary>
    /// 已编码字节正文（<c>CMD 0x01A8</c> 物品链接）：原样写进 <c>0x000C</c>，不做 CP936 解码再编码，
    /// 因此 <c>0x7F</c> 标记与客户端给出的字节序列保持不变。超过 <see cref="MaxTextBytes"/> 时按
    /// CP936 双字节边界回退（不切开半个汉字），<paramref name="truncated"/> 表示确有截断。
    /// </summary>
    public static byte[] MessageBytes(ChatKind kind, ushort layer, ReadOnlySpan<byte> text, out bool truncated)
    {
        int length = Cp936Boundary(text, MaxTextBytes);
        truncated = length < text.Length;
        return Body(kind, layer, text[..length]);
    }

    static byte[] Body(ChatKind kind, ushort layer, ReadOnlySpan<byte> text)
    {
        byte[] body = new byte[HeaderLength + text.Length];
        body[0] = ChatWire.TypeOf(kind);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), layer);
        body[3] = SpeakerFromLayer;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)text.Length);
        text.CopyTo(body.AsSpan(HeaderLength));
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
