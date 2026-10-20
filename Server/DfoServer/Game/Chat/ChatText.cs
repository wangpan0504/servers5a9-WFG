using System.Text;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Chat;

/// <summary>
/// 聊天文本的 wire 编码与长度上限。客户端聊天正文固定为 CP936，服务端必须用同一代码页解码，
/// 不能按 UTF-8 或当前区域设置猜测。
/// </summary>
internal static class ChatText
{
    /// <summary>单条聊天文本上限（CP936 字节）。超长按字符边界截断并记日志，不断线。</summary>
    public const int MaxBytes = 200;

    /// <summary>
    /// 宽松回退的 CP936：客户端可能发来任意字节，诊断/记录路径不能因为非法字节抛异常断开连接。
    /// 解码时非法序列替换为回退字符，编码时无法表示的字符同样替换，往返不抛 <see cref="EncoderFallbackException"/>。
    /// </summary>
    public static readonly Encoding Encoding = CreateEncoding();

    static Encoding CreateEncoding()
    {
        // 仓库只在 CharacterName.CreateEncoding() 里注册一次 CodePagesEncodingProvider：
        // 先触及该字段复用既有注册，不重复注册（重复注册会改变全局 provider 列表）。
        _ = CharacterName.Encoding;
        return System.Text.Encoding.GetEncoding(936, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    /// <summary>
    /// 按 CP936 字节上限截断。<paramref name="truncated"/> 为 true 表示原文超限；
    /// 截断按字符边界回退，避免把双字节汉字切成半个字节序列。
    /// </summary>
    public static string Truncate(string text, out bool truncated) => Truncate(text, MaxBytes, out truncated);

    /// <summary>
    /// 按调用方给定的 CP936 字节上限截断，规则与聊天正文截断一致（超限回退到字符边界）。
    /// 供诊断弹框这类上限与聊天正文不同的正文复用同一边界逻辑。
    /// </summary>
    public static string Truncate(string text, int maxBytes, out bool truncated) =>
        Truncate(text, maxBytes, Encoding, out truncated);

    /// <summary>
    /// 按调用方给定的字节上限与编码截断，边界规则同上（超限回退到字符边界）。
    /// 供诊断弹框按 UTF-8/UTF-16LE 的字节数复用同一段截断逻辑，不再写第二套边界判断。
    /// </summary>
    public static string Truncate(string text, int maxBytes, Encoding encoding, out bool truncated)
    {
        truncated = false;
        if (text.Length == 0 || encoding.GetByteCount(text) <= maxBytes)
            return text;
        truncated = true;
        int length = text.Length;
        while (length > 0 && encoding.GetByteCount(text.AsSpan(0, length)) > maxBytes)
            length--;
        return text[..length];
    }

    public static int ByteCount(string text) => Encoding.GetByteCount(text);
}
