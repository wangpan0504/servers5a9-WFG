using DfoServer.Game.Chat;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

/// <summary>
/// 聊天投递的网络侧：把 <see cref="ChatNotice"/> 翻译成下行帧并入目标连接自己的发送队列。
/// 文字按普通、具名或喇叭分别使用12/65/118；原字节引用分别使用370/371/372。
/// 记录不足或标记不配对时只退化原通道文字，不改变目标和消息身份。
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
            if (notice.TextBytes is { } namedBytes)
            {
                var plan = ItemLinkPackets.DesignNamed(named, namedBytes, notice.ItemRecords);
                context.Sender.Post(0, (ushort)plan.Opcode, plan.Body, "chat_named_hyperlink", CanSend);
            }
            else
                context.Sender.Post(0, (ushort)NotiPacketType.MESSAGE_OTHER_CHANNEL,
                    ChatPackets.NamedMessage(named), "chat_named_message", CanSend);
            return;
        }
        if (notice.Megaphone is { } text)
        {
            if (notice.TextBytes is { } megaphoneBytes)
            {
                var plan = ItemLinkPackets.DesignMegaphone(notice.Kind, text, megaphoneBytes, notice.ItemRecords);
                context.Sender.Post(0, (ushort)plan.Opcode, plan.Body, "chat_megaphone_hyperlink", CanSend);
            }
            else
                context.Sender.Post(0, (ushort)NotiPacketType.MEGAPHONE_MSG,
                    MegaphonePackets.Build(notice.Kind, text), "chat_megaphone", CanSend);
            return;
        }

        if (notice.TextBytes is { } bytes)
        {
            var plan = ItemLinkPackets.Design(notice.Kind, notice.Layer, notice.Speaker, bytes, notice.ItemRecords,
                notice.Origin?.Scene.Channel.ServerIndex ?? 0);
            context.Sender.Post(0, (ushort)plan.Opcode,
                plan.Body, "chat_hyperlink", CanSend);
            return;
        }

        context.Sender.Post(0, (ushort)NotiPacketType.MESSAGE,
            ChatPackets.Message(notice.Kind, notice.Layer, notice.Text, notice.Speaker,
                notice.Origin?.Scene.Channel.ServerIndex ?? 0), "chat_message", CanSend);
    }
}
