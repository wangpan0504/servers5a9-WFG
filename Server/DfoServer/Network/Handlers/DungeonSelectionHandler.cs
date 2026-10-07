using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;

namespace DfoServer.Network.Handlers;

// 正式与诊断实例共用已经确认的选图界面入口，不负责建立副本实例。
internal sealed class DungeonSelectionHandler(ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.ENTER_SELECT_DUNGEON, EnterAsync, nameof(DungeonSelectionHandler));

    async Task<CommandResult> EnterAsync(GameConnectionContext context, Packet request)
    {
        var entry = DungeonRequests.ParseEntry(request.Body);
        if (!context.Session.Ready || context.Session.Selected is null)
            throw new InvalidDataException("Invalid dungeon-selection request");
        // 正文 u32 为 0 是左侧入口打开选图；非 0 是已经选中的副本编号。再推空列表会把选图界面重新打开。
        uint dungeon = entry.DungeonIndex;
        log.Write("dungeon_entry", new { Connection = context.Id, Accepted = true, Dungeon = dungeon, Reason = dungeon == 0 ? "select-notice" : "dungeon-chosen", context.Session.Selected.Position });
        await context.Reply(CmdPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionOpened(), "dungeon_entry_opened");
        if (dungeon == 0)
            await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.EmptySelectNotice(), "dungeon_select_notice");
        return CommandResult.Handled;
    }

}
