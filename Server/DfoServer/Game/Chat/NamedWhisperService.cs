using DfoServer.Game.Session;

namespace DfoServer.Game.Chat;

internal sealed class NamedWhisperService(OnlineSessionDirectory online, DfoServer.Game.Social.ISocialStore store)
{
    public static ChatMessageType? ResponseType(byte type) => (ChatRequestType)type switch
    {
        ChatRequestType.Whisper => ChatMessageType.Whisper,
        ChatRequestType.WhisperReply => ChatMessageType.WhisperReply,
        ChatRequestType.OneToOne or ChatRequestType.OneToOneReply => ChatMessageType.OneToOne,
        _ => null
    };

    public ChatDispatch Send(GameSession source, ChatSend request, byte[]? textBytes = null, byte[]? records = null)
    {
        var response = ResponseType(request.Type);
        if (response is null) return ChatDispatch.Rejected("unsupported-named-type");
        var actor = source.ChatOrigin(includeAdventureName: true);
        if (actor is null || request.TargetName is null) return ChatDispatch.Rejected("no-character");
        if (request.TargetServer != 0 && request.TargetServer != actor.Scene.Channel.ServerIndex)
            return new(false, 0, TargetOffline: true);
        var target = online.Snapshot().FirstOrDefault(row => row.Ready && row.Name == request.TargetName);
        if (target is null || online.Find(target.Account) is not { } session) return new(false, 0, TargetOffline: true);
        var peer = session.ChatOrigin();
        if (peer is null || peer.CharacterId != target.CharacterId) return new(false, 0, TargetOffline: true);
        if (peer.Scene.Channel.ServerIndex != actor.Scene.Channel.ServerIndex) return new(false, 0, TargetOffline: true);
        if (store.IsBlocked(target.CharacterId, actor.CharacterId)) return ChatDispatch.Rejected("blacklisted");
        var notice = new ChatNotice(ChatKind.Whisper, actor.CharacterId, actor.ChannelNumber)
        {
            Named = new(response.Value,0, actor.Name, actor.Scene.Channel.ServerIndex,
                textBytes is null ? ChatText.Truncate(request.Text, out _) : "") { AdventureName = actor.AdventureName },
            TextBytes = textBytes, ItemRecords = records,
            Origin = actor,
            Guard = () => (actor.IsCurrent?.Invoke() ?? false) && (peer.IsCurrent?.Invoke() ?? false)
        };
        bool delivered = session.TryDeliverChat(row => row.CharacterId == target.CharacterId, notice);
        return delivered ? new(true, 1) : new(false, 0, TargetOffline: true);
    }
}
