using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;

namespace DfoServer.Network.Handlers;

// CMD1598空请求；匹配客户端0084FCE0成功读取一个未使用u32后继续15/16，不将其伪称任务ID。
internal sealed class OperationHopeHandler(OperationHopeCatalog? catalog, DungeonCatalog? dungeons, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.SELECT_OPERATION_HOPE_DUNGEON,
        SelectAsync,nameof(OperationHopeHandler),UserStateRequirement.WorldReady);

    async Task<CommandResult> SelectAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0) return CommandResult.Unhandled;
        var before = context.Owner.Execute(() => (context.Session.Selected,context.Session.SelectionEpoch));
        var party = context.Session.CurrentParty?.Invoke();
        using var peers = await PartyDungeonProjection.AcquirePeersAsync(context,party?.Members ?? []);
        bool accepted = catalog is not null && dungeons is not null && context.Activity?.BlocksTownActions != true &&
            context.Session.BeginPartyOperationHope(catalog,dungeons);
        log.Write("operation_hope_selection",new { Connection = context.Id, Accepted = accepted });
        await context.Reply(CmdPacketType.SELECT_OPERATION_HOPE_DUNGEON,accepted ? [1,0,0,0,0] : [0,1,0],
            accepted ? "operation_hope_selected" : "operation_hope_rejected", () => context.Owner.Execute(() =>
                context.Session.SelectionEpoch == before.SelectionEpoch && ReferenceEquals(context.Session.Selected,before.Selected)));
        return CommandResult.Handled;
    }
}
