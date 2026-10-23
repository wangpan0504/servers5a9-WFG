using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class SoloPartyHandler(ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SET_PARTY_INFO, SetAsync, nameof(SoloPartyHandler));
        commands.Register(CmdPacketType.LEAVE_PARTY, LeaveAsync, nameof(SoloPartyHandler));
    }

    async Task<CommandResult> SetAsync(GameConnectionContext context, Packet packet)
    {
        var request = PartyRequests.Set(packet.Body);
        var receipt = context.Session.SetSoloParty(request, context.BlocksTownActions);
        log.Write("solo_party", new { Connection = context.Id, packet.Opcode, Accepted = receipt is not null,
            PartyId = receipt?.Party.Id, receipt?.Created, request.Mode, request.Settings.TitlePreset });
        if (receipt is null)
            await context.Reply(CmdPacketType.SET_PARTY_INFO, PartyPackets.Rejected(), "solo_party_rejected");
        else
        {
            bool Current() => context.Session.CanPublishSoloParty(receipt);
            await context.Reply(CmdPacketType.SET_PARTY_INFO, [1], "solo_party_settings_accepted", Current);
            await context.Notify(NotiPacketType.PARTY_INFO, PartyPackets.Info(receipt, context.Channel), "solo_party_info", Current);
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> LeaveAsync(GameConnectionContext context, Packet packet)
    {
        PartyRequests.Leave(packet.Body);
        var receipt = context.Session.LeaveSoloParty(context.BlocksTownActions);
        log.Write("solo_party", new { Connection = context.Id, packet.Opcode, Accepted = receipt is not null, PartyId = receipt?.Party.Id });
        if (receipt is null)
            await context.Reply(CmdPacketType.LEAVE_PARTY, PartyPackets.Rejected(), "solo_party_leave_rejected");
        else
            // 旧端成功退队依靠9的kind3清除队伍；不猜造本版13成功分支的附加字段。
            await context.Notify(NotiPacketType.PARTY_INFO, PartyPackets.Info(receipt, context.Channel), "solo_party_left",
                () => context.Session.CanPublishSoloParty(receipt));
        return CommandResult.Handled;
    }
}
