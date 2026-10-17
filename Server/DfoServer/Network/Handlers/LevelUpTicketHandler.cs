using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class LevelUpTicketHandler(LevelUpTicketService service, QuestCatalog quests, LevelUpTicketProjection projection,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.REQUEST_EVENT_SERVER_LEVEL_UP,
        UseAsync,nameof(LevelUpTicketHandler));

    async Task<CommandResult> UseAsync(GameConnectionContext context, Packet packet)
    {
        if (!LevelUpTicketRequests.TryRead(packet.Body,out var position)) return CommandResult.Unhandled;
        var before = context.Owner.Execute(() => (context.Session.Selected,context.Session.SelectionEpoch,context.Session.Phase));
        var receipt = context.Activity?.BlocksTownActions == true ? null : context.Session.UseLevelUpTicket(position,service,quests);
        bool Guard() => receipt is not null ? context.Session.CanPublishQuestQuickClear(receipt) :
            context.Owner.Execute(() => ReferenceEquals(before.Selected,context.Session.Selected) &&
                before.SelectionEpoch==context.Session.SelectionEpoch && before.Phase==context.Session.Phase);
        log.Write("consumable_use",new { Connection=context.Id,Opcode=packet.Opcode,Slot=position.Index,List=position.List,
            Item=before.Selected?.Inventory.Find(position)?.Core.ItemId,
            Accepted=receipt?.Commit is not null,Reason=receipt?.Reason ?? "invalid-state",Character=before.Selected?.Id,
            Revision=receipt?.Commit?.Character.Revision });
        if (receipt?.Commit is not null) await projection.PublishAsync(context,receipt);
        else if (receipt?.Prepared is { PendingQuestions.Length:>0 } prepared)
            await context.Notify(NotiPacketType.SELECT_BRANCH_QUEST,QuestSkipPackets.Questions(prepared.PendingQuestions),"level_up_ticket_questions",Guard);
        else
            await context.Reply(CmdPacketType.REQUEST_EVENT_SERVER_LEVEL_UP,LevelUpTicketPackets.Rejected(),"level_up_ticket_rejected",Guard);
        return CommandResult.Handled;
    }
}
