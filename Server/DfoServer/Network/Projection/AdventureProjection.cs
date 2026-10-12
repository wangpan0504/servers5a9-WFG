using DfoServer.Game.Session;
using DfoServer.GameWorld.Adventure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class AdventureProjection(AdventureCatalog catalog)
{
    public void Prepare(GameConnectionContext context) => context.Session.InitializeAdventure(catalog, DateTimeOffset.UtcNow);
    public async Task InformationAsync(GameConnectionContext context, bool ack = false, bool initialization = false)
    {
        var selected = context.Session.Selected;
        if (selected is null && !context.Session.CheckState(UserState.CharacterSelect)) return;
        long epoch = context.Session.SelectionEpoch;
        var snapshot = context.Session.ReadAdventure(catalog, DateTimeOffset.UtcNow);
        bool Guard() => context.Session.SelectionEpoch == epoch && ReferenceEquals(context.Session.Selected, selected) &&
            (selected is null ? context.Session.CheckState(UserState.CharacterSelect) : initialization || context.Session.Ready);
        var body = AdventurePackets.Information(selected?.Id ?? 0, snapshot, catalog, snapshot.Representatives(catalog), context.Channel.ServerIndex, ack);
        if (ack) await context.Reply(CmdPacketType.REQUEST_ADVENTURE_INFO, body, "adventure_information", Guard);
        else
        {
            await context.Notify(NotiPacketType.ADVENTURE_INFO, body, selected is null ? "adventure_selector_information" : "adventure_information", Guard);
            if (selected is not null)
                await context.Notify(NotiPacketType.ADVENTURE_LV_EXP, AdventurePackets.Refresh(selected.Id, snapshot.State, catalog), "adventure_actor_initialize", Guard);
        }
    }
}
