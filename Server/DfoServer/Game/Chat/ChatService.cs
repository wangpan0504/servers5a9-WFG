namespace DfoServer.Game.Chat;

/// <summary>
/// 聊天投递合同：按快照过滤 + 每会话锁内复核。单元测试用假实现直接验证路由规则，
/// 生产实现见 <see cref="ChatDelivery"/>（在线会话目录 + 每连接发送队列）。
/// </summary>
internal interface IChatDelivery
{
    /// <summary>遍历在线会话；filter 只在目标账号锁内比较快照字段，返回已入队的会话数。</summary>
    int DeliverAll(Func<ChatRecipient, bool> filter, ChatNotice notice);

    /// <summary>只投给指定账号的在线会话（悄悄话按角色解析出账号后使用）；未命中返回 false。</summary>
    bool DeliverTo(string account, Func<ChatRecipient, bool> filter, ChatNotice notice);
}

/// <summary>聊天只读存储合同：悄悄话按角色 id 解析账号，公会聊天按公会 id 取成员账号。</summary>
internal interface IChatDirectoryStore
{
    /// <summary>角色所属账号；角色不存在返回 null。</summary>
    string? AccountOfCharacter(ushort characterId);

    /// <summary>公会成员账号（<c>guild_members JOIN characters</c>）；公会不存在或没有成员返回空数组。</summary>
    string[] GuildMemberAccounts(long guildId);
}

/// <summary>
/// 聊天投递计划：把 <see cref="ChatSend"/> 翻译成"投给谁"的规则，只读快照与成员表，不碰物品、不发包。
/// 物品校验与扣减在调用方（Network/Handlers 的聊天处理器）完成（Game 层不依赖 Network）；
/// 两种喇叭只有在扣减成功后才调用这里，所以本类不重复检查库存。
/// <para>
/// 协议事实（反汇编 + 实机收包，见 <c>Docs/协议/聊天.md</c>）：<c>NOTI 0x000C</c> 正文是
/// <c>[u8 显示 type][u16 层 id][u8 v][u32 CP936 字节数][CP936 文本]</c>，客户端按长度读文本（<c>0 &lt; len &lt; 0x100</c>）；
/// 显示 type 由 <see cref="ChatWire"/> 映射（普通/队伍/悄悄话 = 3、公会/攻坚队 = 6，蓝/绿/紫 kind 在「普通」页签不渲染）；
/// <c>type==3</c> 时客户端还要求层 id 等于收件人自己的当前层 id，否则丢弃。两种喇叭走已实测可见的
/// <c>NOTI 0x0076</c> 喇叭页签（发言人 + 正文，<c>0x0E</c>/<c>0x0B</c> 类型由 Network 侧 Builder 选定，
/// 三条串非空与 29/511/16 字节上限也在那里截断）。物品链接 <c>CMD 0x01A8</c> 的正文由
/// <see cref="BroadcastHyperlink"/> 原样投递，不做 CP936 往返；文本与 148 字节记录一起进
/// <see cref="ChatNotice"/>，由 Network 侧决定发 <c>0x172</c>（可点击）还是退化 <c>0x0C</c>。
/// </para>
/// </summary>
internal sealed class ChatService(IChatDirectoryStore directory, IChatDelivery delivery)
{
    // 实机样本里两种喇叭 arg 分别是背包槽位，道具 id 由客户端资源固定为 36/37。
    public const int ServerMegaphoneItemId = 36;
    public const int ChannelMegaphoneItemId = 37;

    /// <summary>把请求 type 翻译成逻辑种类；未定义的 type 返回 false，由调用方按未处理拒绝。</summary>
    public static bool TryKind(byte type, out ChatKind kind)
    {
        kind = (ChatKind)type;
        return Enum.IsDefined(kind);
    }

    /// <summary>喇叭对应的背包道具 id；非喇叭返回 0（调用方据此不做扣减）。</summary>
    public static int MegaphoneItemId(ChatKind kind) => kind switch
    {
        ChatKind.ServerMegaphone => ServerMegaphoneItemId,
        ChatKind.ChannelMegaphone => ChannelMegaphoneItemId,
        _ => 0
    };

    /// <summary>
    /// 下行正文：沿用 <see cref="ChatText.MaxBytes"/>（200 字节）按字符边界截断，与 <c>chat_send</c> 日志里的
    /// 实际文本一致；Network 侧 <c>0x000C</c> 的 255 字节上限只作协议防御，正常路径到不了。
    /// </summary>
    static string WireText(ChatSend request) => ChatText.Truncate(request.Text, out _);

    /// <summary>
    /// 通知正文：普通文本走 <see cref="WireText"/>；物品链接的已编码字节走 <see cref="ChatNotice.TextBytes"/>，
    /// 不做 CP936 解码再编码（客户端给出的 <c>0x7F</c> 链接文本必须逐字节保真）。
    /// <paramref name="itemRecords"/> 是同一请求的 <c>148</c> 字节记录，随通知一起投递，避免投递时再读会话快照。
    /// <see cref="ChatNotice.Speaker"/> 取发送者角色名：Network 侧把它写成 <c>0x172</c> 的尾串（行首发言人），
    /// 不从"最近说话的人"等不可靠来源推断。
    /// </summary>
    static ChatNotice NoticeOf(ChatKind kind, ChatSend request, ChatOrigin sender, byte[]? textBytes,
        byte[]? itemRecords = null) =>
        new(kind, request.Layer, sender.ChannelNumber, Text: textBytes is null ? WireText(request) : "",
            TextBytes: textBytes, ItemRecords: itemRecords, Speaker: sender.Name);

    public ChatDispatch Broadcast(ChatSend request, ChatOrigin sender)
    {
        if (!TryKind(request.Type, out var kind))
            return ChatDispatch.Rejected("unsupported-type");
        return kind switch
        {
            ChatKind.Normal => Normal(request, sender),
            ChatKind.Guild => Guild(request, sender),
            ChatKind.Whisper => Whisper(request, sender),
            ChatKind.ServerMegaphone => Megaphone(sender, ChatKind.ServerMegaphone, request.Text),
            ChatKind.ChannelMegaphone => Megaphone(sender, ChatKind.ChannelMegaphone, request.Text),
            _ => ChatDispatch.Rejected("unsupported-type")
        };
    }

    /// <summary>
    /// 物品链接 <c>CMD 0x01A8</c>：正文已经是客户端格式化好的链接文本（含 <c>0x7F</c> 标记的 CP936 字节），
    /// 服务端不解析物品、也不做 CP936 → 宽字符 → CP936 的往返，按 kind 的既有范围原样广播，
    /// 并把 <c>148</c> 字节物品记录随通知一起带给 Network 侧，由它构造可点击的 <c>NOTI 0x172</c>
    /// （记录缺失或与标记不配对时退化回 <c>0x0C</c> 纯文本，见 <c>ItemLinkPackets.Design</c>）。
    /// 424 只带 kind、不带目标角色，所以只支持普通（3）与公会（6）；悄悄话没有目标参数，
    /// 队伍/攻坚队聊天入口尚未实现，全部拒绝而不猜投递范围。
    /// </summary>
    public ChatDispatch BroadcastHyperlink(byte type, ushort layer, byte[] text, byte[] records, ChatOrigin sender)
    {
        if (!TryKind(type, out var kind))
            return ChatDispatch.Rejected("unsupported-type");
        var request = new ChatSend(type, layer, 0, (uint)text.Length, "");
        return kind switch
        {
            ChatKind.Normal => Normal(request, sender, text, records),
            ChatKind.Guild => Guild(request, sender, text, records),
            ChatKind.Whisper => ChatDispatch.Rejected("hyperlink-target-missing"),
            _ => ChatDispatch.Rejected("unsupported-type")
        };
    }

    /// <summary>
    /// 普通聊天：只投递同场景（频道 + town/area 的 PVF 地图 + 房间账号 + 副本实例）的在线角色，
    /// 单人时就是自己一个人。层 id 用发送者的兜底：服务端没有收件人当前层 id 的权威来源，
    /// 客户端 type==3 的过滤会把层 id 不匹配的通知丢掉——这是已知缺陷，日志里以 LayerFallback 标记。
    /// </summary>
    ChatDispatch Normal(ChatSend request, ChatOrigin sender, byte[]? textBytes = null, byte[]? itemRecords = null)
    {
        var notice = NoticeOf(ChatKind.Normal, request, sender, textBytes, itemRecords);
        int recipients = delivery.DeliverAll(recipient => recipient.Scene == sender.Scene, notice);
        return new(true, recipients, LayerFallback: recipients > 1);
    }

    /// <summary>
    /// 公会聊天：成员来源是只读 SQL 的成员账号表（不在内存另建第二套公会成员缓存）；
    /// 投递前仍在每个目标会话自己的账号锁内复核 <c>Selected.Guild?.Id</c> 一致，
    /// 成员表已变而会话快照未变时不得投递。
    /// </summary>
    ChatDispatch Guild(ChatSend request, ChatOrigin sender, byte[]? textBytes = null, byte[]? itemRecords = null)
    {
        if (sender.GuildId is not { } guildId)
            return ChatDispatch.Rejected("not-in-guild");
        var accounts = new HashSet<string>(directory.GuildMemberAccounts(guildId), StringComparer.Ordinal);
        var notice = NoticeOf(ChatKind.Guild, request, sender, textBytes, itemRecords);
        int recipients = delivery.DeliverAll(recipient => recipient.GuildId == guildId && accounts.Contains(recipient.Account), notice);
        return new(true, recipients);
    }

    /// <summary>
    /// 悄悄话：<c>u32 arg</c> 是目标角色 id。先按 id 解析账号找到在线会话，再只投给目标角色。
    /// 目标不在线时当前没有合适的失败包，只由调用方记 <c>chat_whisper_offline</c>，不自造包。
    /// </summary>
    ChatDispatch Whisper(ChatSend request, ChatOrigin sender)
    {
        if (request.Arg is 0 or > ushort.MaxValue)
            return ChatDispatch.Rejected("target-range");
        ushort target = (ushort)request.Arg;
        var notice = NoticeOf(ChatKind.Whisper, request, sender, null);
        string? account = directory.AccountOfCharacter(target);
        return account is not null && delivery.DeliverTo(account, recipient => recipient.CharacterId == target, notice)
            ? new(true, 1)
            : new(false, 0, TargetOffline: true);
    }

    /// <summary>
    /// 两种喇叭：走已实测可见的 <c>NOTI 0x0076</c> 喇叭页签（<c>发言人 : 正文</c>，类型由 Network 侧按种类选定：
    /// 服务器喇叭 <c>0x0E</c> 官方标签、频道喇叭 <c>0x0B</c> 频道标签）。服务器喇叭投全服在线，频道喇叭只投同频道。
    /// </summary>
    ChatDispatch Megaphone(ChatOrigin sender, ChatKind kind, string text)
    {
        var notice = new ChatNotice(kind, 0, sender.ChannelNumber, Megaphone: new MegaphoneText(sender.Name, text));
        int recipients = kind == ChatKind.ServerMegaphone
            ? delivery.DeliverAll(_ => true, notice)
            : delivery.DeliverAll(recipient => recipient.Scene.Channel == sender.Scene.Channel, notice);
        return new(true, recipients);
    }
}
