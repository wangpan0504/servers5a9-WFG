using DfoServer.Game.Adventure;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class AdventureExpeditionHandler(AdventureExpeditionService service, AdventureExpeditionProjection projection,
    TimeProvider? clock = null, Func<GameConnectionContext, Task>? battlePassChanged = null) : IGameCommandModule
{
    DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.MERCENARY_INFO, Information, nameof(AdventureExpeditionHandler));
        commands.Register(CmdPacketType.MERCENARY_COMPETITION, Start, nameof(AdventureExpeditionHandler));
        commands.Register(CmdPacketType.MERCENARY_COMPETITION_CANCLE, (context, packet) => Finish(context, packet, false), nameof(AdventureExpeditionHandler));
        commands.Register(CmdPacketType.MERCENARY_COMPETITION_REWARD_REQUEST, (context, packet) => Finish(context, packet, true), nameof(AdventureExpeditionHandler));
    }
    async Task<CommandResult> Information(GameConnectionContext context, Packet packet)
    {
        AdventureRequests.Information(packet.Body);
        if (!context.Session.CheckState(UserState.CharacterSelect) && (!context.Session.Ready || context.Session.Selected is null))
            await context.Reply(CmdPacketType.MERCENARY_INFO, AdventurePackets.Ack(3), "expedition_information_rejected");
        else await projection.InformationAsync(context, ack: true);
        return CommandResult.Handled;
    }
    async Task<CommandResult> Start(GameConnectionContext context, Packet packet)
    {
        var request = AdventureRequests.Start(packet.Body);
        var now = Now;
        var result = context.Session.PlanAdventureExpedition(service, snapshot =>
        {
            var started = service.Start(snapshot, request.Area, request.Duration, request.Members, now);
            return (started.Plan, started.Error, 0u);
        }, now);
        await context.Reply(CmdPacketType.MERCENARY_COMPETITION, AdventurePackets.Ack(result.Error), "expedition_start",
            result.Accepted ? () => context.Session.CanPublishAdventureExpedition(result) : null);
        if (result.Accepted) await projection.InformationAsync(context, result: result);
        if (result.Accepted && battlePassChanged is not null && context.Session.CanPublishAdventureExpedition(result))
            await battlePassChanged(context);
        return CommandResult.Handled;
    }
    async Task<CommandResult> Finish(GameConnectionContext context, Packet packet, bool claim)
    {
        byte area = AdventureRequests.Area(packet.Body);
        var now = Now;
        var result = context.Session.PlanAdventureExpedition(service, snapshot => service.Finish(snapshot, area, claim, now), now);
        await context.Reply(claim ? CmdPacketType.MERCENARY_COMPETITION_REWARD_REQUEST : CmdPacketType.MERCENARY_COMPETITION_CANCLE,
            result.Accepted && claim ? AdventurePackets.ExpeditionReward(result.Reward) : AdventurePackets.Ack(result.Error), "expedition_finish",
            result.Accepted ? () => context.Session.CanPublishAdventureExpedition(result) : null);
        if (result.Accepted)
        {
            await projection.InformationAsync(context, result: result);
            var snapshot = result.Snapshot!; var selected = result.Selected!;
            bool Guard() => context.Session.CanPublishAdventureExpedition(result);
            await context.Notify(NotiPacketType.ADVENTURE_INFO, AdventurePackets.Information(selected.Id, snapshot, service.Catalog,
                snapshot.Representatives(service.Catalog)), "expedition_account_information", Guard);
            await context.Notify(NotiPacketType.ADVENTURE_LV_EXP, AdventurePackets.Refresh(selected.Id, snapshot.State, service.Catalog),
                "expedition_account_actor", Guard);
            if (claim && battlePassChanged is not null && Guard())
                await battlePassChanged(context);
        }
        return CommandResult.Handled;
    }
}
