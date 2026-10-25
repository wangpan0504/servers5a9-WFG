using DfoServer.Game.Chat;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

/// <summary>
/// 聊天发送 <c>CMD 0x0011 SEND_MESSAGE</c> 的正式处理（普通/公会/悄悄话 + 服务器/频道喇叭），
/// 以及物品链接 <c>CMD 0x01A8 ITEM_HYPERLINK_MESSAGE</c> 的转发。
/// <para>
/// 协议事实（反汇编 + 实机双向确认）：聊天行 <c>NOTI 0x000C</c> 的正文是
/// <c>[u8 显示 type][u16 层 id][u8 v][u32 CP936 字节数][CP936 文本]</c>，客户端按长度读文本
/// （<c>0 &lt; len &lt; 0x100</c>，否则显示「信息长度有误」）。显示 type 不是入站 kind 的原值：
/// 普通/队伍/悄悄话 = <c>3</c>、公会/攻坚队 = <c>6</c>（实测「普通」页签只渲染 <c>{3,6,0x37,0x38}</c>，
/// 蓝/绿/紫 kind 不显示，见 <see cref="ChatWire"/>）。两种喇叭走已实测可见的 <c>NOTI 0x0076</c> 喇叭页签
/// （发言人 + 正文，服务器喇叭 <c>0x0E</c> 官方标签、频道喇叭 <c>0x0B</c> 频道标签），
/// 由 <see cref="MegaphonePackets"/> 构造、<c>ChatProjection</c> 投递。
/// 物品链接的正文是客户端已格式化的 CP936 链接文本（<c>0x7F 0x7F [物品名] 0x7F</c>）与
/// <c>cnt × 148</c> 字节物品记录；文本与记录都在 <see cref="ChatNotice"/> 里走到投递侧，
/// 由 <see cref="ItemLinkPackets"/> 按 <c>NOTI 0x172</c> 布局组帧（记录缺失/不配对时退化 <c>0x0C</c>），
/// 见 <see cref="ItemHyperlinkAsync"/>。
/// 字节合同见 <c>Docs/协议/聊天.md</c>；层 id 兜底等已知缺陷见 <c>Docs/模块/聊天.md</c>。
/// </para>
/// <para>
/// 本处理器不按文本内容分流：所有合法 <c>0x0011</c> 一律走正式投递
/// （解析 → 每连接节流 → 静音/封禁预留钩子 → CP936 截断到 200 字节 → 喇叭先扣物品 → 投递计划 → 记录实际投递结果）。
/// GM <c>chat-inject</c> 是独立的诊断入口，只向指定在线连接注入测试帧，不经过这里。
/// </para>
/// </summary>
internal sealed class ChatHandler(ChatService chat, InventoryProjection? inventory, ServerLog log,
    NamedWhisperService? named = null) : IGameCommandModule
{
    // 日志里的文本上限：单条聊天不能把热点日志撑大。
    const int LogTextLimit = 64;

    // 物品链接摘要只取前 32 字节 hex：既能看出 0x7F 标记/记录开头，又不把整包复制进热点日志。
    const int LogHexLimit = 32;

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SEND_MESSAGE, SendAsync, nameof(ChatHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.ITEM_HYPERLINK_MESSAGE, ItemHyperlinkAsync, nameof(ChatHandler), UserStateRequirement.WorldReady);
    }

    async Task<CommandResult> SendAsync(GameConnectionContext context, Packet packet)
    {
        if (!ChatRequests.TryParse(packet.Body, out var send))
            return CommandResult.Unhandled;

        // GM 聊天帧注入需要与客户端当前层一致；移动包没有层 id 字段，因此只记聊天上报值。
        context.Session.RememberChatLayer(send.Layer);

        // 超限只丢弃并记 chat_throttled：聊天是被动输入，不能成为断线触发器。
        if (!context.Session.AllowChatSend())
        {
            log.Write("chat_throttled", new { Connection = context.Id, send.Type });
            return CommandResult.Handled;
        }

        if (send.TargetName is not null && named is not null)
        {
            var result = named.Send(context.Session, send);
            log.Write(result.Accepted ? "chat_delivered" : "chat_rejected", new
            {
                Connection = context.Id, Kind = send.Type == 36 ? "OneToOne" : "Whisper",
                result.Recipients, Reason = result.TargetOffline ? "target-offline" : result.Rejection
            });
            return CommandResult.Handled;
        }

        // 静音/封禁预留钩子：玩家管理模块尚未接入，恒不拦截；接入后在这里读取真实状态并记 chat_blocked。
        if (IsChatBlocked(context.Session))
        {
            log.Write("chat_blocked", new { Connection = context.Id, send.Type });
            return CommandResult.Handled;
        }

        // 普通/公会/悄悄话与两种喇叭都走这里；文本内容不参与分流。
        string text = ChatText.Truncate(send.Text, out bool truncated);
        bool known = ChatService.TryKind(send.Type, out var kind);
        uint target = kind is ChatKind.Whisper or ChatKind.ServerMegaphone or ChatKind.ChannelMegaphone ? send.Arg : 0;
        log.Write("chat_send", new
        {
            Connection = context.Id, send.Type, Target = target, send.Layer,
            TextLength = ChatText.ByteCount(text), Text = Clip(text), Truncated = truncated
        });
        if (!known)
        {
            log.Write("chat_rejected", new { Connection = context.Id, Reason = "unsupported-type", send.Type });
            return CommandResult.Handled;
        }

        var origin = context.Session.ChatOrigin();
        if (origin is null)
        {
            log.Write("chat_rejected", new { Connection = context.Id, Reason = "not-ready", send.Type });
            return CommandResult.Handled;
        }

        // 两种喇叭：先校验槽位/道具并走既有背包提交路径扣 1 个；扣减失败不广播，避免"没扣钱也喊话"。
        InventoryResult? deduction = null;
        if (kind is ChatKind.ServerMegaphone or ChatKind.ChannelMegaphone)
        {
            int itemId = ChatService.MegaphoneItemId(kind);
            if (send.Arg > short.MaxValue)
            {
                log.Write("chat_megaphone_rejected", new { Connection = context.Id, Slot = send.Arg, Item = itemId, Reason = "slot-range" });
                return CommandResult.Handled;
            }
            deduction = context.Session.UseChatMegaphone((ushort)send.Arg, itemId);
            if (!deduction.Accepted)
            {
                log.Write("chat_megaphone_rejected", new { Connection = context.Id, Slot = send.Arg, Item = itemId, Reason = "deduct-failed" });
                return CommandResult.Handled;
            }
        }

        var dispatch = chat.Broadcast(send, origin);
        if (dispatch.TargetOffline)
            log.Write("chat_whisper_offline", new { Connection = context.Id, Target = send.Arg });
        else if (dispatch.Rejection is { } reason)
            log.Write("chat_rejected", new { Connection = context.Id, Reason = reason, send.Type });
        else
        {
            log.Write("chat_delivered", new
            {
                Connection = context.Id, Kind = kind.ToString(), dispatch.Recipients, dispatch.LayerFallback
            });
            // 喇叭：记种类、类型字节与编码后的段落长度，便于定位"客户端的喇叭页签为什么没这一行"；
            // 不写正文（正文可能很长，且热点日志不应复制玩家文本）。
            if (kind is ChatKind.ServerMegaphone or ChatKind.ChannelMegaphone)
            {
                var megaphone = MegaphonePackets.Measure(kind, origin.Name, send.Text);
                log.Write("chat_megaphone", new
                {
                    Connection = context.Id,
                    Variant = kind == ChatKind.ServerMegaphone ? "server" : "channel",
                    megaphone.Type, megaphone.SpeakerBytes, megaphone.TextBytes, dispatch.Recipients
                });
            }
        }

        // 喇叭扣减已提交：把实际库存差量发布给本人连接（增量按既有背包投影编码）。
        if (deduction is { Accepted: true } && inventory is not null)
            await inventory.RefreshAsync(context, deduction);
        return CommandResult.Handled;
    }

    /// <summary>
    /// 物品链接 <c>CMD 0x01A8</c>：读 kind → 跳过 6 字节未定头 → <c>u32 文本字节数</c> → 链接文本 →
    /// <c>u8 cnt</c> → <c>cnt × 148</c> 记录字节。校验失败只记 <c>item_hyperlink_rejected</c> 并丢弃，
    /// 不返回 <see cref="CommandResult.CloseConnection"/>：聊天输入不能成为断线触发器。
    /// <para>
    /// 投递：文本与记录一起随 <see cref="ChatNotice"/> 广播（普通按同场景、公会按成员，发送者自己也回显），
    /// Network 侧 <see cref="ChatProjection"/> 用 <see cref="ItemLinkPackets.Design"/> 决定：
    /// 文本里的收尾 <c>0x7F</c> 条数与 148 字节记录配对时发 <c>NOTI 0x172 MESSAGE_HYPER_LINK</c>
    /// （携带记录，收件人可点击查看装备）；记录为空/条数不足/截断后标记不成对时退化 <c>NOTI 0x000C</c>
    /// 纯文本（现有"看得见但不可点"行为）。<c>0x172</c> 的尾串写本连接的角色名（行首发言人），
    /// 不猜其他来源。层 id 取本连接最近一次 <c>0x0011</c> 上报值
    /// （424 头部第 1..6 字节语义未定，不从中猜层 id），没有上报值时写 0；
    /// <c>type==3</c> 的层过滤仍可能让层 id 不同的收件人丢帧，属既有已知限制。
    /// </para>
    /// <para>
    /// 记录也完整保存在 <see cref="GameSession.LastItemHyperlink"/>（只保留最新一条）供诊断复用；
    /// 投递本身以通知里携带的那一份为准，避免连续两条链接在并发投递时读到后一条的记录。
    /// </para>
    /// </summary>
    async Task<CommandResult> ItemHyperlinkAsync(GameConnectionContext context, Packet packet)
    {
        if (!ChatRequests.TryParseHyperlink(packet.Body, out var link, out var error))
        {
            log.Write("item_hyperlink_rejected", new
            {
                Connection = context.Id, Reason = error.ToString(), Length = packet.Body.Length,
                Head = HexHead(packet.Body)
            });
            return CommandResult.Handled;
        }

        // 与普通聊天共用每连接节流：物品链接同样是会向同场景/公会广播的聊天输入，不能绕过限速。
        if (!context.Session.AllowChatSend())
        {
            log.Write("chat_throttled", new { Connection = context.Id, link.Kind, Opcode = (int)CmdPacketType.ITEM_HYPERLINK_MESSAGE });
            return CommandResult.Handled;
        }

        var origin = context.Session.ChatOrigin();
        if (origin is null)
        {
            log.Write("chat_rejected", new { Connection = context.Id, Reason = "not-ready", link.Kind });
            return CommandResult.Handled;
        }

        ushort layer = context.Session.LastChatLayer ?? 0;
        var dispatch = chat.BroadcastHyperlink(link.Kind, layer, link.Text, link.Records, origin);
        if (dispatch.Rejection is { } reason)
        {
            log.Write("item_hyperlink_rejected", new
            {
                Connection = context.Id, Reason = reason, link.Kind, TextBytes = link.Text.Length,
                Records = link.Records.Length / ChatRequests.HyperlinkRecordLength
            });
            return CommandResult.Handled;
        }

        // 只保留实际进入投递的最近一条：记录字节完整留在会话快照里，供后续诊断/点击查看装备复用。
        context.Session.RememberItemHyperlink(link);
        ChatService.TryKind(link.Kind, out var kind);
        var plan = ItemLinkPackets.Design(kind, layer, origin.Name, link.Text, link.Records);
        log.Write("item_hyperlink", new
        {
            Connection = context.Id, Kind = kind.ToString(),
            Opcode = plan.HyperLink ? (int)NotiPacketType.MESSAGE_HYPER_LINK : (int)NotiPacketType.MESSAGE,
            TextBytes = link.Text.Length,
            Records = link.Records.Length / ChatRequests.HyperlinkRecordLength,
            plan.MarkerPairs, plan.Degraded, plan.MarkersStripped, plan.Truncated,
            RecordHex = HexHead(link.Records), Head = HexHead(link.Text), dispatch.Recipients
        });
        return CommandResult.Handled;
    }

    /// <summary>日志摘要：前 <see cref="LogHexLimit"/> 字节的十六进制；空数组返回 <c>"-"</c>。</summary>
    static string HexHead(byte[] bytes) =>
        bytes.Length == 0 ? "-" : Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, LogHexLimit)));

    /// <summary>
    /// 静音/封禁预留：玩家管理、举报与屏蔽列表尚未实现，这里恒不拦截。
    /// 接入时读取真实状态（而不是在聊天 handler 里另建一套状态），并保持"拒绝也要记录原因"的约定。
    /// </summary>
    static bool IsChatBlocked(GameSession session) => false;

    static string Clip(string text) => text.Length <= LogTextLimit ? text : text[..LogTextLimit] + "...";
}
