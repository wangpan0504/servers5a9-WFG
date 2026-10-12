using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using DfoServer.Game.Session;

namespace DfoServer.Network.Projection;

internal sealed class AdventureExpeditionProjection(AdventureExpeditionService service, TimeProvider? clock = null)
{
    public Task InformationAsync(GameConnectionContext context, bool ack = false, AdventureExpeditionResult? result = null)
    {
        var selected = result?.Selected ?? context.Session.Selected;
        if (selected is null && !context.Session.CheckState(UserState.CharacterSelect)) return Task.CompletedTask;
        long epoch = context.Session.SelectionEpoch;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var snapshot = result?.Snapshot ?? context.Session.ReadAdventure(service.Catalog, now);
        bool Guard() => result is null ? context.Session.SelectionEpoch == epoch && ReferenceEquals(context.Session.Selected, selected) &&
            (selected is null ? context.Session.CheckState(UserState.CharacterSelect) : context.Session.Ready) :
            context.Session.CanPublishAdventureExpedition(result);
        return SendAsync(context, snapshot, now, ack, Guard);
    }
    public Task RepresentativesAsync(GameConnectionContext context, AdventureRepresentativeResult result) =>
        result.Accepted && result.Snapshot is { } snapshot ? SendAsync(context, snapshot,
            (clock ?? TimeProvider.System).GetUtcNow(), false, () => context.Session.CanPublishAdventureRepresentative(result)) : Task.CompletedTask;

    Task SendAsync(GameConnectionContext context, AdventureSnapshot snapshot, DateTimeOffset now, bool ack, Func<bool> guard)
    {
        var attributes = service.Expeditions.Areas.Keys.ToDictionary(area => area, area => service.AreaAttributes(area, now).ToArray());
        var body = AdventurePackets.Expedition(snapshot, service.Expeditions, snapshot.Representatives(service.Catalog), attributes, now);
        return ack ? context.Reply(CmdPacketType.MERCENARY_INFO, body, "expedition_information", guard) :
            context.Notify(NotiPacketType.MERCEMARY_COMPETITION_INFO, body, "expedition_state", guard);
    }
}
