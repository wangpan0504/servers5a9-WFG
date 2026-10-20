using DfoServer.Game.Session;

namespace DfoServer.Game.Chat;

/// <summary>
/// 生产投递实现：全服/按条件投递走 <see cref="OnlineSessionDirectory.DeliverAll"/>，
/// 悄悄话按账号用 <see cref="OnlineSessionDirectory.Find"/> 定位会话。
/// 两种入口都只在该会话自己的账号锁内复核并取回调，回调在锁外执行。
/// </summary>
internal sealed class ChatDelivery(OnlineSessionDirectory online) : IChatDelivery
{
    public int DeliverAll(Func<ChatRecipient, bool> filter, ChatNotice notice) => online.DeliverAll(filter, notice);

    public bool DeliverTo(string account, Func<ChatRecipient, bool> filter, ChatNotice notice) =>
        online.Find(account) is { } session && session.TryDeliverChat(filter, notice);
}
