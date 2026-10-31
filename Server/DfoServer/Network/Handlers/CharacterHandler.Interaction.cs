using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class CharacterHandler
{
    async Task<CommandResult> NameHistoryAsync(GameConnectionContext context, Packet request)
    {
        if (!CharacterRequests.TryNameHistory(request.Body, out ushort target)) return CommandResult.Unhandled;
        var source = context.Session.OnlineCharacter();
        long epoch = context.Session.SelectionEpoch;
        if (source is not { Ready: true } || online is null) return CommandResult.Handled;
        // 在目标自己的owner内查询，发送守卫只读失效标记，避免跨账号嵌套锁。
        var history = online.FindReadyCharacter(target)?.NameHistory(target);
        if (history is null) return CommandResult.Handled;
        bool Current() => context.Session.Ready && context.Session.SelectionEpoch == epoch &&
            context.Session.Selected?.Id == source.CharacterId && history.Target.IsUnchanged();
        await context.Notify(NotiPacketType.CHARAC_NAME_CHANGE_LOG,
            CharacterNameHistoryPackets.List(history.Names, history.Target.Channel.ServerIndex), "character_name_history", Current);
        return CommandResult.Handled;
    }

    async Task<CommandResult> OtherPlayerAsync(GameConnectionContext context, Packet request)
    {
        if (CharacterRequests.TryNameHistory(request.Body, out _)) return await NameHistoryAsync(context, request);
        if (!CharacterRequests.TryOtherPlayer(request.Body, out ushort target)) return CommandResult.Unhandled;
        var source = context.Session.OnlineCharacter();
        long epoch = context.Session.SelectionEpoch;
        if (source is not { Ready: true } || online is null) return CommandResult.Handled;
        var other = online.PublicCharacter(target);
        if (other is null) return CommandResult.Handled;
        bool Current() => context.Session.Ready && context.Session.SelectionEpoch == epoch &&
            context.Session.Selected?.Id == source.CharacterId && other.IsUnchanged();
        await projection.OtherPlayerAsync(context, other, Current);
        return CommandResult.Handled;
    }
}
