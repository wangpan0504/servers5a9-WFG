using System.Collections.Immutable;
using DfoServer.Game.Pvp;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class PvpMatchingHandler(PvpArenaService arena, ServerLog log, TimeProvider? clock = null) : IGameCommandModule
{
    DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.REQUEST_INTEGRATED_MATCHING, QueueAsync, nameof(PvpMatchingHandler));
        commands.Register(CmdPacketType.CANCEL_INTEGRATED_MATCHING, CancelAsync, nameof(PvpMatchingHandler));
        commands.Register(CmdPacketType.INTEGRATE_MATCHING_DIRECT_COMPLETE, LoadedAsync, nameof(PvpMatchingHandler));
    }

    async Task<CommandResult> QueueAsync(GameConnectionContext context, Packet packet)
    {
        var request = PvpMatchingRequests.Match(packet.Body);
        ushort error = request.Kind != 1 || request.GameType is not (0 or 7) ? (ushort)2 :
            context.Session.ChannelEntry?.Type is not (8 or 24) ? (ushort)0x13 : (ushort)0;
        PvpMatchingPreferences? preferences = null;
        // 18B通用入口与7B公平场入口分开；不能把后者的四个原值当成排除列表。
        if (error == 0 && request.TrailingValue == 0 && request.Parameters is [var option, <= 1 and var excluded, var final] &&
            request.Pairs.Length == 4)
        {
            var choice = new PvpMatchingPreferences(option, final, excluded != 0,
                request.Pairs.Select(row => new PvpMatchingExclusion(row.First, row.Second)).ToImmutableArray());
            if (choice.IsValid) preferences = choice;
        }
        var ticket = error == 0 && preferences is not null
            ? request.GameType == 7 ? arena.QueueTotal(context.Session, preferences, context.BlocksTownActions)
                : arena.QueueOrdinary(context.Session, preferences, context.BlocksTownActions) : null;
        if (ticket is null)
            await context.Reply(CmdPacketType.REQUEST_INTEGRATED_MATCHING,
                PvpMatchingPackets.Rejected(error == 0 ? (ushort)0x17 : error), "pvp_matching_rejected");
        else
        {
            // 回执已被接受；后续配对/取消不应吞掉本次回执，换角和接管仍使它失效。
            bool Current() => ticket.Actor.IsCurrent();
            using var publication = context.Sender.QueuePublication(Current);
            await context.Reply(CmdPacketType.REQUEST_INTEGRATED_MATCHING,
                PvpMatchingPackets.Accepted(1, request.GameType, ticket.Preferences.ExclusionsEnabled ? (byte)1 : (byte)0),
                "pvp_matching_queued", Current);
        }
        log.Write("pvp_matching_queue", new { Connection = context.Id, request.Kind, request.GameType,
            Accepted = ticket is not null, Order = ticket?.Order });
        return CommandResult.Handled;
    }

    async Task<CommandResult> CancelAsync(GameConnectionContext context, Packet packet)
    {
        var request = PvpMatchingRequests.Cancel(packet.Body);
        bool total = context.Session.CurrentPvpArena?.Room.IsTotal ?? context.Session.CurrentPvpMatching?.IsTotal ?? false;
        if (request.Kind != 1 || request.GameType is not (0 or 7) ||
            (context.Session.CurrentPvpArena is not null || context.Session.CurrentPvpMatching is not null) && total != (request.GameType == 7))
        {
            await context.Reply(CmdPacketType.CANCEL_INTEGRATED_MATCHING,
                PvpMatchingPackets.Rejected(2), "pvp_matching_cancel_mode_rejected");
            return CommandResult.Handled;
        }
        var member = context.Session.CurrentPvpArena;
        var actor = member?.Actor ?? context.Session.CaptureSocialActor();
        if (actor is null)
        {
            await context.Reply(CmdPacketType.CANCEL_INTEGRATED_MATCHING,
                PvpMatchingPackets.Rejected(0x17), "pvp_matching_cancel_rejected");
            return CommandResult.Handled;
        }
        PvpArenaNotice[] notices = [];
        bool accepted;
        if (member is not null)
        {
            notices = member.Room.IsMatching ? arena.CancelMatchingRoom(member.Room, Now) : [];
            accepted = notices.Length != 0;
        }
        else accepted = context.Session.CurrentPvpMatching is not { } ticket || arena.CancelMatching(context.Session, ticket);
        using var publication = context.Sender.QueuePublication(actor.IsCurrent);
        await context.Reply(CmdPacketType.CANCEL_INTEGRATED_MATCHING, accepted ? PvpMatchingPackets.Cancelled() :
            PvpMatchingPackets.Rejected(0x17), accepted ? "pvp_matching_cancelled" : "pvp_matching_cancel_rejected");
        if (notices.Length != 0) arena.Publish(notices);
        log.Write("pvp_matching_cancel", new { Connection = context.Id, Accepted = accepted });
        return CommandResult.Handled;
    }

    Task<CommandResult> LoadedAsync(GameConnectionContext context, Packet packet)
    {
        uint id = PvpMatchingRequests.DirectComplete(packet.Body);
        var load = arena.CompleteMatchingLoad(context.Session, id, Now);
        var notices = load?.Phase == PvpMatchingLoadPhase.Ready && context.Session.CurrentPvpArena is { } member
            ? arena.StartOrdinaryMatching(member.Room, Now) : [];
        if (notices.Length != 0) arena.Publish(notices);
        log.Write("pvp_matching_loaded", new { Connection = context.Id, Matching = id,
            Accepted = load is not null, Started = notices.Length != 0 });
        return Task.FromResult(CommandResult.Handled);
    }
}
