using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Marriage;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class MarriageHandler
{
    MarriageTeleportQuote? PartnerQuote(GameConnectionContext context, MarriageCapture capture,
        MarriageRecord record, uint slot)
    {
        if (slot > short.MaxValue || record.State != 4 || !record.IsMember(capture.Character.Id) ||
            context.Session.ChannelEntry is not { } channel ||
            online.FindConnectionByCharacterId(record.Other(capture.Character.Id)) is not { } connection ||
            connection.Session.ChannelEntry?.Identity != channel.Identity ||
            connection.Session.CaptureMarriage() is not { Phase: SessionPhase.Town } peer ||
            peer.Character.Id != record.Other(capture.Character.Id)) return null;
        var actor = capture.Character;
        var target = peer.Character.Position;
        var completed = QuestEligibility.Completed(actor);
        var peerQuests = QuestEligibility.Completed(peer.Character);
        if (target.Town is 17 or 101 || target is { Town: 39, Area: >= 8 and <= 38 } ||
            target.X > short.MaxValue || target.Y > short.MaxValue ||
            maps.IsRoom(target, peerQuests) || maps.Scene(target, peerQuests).IsEpisode ||
            maps.DestinationAdmission(target, actor.Level, completed) != "ready" || !maps.IsPosition(target, completed) ||
            actor.Inventory.Find(new(InventoryListType.Main, (short)slot)) is not { } source ||
            items.PartnerTeleport(source.Core.ItemId) is not { } definition) return null;
        string place = maps.IsRoom(actor.Position, completed) ? "[seria room]" : "[village]";
        var now = timer.GetUtcNow();
        var consumed = InventoryActionConsumableService.Plan(actor, source.Position, definition.Source, place, timer, now);
        if (!consumed.Accepted) return null;
        var plan = new TeleportPlan(actor, target, maps.ResolveRoomReturn(target, actor.Return, actor.Level, completed),
            consumed.Plan!, actor.TeleportCooldowns, now.ToUnixTimeMilliseconds());
        return new(record.Id, record.Revision, capture, connection.Session, peer, channel.Identity, slot,
            now.ToUnixTimeMilliseconds() + 60_000, plan);
    }

    async Task<CommandResult> PartnerTeleportAsync(GameConnectionContext context, Packet packet,
        MarriageCapture capture, MarriageRecord before)
    {
        if (packet.Opcode == 1045)
        {
            var request = MarriageCoupleRequests.PartnerCheck(packet.Body);
            var quote = request.ContextId == before.ContextId ? PartnerQuote(context, capture, before, request.TicketSlot) : null;
            bool accepted = context.Session.QuoteMarriageTeleport(quote);
            var target = accepted ? quote!.Plan.Position : new Location(0, 0, 0, 0);
            await context.Reply((CmdPacketType)1045, MarriageCouplePackets.PartnerCheckAck(accepted ? 0 : -2,
                accepted ? quote!.Channel.Number : (ushort)0, 0,
                target.Town, target.Area, checked((short)target.X), checked((short)target.Y)), "marriage_partner_precheck");
            return CommandResult.Handled;
        }
        var move = MarriageCoupleRequests.PartnerMove(packet.Body);
        var pending = context.Session.TakeMarriageTeleport(capture, timer.GetUtcNow().ToUnixTimeMilliseconds());
        if (pending is null || pending.Relation != before.Id || pending.Revision != before.Revision ||
            pending.Slot != move.TicketSlot || move.Town != pending.Plan.Position.Town || move.Area != pending.Plan.Position.Area ||
            move.X != pending.Plan.Position.X || move.Y != pending.Plan.Position.Y)
            return await Reject(context, 1046, -2);
        var result = context.Session.CommitMarriageTeleport(pending, () =>
            store.FindMarriage(capture.Character.Id) is { } current && current.Id == before.Id &&
            current.Revision == before.Revision && current.State == 4, timer);
        log.Write("marriage_partner_teleport", new { Connection = context.Id, result.Accepted, result.Reason,
            Character = capture.Character.Id, Partner = pending.Peer.Character.Id, Slot = pending.Slot });
        if (result.Receipt is not { } receipt) return await Reject(context, 1046, -2);
        bool Guard() => context.Session.CanPublishTravel(receipt.Travel);
        await context.Reply((CmdPacketType)1046, MarriageCouplePackets.EmptyAck(), "marriage_partner_teleported", Guard);
        await inventory.RefreshAsync(context, receipt.Inventory, refreshEquipment: false, suppressMainNotice: true);
        await town.TravelAsync(context, receipt.Travel);
        return CommandResult.Handled;
    }

    public async Task<CommandResult> UsePartnerTeleportActionAsync(GameConnectionContext context, StackableActionUseRequest request)
    {
        MarriageTeleportQuote? quote = null;
        if (request.Action == PartnerTeleportDefinition.Action && request.List == (byte)InventoryListType.Main &&
            request.Reserved == 0 && request.FirstArgument == 0 && request.SecondArgument == 0 &&
            !context.BlocksTownActions && context.Session.CaptureMarriage() is { } capture &&
            store.FindMarriage(capture.Character.Id) is { State: 4 } record)
            quote = PartnerQuote(context, capture, record, request.Slot);
        var result = quote is null ? new TeleportResult("partner-unavailable") :
            context.Session.CommitMarriageTeleport(quote, () => store.FindMarriage(quote.Actor.Character.Id) is { } current &&
                current.Id == quote.Relation && current.Revision == quote.Revision && current.State == 4, timer);
        bool Guard() => result.Receipt is not { } receipt || context.Session.CanPublishTravel(receipt.Travel);
        await context.Reply(CmdPacketType.USE_STACKABLE_ACTION,
            result.Accepted ? StackableActionPackets.Used(request) : StackableActionPackets.Rejected(request),
            "marriage_partner_teleport_action", Guard);
        if (result.Receipt is { } committed)
        {
            await inventory.RefreshAsync(context, committed.Inventory,
                new HashSet<InventorySlot> { new(InventoryListType.Main, checked((short)request.Slot)) });
            await town.TravelAsync(context, committed.Travel);
        }
        return CommandResult.Handled;
    }
}
