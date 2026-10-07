using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;

namespace DfoServer.Network.Handlers;

// 正式与诊断实例共用已经确认的选图界面入口，不负责建立副本实例。
internal sealed class DungeonSelectionHandler(TownProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.ENTER_SELECT_DUNGEON, EnterAsync, nameof(DungeonSelectionHandler));
        commands.Register(CmdPacketType.BACK_2_VILLAGE, ReturnAsync, nameof(DungeonSelectionHandler));
    }

    async Task<CommandResult> EnterAsync(GameConnectionContext context, Packet request)
    {
        var entry = DungeonRequests.ParseEntry(request.Body);
        if (context.BlocksTownActions)
            return CommandResult.Unhandled;
        var receipt = context.Session.BeginDungeonSelection();
        if (!await projection.DetachForDungeonSelectionAsync(context, receipt))
            return CommandResult.Handled;
        bool CanSend() => context.Session.CanPublishDungeonSelection(receipt);
        // 正文 u32 为 0 是左侧入口打开选图；非 0 是已经选中的副本编号。再推空列表会把选图界面重新打开。
        uint dungeon = entry.DungeonIndex;
        log.Write("dungeon_entry", new { Connection = context.Id, Accepted = true, Dungeon = dungeon, Reason = dungeon == 0 ? "select-notice" : "dungeon-chosen", receipt.Character.Position, receipt.SelectionId });
        await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "dungeon_entry_opened", CanSend);
        // 同步客户端选图状态，触发旧城镇实体清理；仅切服务端阶段不会结束客户端旧动作。
        await context.Notify(NotiPacketType.USER_STATE, DungeonPackets.DungeonPlayerState(receipt.Character.Id), "dungeon_selection_state", CanSend);
        if (dungeon == 0)
            await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.EmptySelectNotice(), "dungeon_select_notice", CanSend);
        return CommandResult.Handled;
    }

    async Task<CommandResult> ReturnAsync(GameConnectionContext context, Packet request)
    {
        if (!DungeonRequests.IsSelectionReturn(request.Body) || context.BlocksTownActions)
            return CommandResult.Unhandled;
        var receipt = context.Session.ReturnFromDungeonSelection();
        log.Write("dungeon_selection_return", new { Connection = context.Id, Accepted = receipt is not null, receipt?.SelectionId });
        if (receipt is not null)
            await projection.ReturnFromDungeonSelectionAsync(context, receipt);
        return CommandResult.Handled;
    }
}
