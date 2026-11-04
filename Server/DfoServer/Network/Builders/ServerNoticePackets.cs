using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Chat;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Builders;

// NOTI502 的完整分支名册；4、5缺少专用载荷时明确报错，不允许伪装成普通文本。
internal enum ServerNoticeDisplay : byte
{
    Information = 0, // 普通信息窗1006。
    InformationMode100 = 1, // 同一1006窗口附参数100；不将该参数猜成确认结果或服务器业务成功。
    Chat = 2, // 聊天栏系统消息。
    Toast = 3, // 客户端固定约两秒的屏幕短提示。
    ItemList = 4, // count:u8及itemId/count各u32；活动发奖使用专用物品正文，不能用文本替代。
    ToastWithOptions = 5 // 底层问题：文本后必读两个u8，第一项用途未闭合，第二项写短提示对象+108。
}

/// <summary>
/// GM 公告 <c>notice</c> 的正式文本通道：<c>NOTI 0x06AD</c>（<c>AAS_TIPS</c>）弹框，已由匹配客户端实测确认。
/// 两种喇叭不走这里，改走 <see cref="MegaphonePackets"/>（<c>NOTI 0x0076</c> 喇叭页签）。
/// <para>
/// 正文：<c>[u32 标题字节数][标题 UTF-8][u32 正文字节数][正文 UTF-8][u8 type=0]</c>，两个长度字段都是编码后的字节数。
/// 客户端对文本的硬性要求：<b>UTF-8</b>（CP936 与 UTF-16LE 实测都是乱码），标题 ≤ 24(0x18) 字节、
/// 正文 ≤ 600(0x258) 字节；越界帧被客户端直接丢弃、不显示，所以这里先按字符边界截断再发送。
/// </para>
/// <para>
/// 与另外两条文本通道的区别：聊天行 <c>NOTI 0x000C</c> 是聊天窗口的一行、喇叭页签 <c>NOTI 0x0076</c>
/// 是「喇叭」页签的一行，二者都由客户端按已有界面渲染；<c>0x06AD</c> 弹出独立对话框
/// （标题 + 正文 + 「确认」按钮），适合需要强提示的公告（字节合同见 <c>Docs/协议/聊天.md</c>）。
/// </para>
/// <para>普通业务提示另用 <see cref="Information"/>：NOTI502 的 CP936 文本信息框，不混用公告的 UTF-8 格式。</para>
/// </summary>
internal static class ServerNoticePackets
{
    public const int InformationMaxBytes = 0x3FF;

    // 023C65D0的type0经03BE26C0读取文本，随后打开普通信息窗1006；不是业务成功或强制开兑换结果窗。
    public static byte[] Information(string text) => Message(ServerNoticeDisplay.Information, text);

    public static byte[] Chat(string text) => Message(ServerNoticeDisplay.Chat, text);

    // 023C66B2/023C6702：type2/3读取同一CP936文本，分别投递聊天和屏幕短提示。
    public static byte[] Message(ServerNoticeDisplay display, string text)
    {
        if (!Enum.IsDefined(display)) throw new ArgumentOutOfRangeException(nameof(display));
        if (display is ServerNoticeDisplay.ItemList or ServerNoticeDisplay.ToastWithOptions)
            throw new NotSupportedException(display == ServerNoticeDisplay.ItemList
                ? "NOTI502 type4 requires a dedicated itemId/count payload, not text."
                : "NOTI502 type5 requires two u8 options after text; option semantics are not verified.");
        ArgumentNullException.ThrowIfNull(text);
        int length = CharacterName.Encoding.GetByteCount(text);
        if (length is < 1 or > InformationMaxBytes) throw new ArgumentOutOfRangeException(nameof(text));
        byte[] packet = new byte[5 + length];
        packet[0] = (byte)display;
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(1), length);
        CharacterName.Encoding.GetBytes(text, packet.AsSpan(5));
        return packet;
    }

    /// <summary>标题为空时使用的默认标题。</summary>
    public const string DefaultTitle = "系统公告";

    /// <summary>标题上限（编码后字节数，客户端 0x18）；超长按字符边界截断。</summary>
    public const int TitleMaxBytes = 0x18;

    /// <summary>正文上限（编码后字节数，客户端 0x258）；超长按字符边界截断。</summary>
    public const int BodyMaxBytes = 0x258;

    /// <summary>弹框类型字段；实测量取值 0。</summary>
    public const byte Type = 0;

    // 客户端实测只接受 UTF-8（CP936/UTF-16LE 都显示乱码），不要改回聊天正文的 CP936。
    static readonly Encoding Utf8 = Encoding.UTF8;

    /// <summary>不关心是否截断时使用这个重载。</summary>
    public static byte[] Build(string title, string body) => Build(title, body, out _);

    /// <summary>
    /// 构造一帧 <c>NOTI 0x06AD</c> 正文：标题为空时换成 <see cref="DefaultTitle"/>，
    /// 两段文本都按 UTF-8 字节数用 <see cref="ChatText.Truncate(string,int,Encoding,out bool)"/> 按字符边界截断，
    /// 超限时 <paramref name="truncated"/> 为 true。<c>type</c> 固定 0。
    /// </summary>
    public static byte[] Build(string title, string body, out bool truncated)
    {
        if (string.IsNullOrEmpty(title))
            title = DefaultTitle;
        byte[] titleBytes = Utf8.GetBytes(ChatText.Truncate(title, TitleMaxBytes, Utf8, out bool titleTruncated));
        byte[] bodyBytes = Utf8.GetBytes(ChatText.Truncate(body ?? "", BodyMaxBytes, Utf8, out bool bodyTruncated));
        truncated = titleTruncated || bodyTruncated;

        byte[] packet = new byte[9 + titleBytes.Length + bodyBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0), (uint)titleBytes.Length);
        titleBytes.CopyTo(packet, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4 + titleBytes.Length), (uint)bodyBytes.Length);
        bodyBytes.CopyTo(packet, 8 + titleBytes.Length);
        packet[^1] = Type;
        return packet;
    }
}
