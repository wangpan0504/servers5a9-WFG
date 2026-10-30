using DfoServer.Game.Party;
using DfoServer.Game.Session;

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
/// 显示 type 由 <see cref="ChatWire"/> 映射（队伍2、普通/悄悄话3、公会6）；
/// 正式通知使用真实发言人编号和来源服务器分组，不把接收者编号或请求引用当作发言人。两种喇叭走
/// <c>NOTI 0x0076</c> 喇叭页签（发言人 + 正文，type11/13由Network侧Builder选定，
/// 三条串非空与 29/511/16 字节上限也在那里截断）。物品链接 <c>CMD 0x01A8</c> 的正文由
/// 物品链接原样投递，不做 CP936 往返；文本与 148 字节记录一起进
/// <see cref="ChatNotice"/>，由 Network 侧决定发 <c>0x172</c>（可点击）还是退化 <c>0x0C</c>。
/// </para>
/// </summary>
internal sealed class ChatService(IChatDirectoryStore directory, IChatDelivery delivery, DfoServer.Game.Social.ISocialStore? social = null)
{
    int Deliver(ChatOrigin sender, Func<ChatRecipient, bool> filter, ChatNotice notice)
    {
        var blocked = social?.BlockingCharacters(sender.CharacterId);
        return delivery.DeliverAll(recipient => blocked?.Contains(recipient.CharacterId) != true && filter(recipient), notice);
    }
    // 实机样本里两种喇叭 arg 分别是背包槽位，道具 id 由客户端资源固定为 36/37。
    public const int ServerMegaphoneItemId = 36;
    public const int ChannelMegaphoneItemId = 37;

    /// <summary>把请求 type 翻译成逻辑种类；未定义的 type 返回 false，由调用方按未处理拒绝。</summary>
    public static bool TryKind(byte type, out ChatKind kind)
    {
        ChatKind? selected = (ChatRequestType)type switch
        {
            ChatRequestType.Normal => ChatKind.Normal,
            ChatRequestType.Party => ChatKind.Party,
            ChatRequestType.Guild => ChatKind.Guild,
            ChatRequestType.LegacyCharacterWhisper => ChatKind.Whisper,
            ChatRequestType.ServerMegaphone => ChatKind.ServerMegaphone,
            ChatRequestType.ChannelMegaphone => ChatKind.ChannelMegaphone,
            _ => null
        };
        kind = selected ?? default;
        return selected is not null;
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
        new(kind, sender.CharacterId, sender.ChannelNumber, Text: textBytes is null ? WireText(request) : "",
            TextBytes: textBytes, ItemRecords: itemRecords, Speaker: sender.Name) { Origin = sender, Guard = sender.IsCurrent };

    public ChatDispatch Broadcast(ChatSend request, ChatOrigin sender, GameSession? source = null,
        byte[]? textBytes = null, byte[]? itemRecords = null)
    {
        if (!TryKind(request.Type, out var kind))
            return ChatDispatch.Rejected("unsupported-type");
        return kind switch
        {
            ChatKind.Normal => Normal(request, sender, textBytes, itemRecords),
            ChatKind.Party => Party(request, sender, source, textBytes, itemRecords),
            ChatKind.Guild => Guild(request, sender, textBytes, itemRecords),
            ChatKind.Whisper => Whisper(request, sender, textBytes, itemRecords),
            ChatKind.ServerMegaphone or ChatKind.ChannelMegaphone => Megaphone(sender, kind, request.Text, textBytes, itemRecords),
            _ => ChatDispatch.Rejected("unsupported-type")
        };
    }

    /// <summary>
    /// 普通聊天：只投递同场景（频道 + town/area 的 PVF 地图 + 房间账号 + 副本实例）的在线角色，
    /// 单人时仅本人；发送前再次验证接收者的实际场景。
    /// 不信任客户端声明的发言人编号。
    /// </summary>
    ChatDispatch Normal(ChatSend request, ChatOrigin sender, byte[]? textBytes = null, byte[]? itemRecords = null)
    {
        var notice = NoticeOf(ChatKind.Normal, request, sender, textBytes, itemRecords);
        int recipients = Deliver(sender, recipient => recipient.Scene == sender.Scene, notice);
        return new(true, recipients);
    }

    ChatDispatch Party(ChatSend request, ChatOrigin sender, GameSession? source, byte[]? textBytes = null,
        byte[]? itemRecords = null)
    {
        var party = source?.CurrentParty?.Invoke();
        if (party is null) return ChatDispatch.Rejected("not-in-party");
        var members = party.Members.Select(member => (Member: member, Membership: member.Actor.Session.PartyMembershipVersion)).ToArray();
        var own = members.FirstOrDefault(row => ReferenceEquals(row.Member.Actor.Session, source) &&
            row.Member.Actor.Character.Id == sender.CharacterId);
        bool Current((PartyMember Member, long Membership) row) => row.Member.IsCurrent() &&
            row.Member.Actor.Session.PartyMembershipVersion == row.Membership;
        // 读取版本期间发生退队或重新入队时不使用旧名册；之后按各自成员版本复核，不因改队名或资产更新丢消息。
        if (own.Member is null || !ReferenceEquals(source!.CurrentParty?.Invoke(), party) || !Current(own))
            return ChatDispatch.Rejected("stale-party");
        var blocked = social?.BlockingCharacters(sender.CharacterId);
        var notice = NoticeOf(ChatKind.Party, request, sender, textBytes, itemRecords);
        int recipients = 0;
        foreach (var member in members)
        {
            if (blocked?.Contains(member.Member.Actor.Character.Id) == true) continue;
            bool Guard() => Current(own) && Current(member);
            if (!Guard()) continue;
            if (delivery.DeliverTo(member.Member.Actor.Owner.Account,
                recipient => recipient.CharacterId == member.Member.Actor.Character.Id && Guard(), notice with { Guard = Guard }))
                recipients++;
        }
        return new(true, recipients);
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
        notice = notice with { Named = new(ChatMessageType.Guild,0,sender.Name,sender.Scene.Channel.ServerIndex,
            textBytes is null ? WireText(request) : "") { AdventureName = sender.AdventureName } };
        int recipients = Deliver(sender, recipient => recipient.GuildId == guildId && accounts.Contains(recipient.Account) &&
            recipient.Scene.Channel.ServerIndex == sender.Scene.Channel.ServerIndex, notice);
        return new(true, recipients);
    }

    /// <summary>
    /// 悄悄话：<c>u32 arg</c> 是目标角色 id。先按 id 解析账号找到在线会话，再只投给目标角色。
    /// 目标不在线时当前没有合适的失败包，只由调用方记 <c>chat_whisper_offline</c>，不自造包。
    /// </summary>
    ChatDispatch Whisper(ChatSend request, ChatOrigin sender, byte[]? textBytes = null, byte[]? itemRecords = null)
    {
        if (request.Arg is 0 or > ushort.MaxValue)
            return ChatDispatch.Rejected("target-range");
        ushort target = (ushort)request.Arg;
        if (social?.IsBlocked(target, sender.CharacterId) == true) return ChatDispatch.Rejected("blacklisted");
        var notice = NoticeOf(ChatKind.Whisper, request, sender, textBytes, itemRecords);
        string? account = directory.AccountOfCharacter(target);
        return account is not null && delivery.DeliverTo(account, recipient => recipient.CharacterId == target &&
            recipient.Scene.Channel.ServerIndex == sender.Scene.Channel.ServerIndex, notice)
            ? new(true, 1)
            : new(false, 0, TargetOffline: true);
    }

    /// <summary>
    /// 两种喇叭：走已实测可见的 <c>NOTI 0x0076</c> 喇叭页签（<c>发言人 : 正文</c>，类型由 Network 侧按种类选定：
    /// 服务器喇叭type11投同分组的全部频道，频道喇叭type13只投同分组同频道，均携带真实来源身份。
    /// </summary>
    ChatDispatch Megaphone(ChatOrigin sender, ChatKind kind, string text, byte[]? textBytes = null, byte[]? itemRecords = null)
    {
        var notice = new ChatNotice(kind, sender.CharacterId, sender.ChannelNumber,
            Megaphone: new MegaphoneText(sender.Name, text) { Channel = sender.Scene.Channel, Actor = sender.CharacterId })
            { Origin = sender, Guard = sender.IsCurrent, TextBytes = textBytes, ItemRecords = itemRecords };
        int recipients = kind == ChatKind.ServerMegaphone
            ? Deliver(sender, recipient => recipient.Scene.Channel.ServerIndex == sender.Scene.Channel.ServerIndex, notice)
            : Deliver(sender, recipient => recipient.Scene.Channel == sender.Scene.Channel, notice);
        return new(true, recipients);
    }
}
