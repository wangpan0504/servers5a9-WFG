using DfoServer.Game.Session;

namespace DfoServer.Game.Chat;

internal sealed class NamedWhisperService(OnlineSessionDirectory online, DfoServer.Game.Social.ISocialStore store)
{
    public ChatDispatch Send(GameSession source, ChatSend request)
    {
        var actor = source.OnlineCharacter();
        if (actor is not { Ready: true } || request.TargetName is null) return ChatDispatch.Rejected("not-ready");
        var identity = source.PublicCharacter(actor.CharacterId);
        if (identity is null) return ChatDispatch.Rejected("not-ready");
        if (request.TargetServer != 0 && request.TargetServer != identity.Channel.ServerIndex)
            return new(false, 0, TargetOffline: true);
        var target = online.Snapshot().FirstOrDefault(row => row.Ready && row.Name == request.TargetName);
        if (target is null || online.Find(target.Account) is not { } session) return new(false, 0, TargetOffline: true);
        var peer = session.PublicCharacter(target.CharacterId);
        if (peer is null) return new(false, 0, TargetOffline: true);
        if (store.IsBlocked(target.CharacterId, actor.CharacterId)) return ChatDispatch.Rejected("blacklisted");
        var notice = new ChatNotice(ChatKind.Whisper, request.Layer, identity.Channel.Number)
        {
            Named = new(request.Type, 0, actor.Name, identity.Channel.ServerIndex,
                ChatText.Truncate(request.Text, out _), target.Name),
            Guard = () => identity.IsCurrent() && peer.IsCurrent()
        };
        bool delivered = session.TryDeliverChat(row => row.CharacterId == target.CharacterId, notice);
        return delivered ? new(true, 1) : new(false, 0, TargetOffline: true);
    }
}
