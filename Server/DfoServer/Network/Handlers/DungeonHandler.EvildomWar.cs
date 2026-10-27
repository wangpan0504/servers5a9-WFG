using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    async Task<CommandResult> StartEvildomWarAsync(GameConnectionContext context, Packet request)
    {
        if (!EvildomWarRequests.IsStart(request.Body)) return CommandResult.Unhandled;
        var party = context.Session.CurrentParty?.Invoke();
        string? rejection = context.BlocksTownActions ? "town-action-blocked" :
            evildomWar is null || partyDungeons is null ? "resources-unavailable" :
            party is null || !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, context.Session))
                ? "party-leader-required" : null;
        using var peers = rejection is null ? await PartyDungeonProjection.AcquirePeersAsync(context, party!.Members) : null;
        if (rejection is null) rejection = partyDungeons!.StartEvildomWar(context.Session, evildomWar!);
        log.Write("evildom_war_start", new { Connection = context.Id, Accepted = rejection is null, Reason = rejection,
            Party = party?.Id, Members = party?.Members.Length, Mode = party?.Settings.KindArgument });
        long epoch = context.Session.SelectionEpoch;
        await context.Reply(CmdPacketType.START_EVILDOM_WAR, EvildomWarPackets.Start(rejection is null), "evildom_war_start_ack",
            () => context.Session.SelectionEpoch == epoch && (rejection is not null || context.Session.ReadEvildomWar() is not null));
        if (rejection is null)
            await PartyDungeonProjection.PublishAsync(party!.Members.Select(member => new DfoServer.Game.Dungeons.PartyDungeonNotice(
                DfoServer.Game.Dungeons.PartyDungeonNoticeKind.EvildomWarStarted, member, party.Members)));
        return CommandResult.Handled;
    }
}
