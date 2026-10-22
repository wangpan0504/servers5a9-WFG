using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class CargoHandler(InventoryCargoService cargo, InventoryProjection projection) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.CREATE_ACCOUNT_CARGO, (context, packet) => AccountUpgrade(context, packet, true), nameof(CargoHandler));
        commands.Register(CmdPacketType.UPGRADE_ACCOUNT_CARGO, (context, packet) => AccountUpgrade(context, packet, false), nameof(CargoHandler));
        commands.Register(CmdPacketType.DEPOSIT_MONEY, (context, packet) => Gold(context, packet, true), nameof(CargoHandler));
        commands.Register(CmdPacketType.WITHDRAW_MONEY, (context, packet) => Gold(context, packet, false), nameof(CargoHandler));
        commands.Register(CmdPacketType.UPGRADE_CARGO, PersonalUpgrade, nameof(CargoHandler));
    }

    async Task<CommandResult> AccountUpgrade(GameConnectionContext context, Packet packet, bool create)
    {
        CargoRequests.AccountUpgrade(packet.Body);
        var original = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        var result = context.BlocksTownActions ? InventoryResult.Reject(21) :
            context.Session.MutateInventory(character => cargo.AccountUpgrade(character, create), 21);
        bool CanSend() => result.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) : context.Owner.Execute(() =>
            context.Session.SelectionEpoch == original.SelectionEpoch && context.Session.Phase == original.Phase &&
            ReferenceEquals(context.Session.Selected, original.Selected));
        await context.Reply(create ? CmdPacketType.CREATE_ACCOUNT_CARGO : CmdPacketType.UPGRADE_ACCOUNT_CARGO,
            CargoPackets.Ack(result), result.Accepted ? "account_cargo_upgraded" : "account_cargo_upgrade_rejected", CanSend);
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> Gold(GameConnectionContext context, Packet packet, bool deposit)
    {
        int amount = CargoRequests.Gold(packet.Body);
        var result = context.BlocksTownActions ? InventoryResult.Reject(10) :
            context.Session.MutateInventory(character => cargo.Gold(character, amount, deposit), 10);
        await context.Reply(deposit ? CmdPacketType.DEPOSIT_MONEY : CmdPacketType.WITHDRAW_MONEY,
            CargoPackets.Ack(result, context.Session.Selected?.Inventory.AccountCargo.Gold), result.Accepted ? "cargo_gold_committed" : "cargo_gold_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> PersonalUpgrade(GameConnectionContext context, Packet packet)
    {
        short slot = CargoRequests.PersonalUpgrade(packet.Body);
        var result = context.BlocksTownActions ? InventoryResult.Reject(17) :
            context.Session.MutateInventory(character => cargo.PersonalUpgrade(character, slot), 17);
        await context.Reply(CmdPacketType.UPGRADE_CARGO, CargoPackets.Ack(result), result.Accepted ? "personal_cargo_upgraded" : "personal_cargo_upgrade_rejected");
        await projection.RefreshAsync(context, result);
        return CommandResult.Handled;
    }
}
