using DfoServer.GameWorld.Channels;

namespace DfoServer.Game.Chat;

/// <summary>
/// 已接入的业务分类，不是wire枚举；入站转换使用ChatRequestType，下行使用ChatMessageType。
/// </summary>
internal enum ChatKind : byte
{
    Normal,
    Party,
    Guild,
    Whisper,
    ServerMegaphone,
    ChannelMegaphone,
    OfficialMegaphone
}

/// <summary>
/// 聊天显示频道（客户端页签），与入站 <see cref="ChatKind"/> 分开：它决定下行
/// <c>NOTI 0x000C</c> 正文首字节 <c>type</c>。普通与公会沿用已验证的3/6，队伍使用原生2；
/// ARAD的02E2C180显式处理2，不能由其他蓝色type的失败样本推断队伍也必须写3。
/// </summary>
internal enum ChatChannel : byte
{
    Normal,
    Party,
    Whisper,
    Guild,
    Raid
}

/// <summary>
/// 聊天频道 → <c>NOTI 0x000C</c> 显示 <c>type</c> 的唯一映射。
/// 蓝(0x32)/绿(0x0A)/紫(0x35)/天蓝(0x39) 在「普通」页签不被渲染，是客户端限制，不是服务端可调样式；
/// 具体实测清单见 <c>Docs/协议/聊天.md</c> 第 2.1 节。
/// </summary>
internal static class ChatWire
{
    /// <summary>普通及旧角色编号悄悄话的显示type。</summary>
    public const byte NormalType = (byte)ChatMessageType.Normal;

    /// <summary>「普通」页签可渲染的公会/攻坚队 type。</summary>
    public const byte GuildType = (byte)ChatMessageType.Guild;

    public static byte TypeOf(ChatChannel channel) => channel switch
    {
        ChatChannel.Party => (byte)ChatMessageType.Party,
        ChatChannel.Normal or ChatChannel.Whisper => NormalType,
        ChatChannel.Guild => GuildType,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "未知聊天频道")
    };

    /// <summary>把入站 <see cref="ChatKind"/> 归到显示频道；两种喇叭不走 <c>0x000C</c>，没有映射。</summary>
    public static ChatChannel ChannelOf(ChatKind kind) => kind switch
    {
        ChatKind.Normal => ChatChannel.Normal,
        ChatKind.Party => ChatChannel.Party,
        ChatKind.Whisper => ChatChannel.Whisper,
        ChatKind.Guild => ChatChannel.Guild,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "该聊天种类不走 0x000C 聊天行")
    };

    /// <summary>下行 <c>0x000C</c> 正文首字节；队伍2、普通/悄悄话3、公会6。</summary>
    public static byte TypeOf(ChatKind kind) => TypeOf(ChannelOf(kind));
}

/// <summary>
/// <c>CMD 0x0011 SEND_MESSAGE</c> 的请求正文：<c>[u8 type][u16 层 id][u32 arg][u32 textLen][CP936 文本]</c>。
/// Layer是历史命名的请求角色引用，依上下文使用，不作为正式发言人的权威来源。
/// 原角色ID兼容私聊的arg是目标角色编号；两种喇叭的arg是源槽位。
/// </summary>
internal readonly record struct ChatSend(byte Type, ushort Layer, uint Arg, uint TextLength, string Text)
{
    public string? TargetName { get; init; }
    public byte TargetServer { get; init; }
}

/// <summary>
/// <c>CMD 0x01A8 ITEM_HYPERLINK_MESSAGE</c> 的解析结果（实测 179 字节，见 <c>Docs/协议/聊天.md</c> 第 7 节）：
/// <paramref name="Request"/> 保留同CMD17的头部、源槽位及具名目标；<paramref name="Text"/> 是格式化原字节，
/// 含 <c>0x7F</c> 标记，不经过解码重编码；
/// <paramref name="Records"/> 是 <c>cnt × 148</c> 字节的物品记录（记录 <c>+0x04</c> 是物品 id），
/// 随 <see cref="ChatNotice.ItemRecords"/> 一起投递，不在服务端改写。
/// </summary>
internal sealed record ChatHyperlink(ChatSend Request, byte[] Text, byte[] Records)
{
    public byte Kind => Request.Type;
}

/// <summary>
/// 聊天与城镇同屏共用的场景身份：频道 + town/area 解析出的 PVF 地图 + 房间账号（房间按账号隔离）。
/// <paramref name="Instance"/> 只在副本/选图/结算阶段使用，取本角色副本实例的 <c>InstanceId</c>：
/// 两个角色坐标相同但分属不同副本实例时不得互相收到普通聊天。城镇阶段为 <see cref="Guid.Empty"/>。
/// </summary>
internal readonly record struct ChatScene(ChannelIdentity Channel, byte Town, byte Area, string Map, string? RoomAccount, Guid Instance);

/// <summary>
/// 发送者快照：在 <see cref="Game.Session.GameSession"/> 的账号锁内取得，供锁外的投递选择使用。
/// <paramref name="Name"/> 是角色名，两种喇叭按 <c>发言人 + 正文</c> 两段填 <c>NOTI 0x0076</c>。
/// </summary>
internal sealed record ChatOrigin(ushort CharacterId, string Name, long? GuildId, ChatScene Scene)
{
    public byte ChannelNumber => Scene.Channel.Number;
    public string AdventureName { get; init; } = "";
    public Func<bool>? IsCurrent { get; init; }
}

/// <summary>接收者快照：同样在目标会话的账号锁内取得，<see cref="Account"/> 用于公会成员表与实际在线会话对齐。</summary>
internal sealed record ChatRecipient(string Account, ushort CharacterId, long? GuildId, ChatScene Scene);

/// <summary>
/// 喇叭文本：<c>NOTI 0x0076</c> 的发言人（<c>str1</c>）与正文（<c>str2</c>）。
/// CP936 编码、三条串必须非空与 29/511/16 字节上限由 Network 侧 <c>MegaphonePackets</c> 负责，
/// 领域层只给出未编码的字符串。
/// </summary>
internal sealed record MegaphoneText(string Speaker, string Text)
{
    public ChannelIdentity Channel { get; init; }
    public ushort Actor { get; init; }
}

/// <summary>按角色名投递的悄悄话或1vs1消息，接收方身份在发送前复验。</summary>
internal sealed record NamedChatMessage(ChatMessageType Mode, byte Status, string Name, byte Server, string Text)
{
    public string AdventureName { get; init; } = "";
    public byte GuildFlag { get; init; }
    public byte GuildColorFlag { get; init; }
}

/// <summary>
/// 一次聊天投递的领域描述，不含 opcode 与字节布局（Game 层不引用 Network，由 Network/Projection 翻译）：
/// 普通/队伍使用12，具名/公会使用65；来源身份由领域快照提供，不信任请求编号。
/// 两种喇叭用 <see cref="Megaphone"/> 生成 <c>NOTI 0x0076</c>（客户端「喇叭」页签的一行，已实测可见），
/// 此时 <see cref="Text"/> 不参与编码。
/// <see cref="TextBytes"/> 只给需要原样转发的字节正文（<c>CMD 0x01A8</c> 物品链接）：
/// 这些字节已经是客户端格式化好的 CP936 链接文本（含 <c>0x7F</c> 标记），下行时不得重新解码再编码，
/// 否则非法或可映射到不同字节的字符会被改写；此时 <see cref="Text"/> 为空、不参与编码。
/// <see cref="ItemRecords"/> 是同一请求里的 <c>cnt × 148</c> 字节物品记录（只读、按连接投递共用同一份），
/// 由Network侧按消息类别和完整标记决定370/371/372，记录不足只退化到对应文字通道；
/// 放在通知里而不是投递时再读会话快照，避免并发 / 连续两条链接互相覆盖。
/// <see cref="Speaker"/> 是发送者角色名：<c>0x172</c> 的尾串（客户端存进 <c>msg+0x44</c> → link run <c>+0xB4</c>，
/// 渲染为行首「角色名：」），缺失或发占位时整行显示为无名；普通 <c>0x0C</c> 也保留此发言人尾串。
/// GM <c>notice</c> 是管理入口，由 Development 侧直接构造 <c>NOTI 0x06AD</c> 弹框，不经过本记录。
/// </summary>
internal sealed record ChatNotice(ChatKind Kind, ushort Layer, byte Channel, string Text = "",
    MegaphoneText? Megaphone = null, byte[]? TextBytes = null, byte[]? ItemRecords = null, string Speaker = "")
{
    public NamedChatMessage? Named { get; init; }
    public Func<bool>? Guard { get; init; }
    public ChatOrigin? Origin { get; init; }
}

/// <summary>
/// 投递结果。<paramref name="Recipients"/> 是已交给各连接发送队列的会话数（不等于已写到 socket）；
/// <paramref name="LayerFallback"/> 表示 <c>0x000C</c> 的层 id 是发送者兜底而不是收件人自己的层 id；
/// <paramref name="TargetOffline"/> 只用于悄悄话目标不在线（当前没有可用的失败包，只记日志）。
/// </summary>
internal sealed record ChatDispatch(bool Accepted, int Recipients, bool LayerFallback = false,
    bool TargetOffline = false, string? Rejection = null)
{
    public static ChatDispatch Rejected(string reason) => new(false, 0, Rejection: reason);
}
