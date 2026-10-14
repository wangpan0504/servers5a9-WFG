using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class CubeContractHandler(CubeContractService service, InventoryProjection projection,
    Infrastructure.ServerLog log, TimeProvider? time = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.UPDATE_CONTRACT_OF_CUBE_INFO,
        SelectAsync, nameof(CubeContractHandler));

    async Task<CommandResult> SelectAsync(GameConnectionContext context, Packet packet)
    {
        DfoServer.Game.Inventory.Models.CubeContractState request;
        try { request = CubeContractRequests.Read(packet.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        var result = context.Session.PlanAndCommitInventory(character => service.Select(character, request,
            (time ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()), requireTown: false);
        log.Write("cube_contract_select", new { Connection = context.Id, request.Selected, request.CubeType, result.Accepted });
        await context.Reply(CmdPacketType.UPDATE_CONTRACT_OF_CUBE_INFO, CubeContractPackets.Result(request, result.Accepted, result.Error),
            result.Accepted ? "cube_contract_selected" : "cube_contract_rejected",
            result.Receipt is { } receipt ? () => context.Session.CanPublishInventory(receipt) : null);
        if (result.Accepted) await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
