using DfoServer.Game.Social;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class SocialProjection(SocialService service, ISocialStore store)
{
    public void Initialize(GameConnectionContext context)
    {
        // 12B6830会创建并加载隐藏好友面板，1702填充分组；否则右键5DB窗口读取空下拉项。
        if (context.Session.CaptureSocialActor() is not { } actor) return;
        var groups = store.AccountGroups(actor);
        context.Sender.Post(0, (ushort)NotiPacketType.ACCOUNT_FRIEND_GROUP_INFO, SocialPackets.AccountGroups(groups),
            "account_friend_groups", actor.IsCurrent);
        service.Refresh(context.Owner.Account);
        if (context.Session.Selected is { } c) service.PresenceChanged(c.Id, context.Owner.Account);
    }
    public static void Notice(GameConnectionContext context, SocialView view)
    {
        context.Sender.Post(0, (ushort)NotiPacketType.UNITED_SERVER_FRIEND_INFO, SocialPackets.CharacterFriends(view.Characters),
            "character_friends", view.IsCurrent);
        // 273子命令0静默恢复含频道的完整列表；274会直接显示上下线提示，只能回放本次事件目标。
        foreach (var row in view.Characters.Where(row => row.Link.Person.Id == view.ChangedCharacter))
            context.Sender.Post(0, (ushort)NotiPacketType.INOUT_UNITED_SERVER_FRIEND, SocialPackets.CharacterPresence(row),
                "character_friend_presence", view.IsCurrent);
        context.Sender.Post(0, (ushort)NotiPacketType.ACCOUNT_FRIEND_INFO, SocialPackets.AccountFriends(view.Accounts),
            "account_friends", view.IsCurrent);
        context.Sender.Post(0, (ushort)NotiPacketType.ACCOUNT_FRIEND_REQUEST_INFO, SocialPackets.Requests(view.Requests),
            "account_friend_requests", view.IsCurrent);
    }
}
