using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Chat;

namespace DfoServer.Network.Builders;

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
/// </summary>
internal static class ServerNoticePackets
{
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
