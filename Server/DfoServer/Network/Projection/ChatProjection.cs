using DfoServer.Game.Chat;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

/// <summary>
/// 聊天投递的网络侧：把 <see cref="ChatNotice"/> 翻译成下行帧并入目标连接自己的发送队列。
/// 普通/公会/悄悄话用 <c>NOTI 0x000C</c>（正文 = <c>[显示 type][层 id][v=0][u32 CP936 字节数][CP936 文本]</c>，
/// 见 <see cref="ChatPackets.Message(ChatKind,ushort,string)}；显示 type 由 <see cref="ChatWire"/> 映射，
/// 普通/队伍/悄悄话 = 3、公会/攻坚队 = 6）。带 <see cref="ChatNotice.TextBytes"/> 的物品链接由
/// <see cref="ItemLinkPackets.Design"/> 决策：文本与 148 字节记录配对时发 <c>NOTI 0x172 MESSAGE_HYPER_LINK</c>
/// （收件人可点击查看装备）；记录缺失/条数不足或截断造成标记不成对时退化 <c>NOTI 0x000C</c> 纯文本
/// （现有"看得见但不可点"行为）。<c>0x172</c> 的尾串取 <see cref="ChatNotice.Speaker"/>：
/// 客户端用它补行首「角色名：」，发占位会显示为无名。带 <see cref="ChatNotice.Megaphone"/> 的两种喇叭用
/// 已实测可见的 <c>NOTI 0x0076</c> 喇叭页签（<c>发言人 : 正文</c>，CP936，三条串由 Builder 保证非空）。
/// 调用方已在目标会话的账号锁内复核过身份，这里只做非阻塞入队；
/// 发送泵在写出前还会用本连接的 owner 再检查一次"仍是当前在线角色"（接管后的旧连接不再出帧），
/// 网络写入、sequence 与日志都由该连接的发送泵完成。
/// </summary>
internal static class ChatProjection
{
    public static void Notice(GameConnectionContext context, ChatNotice notice)
    {
        bool CanSend() => context.Session.Ready && context.Session.Selected is not null && (notice.Guard?.Invoke() ?? true);
        if (notice.Named is { } named)
        {
            context.Sender.Post(0, (ushort)NotiPacketType.MESSAGE_OTHER_CHANNEL,
                ChatPackets.NamedMessage(named), "chat_named_message", CanSend);
            return;
        }
        if (notice.Megaphone is { } text)
        {
            context.Sender.Post(0, (ushort)NotiPacketType.MEGAPHONE_MSG,
                MegaphonePackets.Build(notice.Kind, text.Speaker, text.Text), "chat_megaphone", CanSend);
            return;
        }

        if (notice.TextBytes is { } bytes)
        {
            var plan = ItemLinkPackets.Design(notice.Kind, notice.Layer, notice.Speaker, bytes, notice.ItemRecords);
            context.Sender.Post(0, plan.HyperLink ? (ushort)NotiPacketType.MESSAGE_HYPER_LINK : (ushort)NotiPacketType.MESSAGE,
                plan.Body, "chat_hyperlink", CanSend);
            return;
        }

        context.Sender.Post(0, (ushort)NotiPacketType.MESSAGE,
            ChatPackets.Message(notice.Kind, notice.Layer, notice.Text), "chat_message", CanSend);
    }
}
