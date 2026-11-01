using DfoServer.Game.Town;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class TeleportHandler
{
    async Task<CommandResult> PrivilegeAsync(GameConnectionContext context, Packet packet)
    {
        PrivilegeTeleportRequest request;
        try { request = PrivilegeTeleportRequests.Parse(packet.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        var result = context.Session.TeleportPrivilege(request, service, context.BlocksTownActions);
        Log(context, packet, result);
        if (result.Receipt is not { } receipt)
        {
            await context.Reply(CmdPacketType.PRIVILEGE_SERVICE_TELEPORT,
                PrivilegeTeleportPackets.Rejected(result.Reason == "privilege-teleport-area-unavailable"), "teleport_privilege_rejected");
            return CommandResult.Handled;
        }
        bool Current() => context.Session.CanPublishTravel(receipt.Travel);
        await context.Reply(CmdPacketType.PRIVILEGE_SERVICE_TELEPORT, PrivilegeTeleportPackets.Committed(), "teleport_privilege_committed", Current);
        await town.TravelAsync(context, receipt.Travel);
        return CommandResult.Handled;
    }
}
