using System.Buffers.Binary;
using DfoServer.Game.Chat;

namespace DfoServer.Network.Builders;

/// <summary>
/// 喇叭通道 <c>NOTI 0x0076 MEGAPHONE_MSG</c>：客户端「喇叭」页签里的一行 <c>str1 : str2</c>，
/// 包体均保留真实发言人和来源频道；118/type14由客户端选择官方显示名，不在服务端替换。
/// <para>
/// 正文：<c>[u8 type][u8 B][u8 C][u8 D][u16LE E][str1][str2][str3]</c>，
/// 每条str保留u32字节长度。023A3AE0按来源分组B、显示模式C、来源频道D、角色编号E读取；
/// type11对应道具36，type13对应道具37，type14是1120官方路径。str1是角色名，str2是正文。
/// </para>
/// <para>
/// 客户端硬性要求（此前所有失败的真因）：三条字符串<b>都必须非空</b>（<c>len ≥ 1</c>），
/// 任一条写 <c>len = 0</c> 会让客户端把<b>整帧丢弃</b>；长度上限分别是 <c>str1 &lt; 0x1E</c>、
/// <c>str2 &lt; 0x200</c>、<c>str3 &lt; 0x11</c>，这里复用 <see cref="ChatText"/> 的截断逻辑按字符边界回退
/// （不会切出半个汉字），截空时再回退成单字节占位，保证任何输入都不会生成 <c>len = 0</c> 的串。
/// </para>
/// <para>
/// 正式投递携带发送者的频道身份，不能以接收者频道或全0代替；来源与本机分组/频道匹配时客户端复用本地角色上下文。
/// </para>
/// </summary>
internal static class MegaphonePackets
{
    /// <summary>正文头长度：<c>[u8 type][u8 B][u8 C][u8 D][u16 E]</c>。</summary>
    public const int HeaderLength = 6;

    /// <summary>道具36的服务器喇叭类型，保留玩家发言人。</summary>
    public const byte ServerType = (byte)ChatMessageType.ServerMegaphone;

    /// <summary>道具37的频道喇叭类型，保留玩家发言人及来源频道。</summary>
    public const byte ChannelType = (byte)ChatMessageType.ChannelMegaphone;

    /// <summary><c>str1</c>（发言人）字节上限；客户端要求 <c>len &lt; 0x1E</c>，所以最多 29 字节。</summary>
    public const int SpeakerMaxBytes = 0x1E - 1;

    /// <summary><c>str2</c>（正文）字节上限；客户端要求 <c>len &lt; 0x200</c>，所以最多 511 字节。</summary>
    public const int TextMaxBytes = 0x200 - 1;

    /// <summary><c>str3</c>（附加标记）字节上限；客户端要求 <c>len &lt; 0x11</c>，所以最多 16 字节。</summary>
    public const int TagMaxBytes = 0x11 - 1;

    /// <summary><c>str3</c> 的固定占位：画面不显示，只用来保证该串非空。</summary>
    public const string Tag = "X";

    // 三条串都必须 len >= 1（否则整帧被丢弃），空输入回退成单字节 ASCII。
    const string SpeakerFallback = "?";
    const string TextFallback = " ";

    /// <summary>
    /// 玩家服务器/频道喇叭对应原生type11/13，GM1120独立对应官方type14。
    /// 非喇叭种类直接抛出，避免静默按频道喇叭发出去。
    /// </summary>
    public static byte TypeOf(ChatKind kind) => kind switch
    {
        ChatKind.ServerMegaphone => ServerType,
        ChatKind.ChannelMegaphone => ChannelType,
        ChatKind.OfficialMegaphone => (byte)ChatMessageType.Official1120Megaphone,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "只有喇叭种类有 0x0076 类型")
    };

    /// <summary>按种类构造一帧 <c>NOTI 0x0076</c> 正文，<c>str3</c> 用固定占位（生产入口）。</summary>
    public static byte[] Build(ChatKind kind, string speaker, string text) => Build(kind, speaker, text, null);

    public static byte[] Build(ChatKind kind, MegaphoneText message) => Build(kind, message.Speaker, message.Text,
        null, message.Channel.ServerIndex, message.Channel.Number, message.Actor);

    public static byte[] BuildBytes(ChatKind kind, MegaphoneText message, byte[] text) => Build(kind, message.Speaker,
        text.Length == 0 ? [(byte)' '] : text, null, message.Channel.ServerIndex, message.Channel.Number, message.Actor);

    /// <summary>
    /// 按种类构造一帧 <c>NOTI 0x0076</c> 正文；<paramref name="tag"/> 为 <c>null</c> 时用 <see cref="Tag"/>。
    /// 给出显式 <c>str3</c> 只用于复现实测帧，生产投递不要传。
    /// </summary>
    public static byte[] Build(ChatKind kind, string speaker, string text, string? tag,
        byte server = 0, byte channel = 0, ushort actor = 0) =>
        Build(kind, speaker, Encode(text, TextMaxBytes, TextFallback), tag, server, channel, actor);

    static byte[] Build(ChatKind kind, string speaker, byte[] textBytes, string? tag,
        byte server, byte channel, ushort actor)
    {
        byte[] speakerBytes = Encode(speaker, SpeakerMaxBytes, SpeakerFallback);
        byte[] tagBytes = Encode(tag ?? Tag, TagMaxBytes, Tag);

        byte[] body = new byte[HeaderLength + 3 * sizeof(uint) + speakerBytes.Length + textBytes.Length + tagBytes.Length];
        body[0] = TypeOf(kind);
        // 023A3AE0依次读取来源分组、显示模式、来源频道及角色编号；不能用接收者频道或全0代替。
        body[1] = server;
        body[3] = channel;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), actor);
        int offset = HeaderLength;
        offset = WriteString(body, offset, speakerBytes);
        offset = WriteString(body, offset, textBytes);
        WriteString(body, offset, tagBytes);
        return body;
    }

    /// <summary>
    /// 编码后的实际字节数（<c>type</c> 与两条串），供投递日志使用：
    /// 与 <see cref="Build(ChatKind,string,string)"/> 共用同一套截断/回退规则，日志不会与真实帧不一致。
    /// </summary>
    public static (byte Type, int SpeakerBytes, int TextBytes) Measure(ChatKind kind, string speaker, string text) =>
        (TypeOf(kind),
            Encode(speaker, SpeakerMaxBytes, SpeakerFallback).Length,
            Encode(text, TextMaxBytes, TextFallback).Length);

    /// <summary>写一条 <c>u32LE 字节长度 + 字节</c>，返回下一段的偏移。</summary>
    static int WriteString(byte[] body, int offset, byte[] bytes)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), (uint)bytes.Length);
        bytes.CopyTo(body, offset + sizeof(uint));
        return offset + sizeof(uint) + bytes.Length;
    }

    /// <summary>
    /// CP936 编码并按字节上限截断：截断走 <see cref="ChatText.Truncate(string,int,System.Text.Encoding,out bool)"/>
    /// 按字符边界回退；截空（首字符就是双字节汉字且上限只有 1 字节这类极端）时回退成单字节占位，保证 <c>len ≥ 1</c>。
    /// </summary>
    static byte[] Encode(string? value, int maxBytes, string fallback)
    {
        string kept = ChatText.Truncate(value ?? "", maxBytes, ChatText.Encoding, out _);
        return ChatText.Encoding.GetBytes(kept.Length == 0 ? fallback : kept);
    }
}
