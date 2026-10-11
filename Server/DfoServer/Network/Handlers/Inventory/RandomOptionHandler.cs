using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class RandomOptionHandler(InventoryRandomOptionService options, InventoryProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.UNSEAL_RANDOM_OPTION, Handle, nameof(RandomOptionHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        UnsealRandomOptionRequest request;
        try { request = RandomOptionRequests.Read(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.UNSEAL_RANDOM_OPTION, RandomOptionPackets.Status(false), "random_option_malformed");
            return CommandResult.Handled;
        }
        var result = context.Session.PlanAndCommitInventory(character => options.Unseal(character, request), requireTown: false);
        log.Write(result.Accepted ? "random_option_unseal" : "random_option_rejected", new { Connection = context.Id, Slot = request.Slot,
            result.Accepted, Result = result.Accepted ? "committed" : "rejected", Revision = result.Receipt?.Character.Revision });
        // 原生窗口先读取已提交的词条和金币，再结束解封等待；不重建全量背包。
        await projection.RefreshAsync(context, result);
        await context.Reply(CmdPacketType.UNSEAL_RANDOM_OPTION, RandomOptionPackets.Status(result.Accepted),
            result.Accepted ? "random_option_unsealed" : "random_option_rejected",
            result.Receipt is { } receipt ? () => context.Session.CanPublishInventory(receipt) : null);
        return CommandResult.Handled;
    }
}
