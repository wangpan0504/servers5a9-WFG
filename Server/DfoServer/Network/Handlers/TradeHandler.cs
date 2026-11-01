using System.Buffers.Binary;
using DfoServer.Game.Session;
using DfoServer.Game.Trade;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Currency;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class TradeHandler(TradeRegistry trades, OnlineSessionDirectory online, ItemCatalog items, GoldLimitCatalog goldLimits, ServerLog log)
    : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.SET_ITEMTRADE_STATE, StateAsync, nameof(TradeHandler), UserStateRequirement.WorldReady);

    async Task<CommandResult> StateAsync(GameConnectionContext context, Packet request)
    {
        var captured = context.Session.CapturePlayerTrade();
        if (request.Body.Length != 1 || captured is null || trades.Find(captured.Value.Participant) is not { Accepted: true } view)
        {
            await context.Reply(CmdPacketType.SET_ITEMTRADE_STATE, [0, 1, 0], "trade_state_rejected");
            return CommandResult.Handled;
        }
        var participant = captured.Value.Participant;
        byte state = request.Body[0];
        TradeTicket? ticket = null;
        bool both = false;
        bool changed = state switch
        {
            5 => trades.Lock(participant, out both, view.Id),
            1 => trades.Confirm(participant, out ticket, view.Id),
            _ => (ticket = trades.Cancel(participant, view.Id)) is not null
        };
        var response = context.Reply(CmdPacketType.SET_ITEMTRADE_STATE, changed ? TradePackets.StateAck() : [0, 1, 0], "trade_state_ack");
        if (!changed)
        {
            await response;
            return CommandResult.Handled;
        }
        if (response.IsFaulted || response.IsCanceled)
        {
            if (state is 1 or 5) CancelView(view);
            else CancelBoth(ticket!);
            await response;
            return CommandResult.Handled;
        }
        if (state is not (1 or 5))
        {
            CancelBoth(ticket!);
            await response;
            return CommandResult.Handled;
        }
        Deliver(view.Other(participant), NotiPacketType.STATE_ITEMTRADE, TradePackets.State(participant.CharacterId, state));
        if (both)
        {
            Deliver(view.First, NotiPacketType.ITEMTRADE_REG_ITEM_FINISH, TradePackets.RegisterFinished());
            Deliver(view.Second, NotiPacketType.ITEMTRADE_REG_ITEM_FINISH, TradePackets.RegisterFinished());
        }
        if (ticket is not null) Settle(ticket);
        await response;
        return CommandResult.Handled;
    }

    void Settle(TradeTicket ticket)
    {
        var first = online.FindConnectionByCharacterId(ticket.First.CharacterId);
        var second = online.FindConnectionByCharacterId(ticket.Second.CharacterId);
        var firstCapture = first?.Session.CapturePlayerTrade();
        var secondCapture = second?.Session.CapturePlayerTrade();
        if (first is null || second is null || first.ExpertJob?.BlocksActions() == true || second.ExpertJob?.BlocksActions() == true ||
            firstCapture is null || secondCapture is null ||
            firstCapture.Value.Participant != ticket.First || secondCapture.Value.Participant != ticket.Second)
        {
            CancelFrozen(ticket);
            return;
        }
        var plan = TradeSettlement.Plan(firstCapture.Value.Character, secondCapture.Value.Character, ticket, items);
        if (plan is null || plan.First.After.Gold > Math.Max(firstCapture.Value.Character.Inventory.Gold, new CurrencyService(goldLimits).CarryLimit(firstCapture.Value.Character)) ||
            plan.Second.After.Gold > Math.Max(secondCapture.Value.Character.Inventory.Gold, new CurrencyService(goldLimits).CarryLimit(secondCapture.Value.Character)))
        {
            CancelFrozen(ticket);
            return;
        }
        (InventoryResult First, InventoryResult Second)? result;
        try
        {
            result = GameSession.CommitPlayerTrade(first.Session, second.Session, firstCapture.Value.Character, secondCapture.Value.Character, ticket, plan, items);
        }
        catch (PlayerTradeCommitUnknownException)
        {
            trades.Complete(ticket);
            log.Write("trade_commit_unknown", new { First = ticket.First.CharacterId, Second = ticket.Second.CharacterId });
            first.ExpertJob?.Disconnect();
            second.ExpertJob?.Disconnect();
            return;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException)
        {
            log.Write("trade_rejected", new { First = ticket.First.CharacterId, Second = ticket.Second.CharacterId,
                Failure = error.GetType().Name });
            if (trades.Cancel(ticket)) CancelBoth(ticket);
            else if (!ticket.IsCancelled)
            {
                trades.Complete(ticket);
                CancelBoth(ticket);
            }
            return;
        }
        if (result is null) { CancelFrozen(ticket); return; }
        trades.Complete(ticket);
        log.Write("trade_committed", new { First = ticket.First.CharacterId, Second = ticket.Second.CharacterId,
            FirstItems = ticket.FirstOffer.Items.Length, SecondItems = ticket.SecondOffer.Items.Length });
        Publish(ticket.First, result.Value.First, plan.FirstReceived);
        Publish(ticket.Second, result.Value.Second, plan.SecondReceived);
    }

    void Publish(TradeParticipant participant, InventoryResult result,
        System.Collections.Immutable.ImmutableArray<(ushort TradeSlot, InventorySlot Destination)> received)
    {
        if (result.Receipt is not { } receipt || result.Plan is not { } plan) return;
        var target = online.FindConnectionByCharacterId(participant.CharacterId);
        if (target?.GuardedProbe is not { } post) return;
        var guard = target.Session.GuardPlayerTradeReceipt(receipt);
        post((ushort)NotiPacketType.FINISH_ITEMTRADE,
            TradePackets.Finish(received.Select(row => (row.TradeSlot, checked((ushort)row.Destination.Index))).ToArray()), "trade_finish", guard);
        var slots = plan.ChangedSlots.ToList();
        if (plan.Before.Gold != plan.After.Gold) slots.Add(new(InventoryListType.Main, 0));
        if (slots.Count == 0) slots.Add(new(InventoryListType.Main, 0));
        foreach (var group in slots.Distinct().GroupBy(slot => slot.List))
            post((ushort)NotiPacketType.UPDATE_ITEM_LIST_NOT_NOTICE,
                InventoryPackets.Delta(receipt.Character.Inventory, group.Key, group.ToArray()), "trade_inventory", guard);
    }

    void CancelBoth(TradeTicket ticket)
    {
        CancelOne(ticket.First, ticket.FirstOffer);
        CancelOne(ticket.Second, ticket.SecondOffer);
    }

    void CancelFrozen(TradeTicket ticket)
    {
        if (trades.Cancel(ticket)) CancelBoth(ticket);
    }

    void CancelView(TradeView view)
    {
        if (trades.Cancel(view.First, view.Id) is { } ticket) CancelBoth(ticket);
    }

    void CancelOne(TradeParticipant participant, TradeOffer offer)
    {
        var target = online.FindConnectionByCharacterId(participant.CharacterId);
        var capture = target?.Session.CapturePlayerTrade(requireEligible: false);
        if (target?.GuardedProbe is not { } post || capture is null || capture.Value.Participant != participant) return;
        var guard = target.Session.GuardPlayerTrade(participant);
        post((ushort)NotiPacketType.CANCEL_ITEMTRADE, TradePackets.Close(), "trade_cancel", guard);
        bool SnapshotCurrent() => guard() && ReferenceEquals(target.Session.Selected, capture.Value.Character);
        var slots = offer.Items.Select(line => line.Item.Position).ToList();
        if (offer.Gold > 0) slots.Add(new(InventoryListType.Main, 0));
        foreach (var group in slots.Distinct().GroupBy(slot => slot.List))
            post((ushort)NotiPacketType.UPDATE_ITEM_LIST_NOT_NOTICE,
                InventoryPackets.Delta(capture.Value.Character.Inventory, group.Key, group.ToArray()), "trade_restore", SnapshotCurrent);
    }

    public async Task<bool> TryMoveAsync(GameConnectionContext context, Packet request)
    {
        var body = request.Body;
        if (body.Length != 28 || body[0] != TradePackets.TradeSpace && body[11] != TradePackets.TradeSpace) return false;
        var capture = context.Session.CapturePlayerTrade();
        if (capture is null || trades.Find(capture.Value.Participant) is not { Accepted: true } view)
        {
            await context.Reply(CmdPacketType.MOVE_ITEMSPACE, [0, 1, 0], "trade_move_rejected");
            return true;
        }
        byte from = body[0], to = body[11];
        ushort slot = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1));
        uint itemId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(3));
        uint amount = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(7));
        ushort destination = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(12));
        var other = view.Other(capture.Value.Participant);
        byte[]? notice = null;
        if (slot == 0 && itemId == 0 && (from == 0 && to == 4 || from == 4 && to == 0))
        {
            if (trades.ChangeGold(capture.Value.Participant, amount, from == 4, out uint gold, view.Id))
                notice = TradePackets.ChangedGold(gold);
        }
        else if (from == 4 && to != 4)
        {
            if (destination <= short.MaxValue)
            {
                var position = new InventorySlot((InventoryListType)to, (short)destination);
                var currentItem = capture.Value.Character.Inventory.Find(position);
                if (currentItem is not null && trades.Take(capture.Value.Participant, slot, out _, view.Id,
                    position, itemId, amount, currentItem))
                    notice = TradePackets.ClearedRow(slot);
            }
        }
        else if (to == 4 && from != 4 && slot <= short.MaxValue && amount <= int.MaxValue)
        {
            var item = capture.Value.Character.Inventory.Find(new((InventoryListType)from, (short)slot));
            if (item is not null && item.Core.ItemId == itemId && item.Position.List == ItemSlotBoundService.List(item.Core.Kind) &&
                items.TryGet(item.Core.ItemId, out var template) &&
                !template.AttachType.Replace("`", "").Trim().Trim('[', ']').Equals("trade limit", StringComparison.OrdinalIgnoreCase) &&
                MailboxService.TransferError(item, template, context.Owner.Account, "", DateTimeOffset.UtcNow.ToUnixTimeSeconds()) == MailboxError.None &&
                trades.Put(capture.Value.Participant, item, (int)amount, out var tradeSlot, view.Id))
            {
                destination = tradeSlot;
                notice = TradePackets.ChangedItem(tradeSlot, itemId, TradePackets.TradeOfferMarker, item.Core.Durability);
            }
        }
        if (notice is null)
            await context.Reply(CmdPacketType.MOVE_ITEMSPACE, [0, 1, 0], "trade_move_rejected");
        else
        {
            var response = context.Reply(CmdPacketType.MOVE_ITEMSPACE, TradePackets.MoveAck(from, slot, amount, to, destination), "trade_move_ack");
            if (response.IsFaulted || response.IsCanceled)
            {
                CancelView(view);
                await response;
                return true;
            }
            Deliver(other, NotiPacketType.CHANGE_ITEMTRADE_ITEM, notice);
            await response;
        }
        return true;
    }

    void Deliver(TradeParticipant participant, NotiPacketType opcode, byte[] body)
    {
        var target = online.FindConnectionByCharacterId(participant.CharacterId);
        target?.GuardedProbe?.Invoke((ushort)opcode, body, "trade_notice", target.Session.GuardPlayerTrade(participant));
    }

    public TradeView? Find(TradeParticipant participant) => trades.Find(participant);

    public void Release(TradeParticipant participant, TradeView? expected = null)
    {
        if ((expected ?? trades.Find(participant)) is not { } view || trades.Cancel(participant, view.Id) is not { } ticket) return;
        var peer = view.Other(participant);
        CancelOne(peer, peer == ticket.First ? ticket.FirstOffer : ticket.SecondOffer);
    }

    public async Task<bool> TryInviteAsync(GameConnectionContext context, Packet request)
    {
        var body = request.Body;
        if (body.Length != 11 || body[2] != 1) return false;
        var source = context.Session.CapturePlayerTrade();
        ushort targetId = BinaryPrimitives.ReadUInt16LittleEndian(body);
        uint token = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(3));
        var target = online.FindConnectionByCharacterId(targetId);
        var identity = online.PublicCharacter(targetId);
        var recipient = target?.Session.CapturePlayerTrade();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (context.BlocksTownActions || target?.ExpertJob?.BlocksActions() == true || source is null || recipient is null || identity is null ||
            identity.Channel != context.Channel || target?.GuardedProbe is null ||
            !trades.Invite(source.Value.Participant, recipient.Value.Participant, token, now, out var tradeId))
        {
            await context.Reply(CmdPacketType.REQUEST_PEER, PartyPackets.Failure(3), "trade_invite_rejected");
            return true;
        }
        var active = trades.Find(recipient.Value.Participant);
        bool Guard() => active is { } current && current.Id == tradeId && current.IsCurrent() &&
            identity.IsCurrent() && target.Session.GuardPlayerTrade(recipient.Value.Participant)();
        if (!target.GuardedProbe((ushort)NotiPacketType.REQUEST_PEER,
            TradePackets.Invite(source.Value.Character.Id, token, checked((uint)now)), "trade_invite", Guard))
        {
            trades.Cancel(source.Value.Participant, tradeId);
            await context.Reply(CmdPacketType.REQUEST_PEER, PartyPackets.Failure(3), "trade_invite_unreachable");
            return true;
        }
        await context.Reply(CmdPacketType.REQUEST_PEER, [1], "trade_invite_ack");
        return true;
    }

    public async Task<bool> TryRespondAsync(GameConnectionContext context, Packet request)
    {
        var body = request.Body;
        if ((body.Length != 7 && (body.Length != 9 || BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(7)) != 0x85)) ||
            body[2] != 1) return false;
        var source = context.Session.CapturePlayerTrade();
        if (source is null || trades.Find(source.Value.Participant) is not { Accepted: false } view ||
            view.Second != source.Value.Participant || BinaryPrimitives.ReadUInt16LittleEndian(body) != view.First.CharacterId)
            return false;
        if (body.Length == 9)
        {
            Release(source.Value.Participant, view);
            return true;
        }
        var target = online.FindConnectionByCharacterId(view.First.CharacterId);
        var identity = online.PublicCharacter(view.First.CharacterId);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (context.BlocksTownActions || target?.ExpertJob?.BlocksActions() == true || target?.GuardedProbe is null || identity is null || identity.Channel != context.Channel ||
            !trades.Accept(view.Second, view.First, now, out var token, out var tradeId))
        {
            CancelView(view);
            await context.Reply(CmdPacketType.RESPONSE_PEER, PartyPackets.Failure(3, 1), "trade_response_rejected");
            return true;
        }
        bool Guard() => view.IsCurrent() && identity.IsCurrent() && target.Session.GuardPlayerTrade(view.First)();
        if (!target.GuardedProbe((ushort)NotiPacketType.RESPONSE_PEER,
            TradePackets.Accepted(source.Value.Character.Id, token, checked((uint)now)), "trade_accepted", Guard))
        {
            CancelView(view);
            await context.Reply(CmdPacketType.RESPONSE_PEER, PartyPackets.Failure(3, 1), "trade_response_unreachable");
            return true;
        }
        await context.Reply(CmdPacketType.RESPONSE_PEER, TradePackets.ResponseAck(view.First.CharacterId), "trade_response_ack");
        return true;
    }
}
