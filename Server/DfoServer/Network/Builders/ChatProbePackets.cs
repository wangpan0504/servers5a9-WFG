using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Chat;

namespace DfoServer.Network.Builders;

/// <summary>
/// 诊断工具：GM <c>chat-inject</c> 的测试正文变体，只由该命令使用，正式投递不引用本文件。
/// <para>
/// L1/L2/L3 是 <c>NOTI 0x000C</c> 的诊断正文，与生产 builder <see cref="ChatPackets.Message(ChatKind,ushort,string)"/>
/// 共用同一段编码（<see cref="ChatPackets.EncodeText"/>），三者的区别只在 GM 的用法约定：
/// L1 用于 ASCII 文本、L2 用于中文文本、L3 用于超长文本（核对按字符边界截断到 255 字节）。
/// 布局为 <c>[u8 type][u16 层 id][u8 v][u32 CP936 字节数][CP936 文本]</c>；同一文本三者逐字节相同。
/// </para>
/// <para>
/// P3E 与 AAS 换用聊天族以外的通知，用于验证「服务端能否让客户端显示任意文本」：
/// P3E = <c>NOTI 0x03E2</c>（<see cref="NotiPacketType.REVENUE_SANCTION"/>）制裁弹窗；
/// AAS = <c>NOTI 0x06AD</c>（<see cref="NotiPacketType.AAS_TIPS"/>）弹框；AAS8/AASU 与 AAS 同布局，
/// 只把标题与正文的编码换成 UTF-8 / UTF-16LE。实测结论：弹框可见，且只有 <b>UTF-8</b>（AAS8）能正常显示中文，
/// CP936（AAS）与 UTF-16LE（AASU）都是乱码。正式公告因此直接复用这条通道
/// （<see cref="ServerNoticePackets"/>），本文件只保留编码/布局诊断变体，不进入正式投递。
/// </para>
/// <para>
/// 变体只用于向指定在线连接注入一帧测试包，核对收发路径、帧到达与文本显示。
/// </para>
/// </summary>
internal enum ChatProbeVariant : byte
{
    /// <summary>约定传 ASCII 文本；布局与生产 builder 完全一致（<c>[type][层 id][v][u32 字节数][CP936 文本]</c>）。</summary>
    L1 = 1,

    /// <summary>约定传中文文本，用于核对 CP936 编码；布局与 L1 相同，同一文本逐字节一致。</summary>
    L2 = 2,

    /// <summary>约定传超长文本（&gt; 255 字节），用于核对按字符边界截断；布局与 L1 相同。</summary>
    L3 = 3,

    /// <summary><c>NOTI 0x03E2</c> 制裁弹窗：A/B 都必须非 0 客户端才走制裁分支。</summary>
    P3E = 4,

    /// <summary><c>NOTI 0x06AD</c> 弹框：标题固定「系统公告」，正文取命令文本，文本按 CP936。</summary>
    AAS = 5,

    /// <summary>与 <see cref="AAS"/> 同布局同 opcode，但标题与正文改用 UTF-8。</summary>
    AAS8 = 6,

    /// <summary>与 <see cref="AAS"/> 同布局同 opcode，但标题与正文改用 UTF-16LE（长度仍按字节数）。</summary>
    AASU = 7
}

/// <summary>诊断用测试正文构造与变体解析，只被 GM <c>chat-inject</c> 使用。</summary>
internal static class ChatProbes
{
    /// <summary>AAS 弹框文本的编码。三种变体布局与 opcode 完全相同，只换这两段字节的编码。</summary>
    internal enum AasEncoding : byte
    {
        /// <summary>CP936，与聊天正文一致。</summary>
        Cp936 = 0,

        /// <summary>UTF-8，不带 BOM。</summary>
        Utf8 = 1,

        /// <summary>UTF-16LE，不带 BOM、不带结尾 \0；长度字段仍按字节数。</summary>
        Utf16Le = 2
    }

    /// <summary>
    /// P3E 的 A 字段：非 0 表示制裁开启。客户端模板
    /// <c>sanction is ON : explain : %s\nlevel : %d\nsanction time : %dsec</c>
    /// 只在 A≠0 且 B≠0 时才会命中制裁分支，A/B 全 0 会走别的分支（实测为空白）。
    /// </summary>
    public const uint SanctionOn = 1;

    /// <summary>P3E 的 B 字段：制裁等级（模板里的 <c>level</c>），同样必须非 0。</summary>
    public const uint SanctionLevel = 1;

    /// <summary>AAS 弹框标题，固定「系统公告」；CP936 8 字节，远低于 24 字节上限，不会随正文放大。</summary>
    public const string AasTitle = "系统公告";

    /// <summary>AAS 标题长度上限（编码后的字节数，0x18）。</summary>
    public const int AasTitleMaxBytes = 0x18;

    /// <summary>AAS 正文长度上限（编码后的字节数，0x258）。</summary>
    public const int AasBodyMaxBytes = 0x258;

    /// <summary>变体是否走 <c>NOTI 0x06AD</c> 弹框（<see cref="ChatProbeVariant.AAS"/>/<see cref="ChatProbeVariant.AAS8"/>/<see cref="ChatProbeVariant.AASU"/>）。</summary>
    public static bool IsAas(ChatProbeVariant variant) =>
        variant is ChatProbeVariant.AAS or ChatProbeVariant.AAS8 or ChatProbeVariant.AASU;

    /// <summary>把 GM 命令里的变体文本解析为测试变体（大小写不敏感），只接受 L1/L2/L3/P3E/AAS/AAS8/AASU。</summary>
    public static bool TryParseVariant(string text, out ChatProbeVariant variant)
    {
        switch (text.ToUpperInvariant())
        {
            case "L1": variant = ChatProbeVariant.L1; return true;
            case "L2": variant = ChatProbeVariant.L2; return true;
            case "L3": variant = ChatProbeVariant.L3; return true;
            case "P3E": variant = ChatProbeVariant.P3E; return true;
            case "AAS": variant = ChatProbeVariant.AAS; return true;
            case "AAS8": variant = ChatProbeVariant.AAS8; return true;
            case "AASU": variant = ChatProbeVariant.AASU; return true;
            default: variant = default; return false;
        }
    }

    /// <summary>
    /// 变体对应的 NOTI opcode：L1/L2/L3 沿用聊天通知 <c>0x000C</c>，
    /// P3E 走制裁弹窗 <c>0x03E2</c>，AAS/AAS8/AASU 走弹框 <c>0x06AD</c>。
    /// </summary>
    public static NotiPacketType Noti(ChatProbeVariant variant) => variant switch
    {
        ChatProbeVariant.P3E => NotiPacketType.REVENUE_SANCTION,
        ChatProbeVariant.AAS or ChatProbeVariant.AAS8 or ChatProbeVariant.AASU => NotiPacketType.AAS_TIPS,
        _ => NotiPacketType.MESSAGE
    };

    /// <summary>测试正文；不需要截断信息的调用方用这个重载。</summary>
    public static byte[] Body(ChatProbeVariant variant, ushort layer, string text) => Body(variant, layer, text, out _);

    /// <summary>
    /// 测试正文：
    /// L1/L2/L3 = <c>[u8 03][u16LE 层 id][u8 v=0][u32 CP936 字节数][CP936 文本]</c>，直接复用生产 builder
    /// <see cref="ChatPackets.Message(ChatKind,ushort,string,out bool)"/>，三者对同一文本逐字节相同；
    /// P3E = <c>[u32 A][u32 B][u32 字符数][UTF-16LE 文本][u32 CP936 字节数][CP936 文本]</c>（不带层 id）；
    /// AAS = <c>[u32 标题字节数][标题 CP936][u32 正文字节数][正文 CP936][u8 type=0]</c>（不带层 id）；
    /// AAS8/AASU 与 AAS 同布局，只把标题/正文的编码换成 UTF-8 / UTF-16LE。
    /// <paramref name="truncated"/>：AAS 系是标题/正文超过 24/600 字节，L1/L2/L3 是正文超过 255 字节，
    /// 都按字符边界截断。
    /// </summary>
    public static byte[] Body(ChatProbeVariant variant, ushort layer, string text, out bool truncated)
    {
        truncated = false;
        if (variant == ChatProbeVariant.P3E)
            return SanctionBody(text);
        if (IsAas(variant))
            return AasBody(variant, text, out truncated);
        // 诊断变体不再自带布局：L1/L2/L3 与正式投递是同一段编码，避免两边各写一份长度/编码规则。
        return ChatPackets.Message(ChatKind.Normal, layer, text, out truncated);
    }

    /// <summary>
    /// P3E 制裁弹窗正文：<c>[u32 A=1][u32 B=1][u32 字符数][UTF-16LE 文本][u32 CP936 字节数][CP936 文本]</c>。
    /// 与 L1/L2/L3 的差别是去掉层 id、把 0/0 换成非 0 的 A/B；两段文本同源，客户端按 UTF-16 渲染正文。
    /// </summary>
    static byte[] SanctionBody(string text)
    {
        byte[] wide = Encoding.Unicode.GetBytes(text);
        byte[] mbcs = ChatText.Encoding.GetBytes(text);
        byte[] body = new byte[16 + wide.Length + mbcs.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), SanctionOn);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), SanctionLevel);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), (uint)text.Length);
        wide.CopyTo(body, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12 + wide.Length), (uint)mbcs.Length);
        mbcs.CopyTo(body, 16 + wide.Length);
        return body;
    }

    /// <summary>
    /// AAS 弹框正文：<c>[u32 标题字节数][标题][u32 正文字节数][正文][u8 type=0]</c>。
    /// 标题固定「系统公告」，两段字节的编码由变体决定（<see cref="EncodeAas"/>），上限按编码后的字节数判断，
    /// 超限时按字符边界截断并置 <paramref name="truncated"/>。
    /// </summary>
    static byte[] AasBody(ChatProbeVariant variant, string text, out bool truncated)
    {
        (byte[] title, byte[] content) = EncodeAas(text, EncodingOf(variant), out truncated);
        byte[] body = new byte[9 + title.Length + content.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0), (uint)title.Length);
        title.CopyTo(body, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4 + title.Length), (uint)content.Length);
        content.CopyTo(body, 8 + title.Length);
        body[8 + title.Length + content.Length] = 0;
        return body;
    }

    /// <summary>AAS 变体到文本编码的唯一映射：AAS=CP936、AAS8=UTF-8、AASU=UTF-16LE。</summary>
    static AasEncoding EncodingOf(ChatProbeVariant variant) => variant switch
    {
        ChatProbeVariant.AAS8 => AasEncoding.Utf8,
        ChatProbeVariant.AASU => AasEncoding.Utf16Le,
        _ => AasEncoding.Cp936
    };

    /// <summary>
    /// AAS 标题与正文的唯一编码入口：按 <paramref name="enc"/> 编码，各自按编码后的字节数
    /// （标题 24、正文 600）用 <see cref="ChatText.Truncate(string,int,Encoding,out bool)"/> 按字符边界截断。
    /// UTF-16LE 的长度字段也是字节数（2×字符数），不带 BOM、不加结尾 \0。
    /// </summary>
    static (byte[] Title, byte[] Body) EncodeAas(string text, AasEncoding enc, out bool truncated)
    {
        Encoding encoding = enc switch
        {
            AasEncoding.Utf8 => Encoding.UTF8,
            AasEncoding.Utf16Le => Encoding.Unicode,
            _ => ChatText.Encoding
        };
        byte[] title = encoding.GetBytes(ChatText.Truncate(AasTitle, AasTitleMaxBytes, encoding, out bool titleCut));
        byte[] content = encoding.GetBytes(ChatText.Truncate(text, AasBodyMaxBytes, encoding, out bool bodyCut));
        truncated = titleCut || bodyCut;
        return (title, content);
    }
}
