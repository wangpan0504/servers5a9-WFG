using DfoServer.Game.Adventure;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class AdventureNameHandler(AdventureNameService service, AdventureProjection adventure,
    InventoryProjection inventory, TownProjection town, TimeProvider? clock = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.REPRESENT_ACCOUNT_NAME_DUPLICATE_CHECK, Check, nameof(AdventureNameHandler));
        commands.Register(CmdPacketType.UPDATE_REPRESENT_ACCOUNT_NAME, (context, packet) => Change(context, packet, true), nameof(AdventureNameHandler));
        commands.Register(CmdPacketType.CHANGE_REPRESENT_ACCOUNT_NAME, (context, packet) => Change(context, packet, false), nameof(AdventureNameHandler));
    }

    async Task<CommandResult> Check(GameConnectionContext context, Packet packet)
    {
        string name = AdventureRequests.Name(packet.Body);
        ushort error = !AdventureName.IsValid(name) ? (ushort)3 : context.Session.AdventureNameExists(name) ? (ushort)20 : (ushort)0;
        await context.Reply(CmdPacketType.REPRESENT_ACCOUNT_NAME_DUPLICATE_CHECK,
            error == 0 ? AdventurePackets.NameAccepted(name) : AdventurePackets.Ack(error), "adventure_name_check");
        return CommandResult.Handled;
    }

    async Task<CommandResult> Change(GameConnectionContext context, Packet packet, bool initial)
    {
        var request = initial ? (Name: AdventureRequests.Name(packet.Body), Slot: (ushort)0, Mode: (ushort)0) : AdventureRequests.Rename(packet.Body);
        var opcode = initial ? CmdPacketType.UPDATE_REPRESENT_ACCOUNT_NAME : CmdPacketType.CHANGE_REPRESENT_ACCOUNT_NAME;
        if (context.BlocksTownActions && !context.Session.CheckState(UserState.CharacterSelect))
        {
            await context.Reply(opcode, AdventurePackets.Ack(19), "adventure_name_rejected");
            return CommandResult.Handled;
        }
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var result = context.Session.ChangeAdventureName(service, request.Name, initial, request.Slot, request.Mode, now);
        bool Guard() => context.Session.CanPublishAdventureName(result);
        await context.Reply(opcode, result.Accepted && initial ? AdventurePackets.NameAccepted(result.Snapshot!.State.Name) :
            AdventurePackets.Ack(result.Error), "adventure_name_committed", result.Accepted ? Guard : null);
        if (!result.Accepted) return CommandResult.Handled;
        if (result.Inventory is { } changed) await inventory.RefreshAsync(context, changed, suppressMainNotice: true);
        if (result.Selected is not null)
            await context.Notify(NotiPacketType.REPRESENT_ACCOUNT_NAME, AdventurePackets.AccountName(result.Snapshot!.State.Name), "adventure_name_refresh", Guard);
        if (!initial && result.Changed && Guard())
            await town.ReturnToSelectionAsync(context, Guard);
        else if (Guard())
            await adventure.InformationAsync(context);
        if (initial && result.Changed && result.Selected is { } selected && Guard())
        {
            await context.Notify(NotiPacketType.USERINFO,
                TownPackets.Actor(selected, context.Channel, adventureName: result.Snapshot!.State.Name,
                    premiumPcRoom: context.Session.PremiumPcRoom), "adventure_name_actor", Guard);
            context.Owner.Execute(() =>
            {
                if (Guard()) town.Refresh(context);
                return true;
            });
        }
        return CommandResult.Handled;
    }
}
