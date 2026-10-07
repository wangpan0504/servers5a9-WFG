using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;
using DfoServer.Network.Parsers;

namespace DfoServer.Network.Handlers;

// 正式副本的选择入口；完整准入尚未接入，不发送诊断候选或猜测的成功响应。
internal sealed class DungeonHandler(ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.SELECT_DUNGEON, SelectAsync, nameof(DungeonHandler));

    Task<CommandResult> SelectAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.TryParseSelection(request.Body, out var selection))
            return Task.FromResult(CommandResult.Unhandled);
        var state = context.Owner.Execute(() => new
        {
            Character = context.Session.Selected?.Id,
            context.Session.Ready,
            context.Session.Phase,
            context.Session.SelectionEpoch
        });
        // 完整初始化和失败收尾尚未闭合；识别请求不表示建立了副本实例，不能回成功。
        log.Write("dungeon_selection_unavailable", new
        {
            Connection = context.Id,
            Request = selection,
            State = state,
            Reason = state.Character is null || !state.Ready ? "not-ready" : "dungeon-initialization-unimplemented"
        });
        return Task.FromResult(CommandResult.Unhandled);
    }

}
