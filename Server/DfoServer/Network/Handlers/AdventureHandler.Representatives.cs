using DfoServer.Game.Adventure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class AdventureHandler
{
    async Task<CommandResult> Candidates(GameConnectionContext context, Packet packet)
    {
        var request = AdventureRequests.Profession(packet.Body);
        var selected = context.Session.Selected;
        long epoch = context.Session.SelectionEpoch;
        byte[] body = AdventurePackets.Ack(3);
        if (context.Session.Ready && selected is not null && representatives.Catalog.Professions.ContainsKey(request))
        {
            var snapshot = context.Session.ReadAdventure(representatives.Catalog, Now);
            body = AdventurePackets.Candidates(snapshot.Members.Where(row => row.Job == request.Job && row.ProfessionGrow == request.Grow &&
                representatives.Catalog.Eligible(row.Job, row.Grow, row.Level)).ToArray(), context.Channel.ServerIndex);
        }
        await context.Reply(CmdPacketType.GET_REPRESENT_CHARAC_JOB, body, "adventure_representative_candidates",
            selected is null ? null : () => context.Session.Ready && context.Session.SelectionEpoch == epoch && ReferenceEquals(context.Session.Selected, selected));
        return CommandResult.Handled;
    }
    async Task<CommandResult> Set(GameConnectionContext context, Packet packet)
    {
        var request = AdventureRequests.Representative(packet.Body);
        var result = request.Region == context.Channel.ServerIndex ?
            context.Session.SetAdventureRepresentative(representatives, request.Job, request.Grow, request.Character, Now) :
            new AdventureRepresentativeResult(false, 3);
        byte[] body = AdventurePackets.Ack(result.Error);
        if (result.Accepted && result.Snapshot is { } snapshot)
        {
            var profession = representatives.Catalog.Professions[(request.Job, request.Grow)];
            var member = snapshot.Representatives(representatives.Catalog).Single(row => row.Job == request.Job && row.ProfessionGrow == request.Grow);
            ushort count = checked((ushort)snapshot.Members.Count(row => representatives.Catalog.Eligible(row.Job, row.Grow, row.Level) &&
                representatives.Catalog.Professions[(row.Job, row.ProfessionGrow)].Group == profession.Group));
            body = AdventurePackets.Representative(profession, count, member);
        }
        await context.Reply(CmdPacketType.SET_REPRESENT_CHARAC_JOB, body, "adventure_representative_set",
            result.Accepted ? () => context.Session.CanPublishAdventureRepresentative(result) : null);
        await Publish(context, result);
        return CommandResult.Handled;
    }
    async Task<CommandResult> Remove(GameConnectionContext context, Packet packet)
    {
        var request = AdventureRequests.Profession(packet.Body);
        var result = context.Session.SetAdventureRepresentative(representatives, request.Job, request.Grow, null, Now);
        await context.Reply(CmdPacketType.REMOVE_REPRESENT_CHARAC_JOB, AdventurePackets.Ack(result.Error), "adventure_representative_remove",
            result.Accepted ? () => context.Session.CanPublishAdventureRepresentative(result) : null);
        await Publish(context, result);
        return CommandResult.Handled;
    }
    async Task Publish(GameConnectionContext context, AdventureRepresentativeResult result)
    {
        if (!result.Accepted || result.Snapshot is not { } snapshot || result.Selected is not { } selected) return;
        await expeditions.RepresentativesAsync(context, result);
        bool Guard() => context.Session.CanPublishAdventureRepresentative(result);
        await context.Notify(NotiPacketType.ADVENTURE_INFO, AdventurePackets.Information(selected.Id, snapshot, representatives.Catalog,
            snapshot.Representatives(representatives.Catalog)), "adventure_representative_information", Guard);
        await context.Notify(NotiPacketType.ADVENTURE_LV_EXP, AdventurePackets.Refresh(selected.Id, snapshot.State, representatives.Catalog),
            "adventure_representative_actor", Guard);
    }
}
