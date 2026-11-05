using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class EpicExchangeHandler(EpicExchangeService exchange, InventoryProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.EXCHANGE_EPIC_ITEM, Handle, nameof(EpicExchangeHandler));
        commands.Register(CmdPacketType.REWARD_EPIC_EXCHANGE, HandleReward, nameof(EpicExchangeHandler));
    }

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var request = EpicExchangeRequests.Exchange(packet.Body);
        var result = context.BlocksTownActions ? InventoryResult.Reject(4) :
            context.Session.MutateInventory(character =>
                exchange.Exchange(character, request.SourceItemId, request.TargetItemId, request.Slot), 4);
        await context.Reply(CmdPacketType.EXCHANGE_EPIC_ITEM, EpicExchangePackets.Exchanged(result),
            result.Accepted ? "epic_exchange" : "epic_exchange_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> HandleReward(GameConnectionContext context, Packet packet)
    {
        var request = EpicExchangeRequests.RewardExchange(packet.Body);
        var result = context.BlocksTownActions ? InventoryResult.Reject(4) :
            context.Session.MutateInventory(character =>
                exchange.ExchangeReward(character, request.SourceItemId, request.Mode, request.TargetItemId), 4);
        await context.Reply(CmdPacketType.REWARD_EPIC_EXCHANGE, EpicExchangePackets.Exchanged(result),
            result.Accepted ? "reward_epic_exchange" : "reward_epic_exchange_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
