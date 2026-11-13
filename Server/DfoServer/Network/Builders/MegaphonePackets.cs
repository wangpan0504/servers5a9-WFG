using System.Buffers.Binary;
using DfoServer.Game.Chat;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;

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
/// str1/str2保留既有29/511字节截断及非空回退；str3是账号冒险团邀请目标，
/// 按现行命名合同完整编码为CP936最多32字节/16UTF16，不截断、不替代、不回退合法占位。
/// 空或未初始化/非法账号名写长度0；配套客户端两tag局部reader解耦窄字节/宽字符容量，
/// 空metadata经原非空菜单门不形成邀请目标，不将reader返回0误称整帧必丢。
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

    /// <summary><c>str3</c>账号冒险团名的完整编码上限；另须满足16UTF16的现行命名合同。</summary>
    public const int TagMaxBytes = 32;

    // 只有发言人/正文沿用非空回退；目标串不能生成可命中另一账号的占位。
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

    /// <summary>不提供账号冒险团名时目标为空，不以角色名或固定占位代替。</summary>
    public static byte[] Build(ChatKind kind, string speaker, string text) => Build(kind, speaker, text, null);

    public static byte[] Build(ChatKind kind, MegaphoneText message) => Build(kind, message.Speaker, message.Text,
        message.AdventureName, message.Channel.ServerIndex, message.Channel.Number, message.Actor);

    public static byte[] BuildBytes(ChatKind kind, MegaphoneText message, byte[] text) => Build(kind, message.Speaker,
        text.Length == 0 ? [(byte)' '] : text, message.AdventureName, message.Channel.ServerIndex, message.Channel.Number, message.Actor);

    /// <summary>
    /// 按种类构造一帧 <c>NOTI 0x0076</c> 正文；<paramref name="tag"/> 是完整账号冒险团名，
    /// 空或不合法时写空目标，不截断或替代。生产投递通过MegaphoneText携带owner快照。
    /// </summary>
    public static byte[] Build(ChatKind kind, string speaker, string text, string? tag,
        byte server = 0, byte channel = 0, ushort actor = 0) =>
        Build(kind, speaker, Encode(text, TextMaxBytes, TextFallback), tag, server, channel, actor);

    static byte[] Build(ChatKind kind, string speaker, byte[] textBytes, string? tag,
        byte server, byte channel, ushort actor)
    {
        byte[] speakerBytes = Encode(speaker, SpeakerMaxBytes, SpeakerFallback);
        byte[] tagBytes = EncodeAdventureTarget(tag);

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

    /// <summary>邀请键必须完整满足现行冒险团命名合同；坏旧数据只失去目标，不生成截断别名。</summary>
    public static byte[] EncodeAdventureTarget(string? name)
    {
        if (name is null || !AdventureName.IsValid(name)) return [];
        byte[] bytes = CharacterName.Encoding.GetBytes(name);
        return bytes.Length <= TagMaxBytes && CharacterName.Encoding.GetString(bytes) == name ? bytes : [];
    }

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
