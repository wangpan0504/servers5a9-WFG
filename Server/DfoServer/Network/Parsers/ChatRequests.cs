using System.Buffers.Binary;
using DfoServer.Game.Chat;

namespace DfoServer.Network.Parsers;

/// <summary><c>CMD 0x01A8</c> 解析失败原因，用于日志；解析失败只丢弃，不伪造成功、不断线。</summary>
internal enum ChatHyperlinkError : byte
{
    None,
    TooShort,
    LengthZero,
    LengthTooLarge,
    TextOverrun,
    RecordsOverrun,
    InvalidTarget,
    TrailingData
}

/// <summary>
/// 聊天发送 <c>CMD 0x0011 SEND_MESSAGE</c> 的请求正文解析：
/// <c>[u8 type][u16 层 id][u32 arg][u32 textLen][CP936 文本]</c>，固定 11 字节头。
/// 真实样本见 <c>Docs/协议/聊天.md</c>：普通 <c>03040000000000020000002E2E</c>、
/// 公会 <c>060400000000000A000000B2E2CAD4313233343536</c>、
/// 服务器喇叭 <c>0B0000430000000B000000B7FECEF1C6F7C0AEB0C831</c>、
/// 频道喇叭 <c>0D00004400000009000000C6B5B5C0C0AEB0C831</c>。
/// <para>
/// 物品链接 <c>CMD 0x01A8</c> 的完整正文（实测 179 字节）：
/// <c>[u8 kind][u16 reference][u32 arg][DSTR文本][可选目标名字及分组][u8 cnt][cnt × 148记录]</c>。
/// 02E2E470与CMD17共用头部生产者；arg在喇叭中为源槽位，具名目标先于记录写入。
/// </para>
/// </summary>
internal static class ChatRequests
{
    public const int HeaderLength = 11;

    /// <summary>0x01A8固定前缀：<c>kind:u8,reference:u16,arg:u32,textLength:u32</c>。</summary>
    public const int HyperlinkHeaderLength = 11;

    /// <summary>0x01A8 的 <c>kind</c> 在第 0 字节（与 0x0011 的 <c>u8 type</c> 同源）。</summary>
    public const int HyperlinkKindOffset = 0;

    /// <summary>0x01A8 的 <c>u32 文本字节数</c> 在偏移7，与CMD17相同。</summary>
    public const int HyperlinkTextLengthOffset = 7;

    /// <summary>0x01A8 的单条物品记录长度（实测；记录内容语义未定）。</summary>
    public const int HyperlinkRecordLength = 148;

    /// <summary>
    /// 链接文本的服务端防御上限。实测样本 19 字节；下行 <c>0x000C</c> 仍受 255 字节限制。
    /// </summary>
    public const int HyperlinkMaxTextBytes = 4096;

    /// <summary>
    /// 正文不足 11 字节头，或 <c>textLen</c> 超出实际字节数时返回 false，由调用方按未处理记录，
    /// 不猜测文本、不构造成功。文本按 CP936 宽松解码，非法字节替换为回退字符而不是抛异常断线。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out ChatSend send)
    {
        send = default;
        if (body.Length < HeaderLength)
            return false;
        byte type = body[0];
        ushort layer = BinaryPrimitives.ReadUInt16LittleEndian(body[1..]);
        uint arg = BinaryPrimitives.ReadUInt32LittleEndian(body[3..]);
        uint textLength = BinaryPrimitives.ReadUInt32LittleEndian(body[7..]);
        // 先按头长度比较，避免 u32 长度被 (int) 截断后越界读取。
        if (textLength > (uint)(body.Length - HeaderLength))
            return false;
        send = new(type, layer, arg, textLength, ChatText.Encoding.GetString(body.Slice(HeaderLength, (int)textLength)));
        if (HasNamedTarget(type))
        {
            int at = HeaderLength + (int)textLength;
            if (!TryNamedTarget(body, ref at, ref send) || at != body.Length) return false;
        }
        else if (body.Length != HeaderLength + (int)textLength) return false;
        return true;
    }

    /// <summary>
    /// 解析 <c>CMD 0x01A8</c>。用剩余字节数自行判断边界：长度字段先与
    /// <c>body.Length</c> 比较，再读文本、<c>cnt</c> 与 <c>cnt × 148</c> 条记录；
    /// 任何一步不足都返回 false 并给出原因，绝不抛异常（不能因为客户端畸形包断线）。
    /// 不预检后再读：这里只有 Span 下标，没有会推进游标的 <c>CheckRemain</c>。
    /// </summary>
    public static bool TryParseHyperlink(ReadOnlySpan<byte> body, out ChatHyperlink link, out ChatHyperlinkError error)
    {
        link = null!;
        if (body.Length < HyperlinkHeaderLength)
        {
            error = ChatHyperlinkError.TooShort;
            return false;
        }

        uint textLength = BinaryPrimitives.ReadUInt32LittleEndian(body[HyperlinkTextLengthOffset..]);
        if (textLength == 0)
        {
            error = ChatHyperlinkError.LengthZero;
            return false;
        }
        if (textLength > HyperlinkMaxTextBytes)
        {
            error = ChatHyperlinkError.LengthTooLarge;
            return false;
        }
        // 文本之后还必须留有 1 字节 cnt；先排除连 cnt 都没有的正文，避免下面 -1 变成无符号回绕。
        if (body.Length <= HyperlinkHeaderLength)
        {
            error = ChatHyperlinkError.TextOverrun;
            return false;
        }
        if (textLength > (uint)(body.Length - HyperlinkHeaderLength - 1))
        {
            error = ChatHyperlinkError.TextOverrun;
            return false;
        }

        int textOffset = HyperlinkHeaderLength;
        int countOffset = textOffset + (int)textLength;
        var request = new ChatSend(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]),
            BinaryPrimitives.ReadUInt32LittleEndian(body[3..]), textLength, "");
        // 02E2E470在正文后先写名字和分组，最后才写记录数；不能把名字长度误作记录数。
        if (HasNamedTarget(request.Type) && !TryNamedTarget(body, ref countOffset, ref request))
        {
            error = ChatHyperlinkError.InvalidTarget;
            return false;
        }
        if (countOffset == body.Length)
        {
            error = ChatHyperlinkError.RecordsOverrun;
            return false;
        }
        int count = body[countOffset];
        int recordsOffset = countOffset + 1;
        int recordsLength = count * HyperlinkRecordLength;
        if (recordsLength > body.Length - recordsOffset)
        {
            error = ChatHyperlinkError.RecordsOverrun;
            return false;
        }
        if (recordsLength != body.Length - recordsOffset)
        {
            error = ChatHyperlinkError.TrailingData;
            return false;
        }

        error = ChatHyperlinkError.None;
        link = new(request,
            body.Slice(textOffset, (int)textLength).ToArray(),
            body.Slice(recordsOffset, recordsLength).ToArray());
        return true;
    }

    static bool HasNamedTarget(byte type) => (ChatRequestType)type is ChatRequestType.Whisper or
        ChatRequestType.OneToOne or ChatRequestType.OneToOneReply or ChatRequestType.WhisperReply;

    static bool TryNamedTarget(ReadOnlySpan<byte> body, ref int at, ref ChatSend request)
    {
        if (body.Length - at < 5) return false;
        int length = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        if (length is < 1 or > 12 || body.Length - at < 5 + length) return false;
        try
        {
            string name = DfoServer.Game.Characters.CharacterName.Encoding.GetString(body.Slice(at + 4, length));
            if (!DfoServer.Game.Characters.CharacterName.IsValid(name)) return false;
            request = request with { TargetName = name, TargetServer = body[at + 4 + length] };
            at += 5 + length;
            return true;
        }
        catch (System.Text.DecoderFallbackException) { return false; }
    }
}
