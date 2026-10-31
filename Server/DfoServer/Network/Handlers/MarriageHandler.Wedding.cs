using System.Collections.Immutable;
using System.Data.Common;
using System.Security.Cryptography;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Marriage;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class MarriageHandler
{
    sealed record Offer(GameSession Source, MarriageCapture First, GameSession Target, MarriageCapture Second,
        byte Operation, byte ExtraA, byte ExtraB, long Until);
    readonly object offerGate = new();
    readonly Dictionary<ushort, Offer> offers = [];

    async Task<CommandResult> WeddingAsync(GameConnectionContext context, Packet packet, MarriageCapture capture)
    {
        if (packet.Opcode == 1034) return await RequestWedding(context, capture, MarriageWeddingRequests.Request(packet.Body));
        if (packet.Opcode == 1035) return await RespondWedding(context, capture, MarriageWeddingRequests.Response(packet.Body));
        if (packet.Opcode == 1037)
            return await PartnerCardAsync(context, capture, MarriageWeddingRequests.Character(packet.Body));
        var actor = capture.Character;
        var before = store.FindMarriage(actor.Id);
        if (packet.Opcode == 1036)
        {
            var gift = MarriageWeddingRequests.MoneyGift(packet.Body);
            before = store.FindMarriageByContext(gift.Context);
            if (before is null || before.Wedding is not { } wedding) return await Reject(context, 1036, -1);
            if (timer.GetUtcNow().ToUnixTimeSeconds() >= wedding.StartsAt) return await Reject(context, 1036, -4);
            if (before.IsMember(actor.Id) || !wedding.Invitations.Any(row => row.CharacterId == actor.Id) ||
                CharacterName.Encoding.GetByteCount(gift.Text) > 255 || gift.Text.Any(char.IsControl))
                return await Reject(context, 1036, -2);
            var after = MarriageWeddingRules.AddGift(before, catalog,
                new() { Id = Guid.NewGuid(), CharacterId = actor.Id, Name = actor.Name, Gold = gift.Gold, Text = gift.Text }, timer.GetUtcNow().ToUnixTimeSeconds());
            if (actor.Inventory.Gold < gift.Gold) return await Reject(context, 1036, -8);
            var plan = planner.Plan(actor, [], timer.GetUtcNow(), gift.Gold);
            if (plan is null || context.Session.CommitMarriage(capture, plan, before, after) is not { } saved)
                return await Reject(context, 1036, -2);
            await inventory.RefreshAsync(context, saved.Actor);
            await context.Reply((CmdPacketType)1036, MarriageWeddingPackets.GiftAck(0, saved.Actor.Receipt!.Character.Inventory.Gold), "marriage_gift_escrowed");
            return CommandResult.Handled;
        }
        if (packet.Opcode == 1041)
        {
            var entry = MarriageWeddingRequests.EnterCeremony(packet.Body);
            var wedding = store.FindMarriageByContext(entry.Context);
            return wedding is null ? await Reject(context, 1041, -2) : await EnterWeddingAsync(context, capture, wedding, entry);
        }
        if (before is null) return await Reject(context, packet.Opcode, MarriageFailurePackets.MissingRelation(packet.Opcode));
        if (packet.Opcode is 1038 or 1039)
        {
            var ring = packet.Opcode == 1038 ? MarriageWeddingRequests.UpgradeRing(packet.Body) : default;
            uint slot = packet.Opcode == 1038 ? ring.ItemSlot : MarriageWeddingRequests.UseLovePointItem(packet.Body);
            if (slot > short.MaxValue || actor.Inventory.Find(new(InventoryListType.Main, (short)slot)) is not { } source)
                return await Reject(context, packet.Opcode, packet.Opcode == 1038 ? -7 : -1);
            var changed = packet.Opcode == 1038
                ? MarriageWeddingRules.UpgradeRing(before, catalog, actor.Id, ring.NewLevel, source.Core.ItemId)
                : MarriageWeddingRules.UseLovePointItem(before, catalog, actor.Id, source.Core.ItemId);
            var plan = planner.Plan(actor, [new(actor.Id, changed.Cost.Item.ItemId, -changed.Cost.Item.Count, (ushort)slot)], timer.GetUtcNow(), changed.Cost.Gold);
            if (plan is null || context.Session.CommitMarriage(capture, plan, before, changed.Record) is not { } saved)
                return await Reject(context, packet.Opcode, packet.Opcode == 1038 ? -7 : -1);
            await inventory.RefreshAsync(context, saved.Actor);
            await context.Reply((CmdPacketType)packet.Opcode, packet.Opcode == 1038
                ? MarriageWeddingPackets.RingAck(0, changed.Record.RingLevel) : MarriageWeddingPackets.ResultAck(0), "marriage_item_used");
            await PublishRelationshipAsync(context, changed.Record);
            return CommandResult.Handled;
        }
        if (packet.Opcode == 1040) return await InviteAsync(context, capture, before, MarriageWeddingRequests.SendLetter(packet.Body));
        return await Reject(context, packet.Opcode, -1);
    }

    async Task<CommandResult> RequestWedding(GameConnectionContext context, MarriageCapture capture, MarriageWeddingRequest request)
    {
        if (request.Operation == 2 && (!catalog.Ceremonies.ContainsKey(request.ExtraA) ||
            request.ExtraB >= catalog.Timing.SelectionSeconds.Length)) return await Fail(-3);
        if (request.Operation is 5 or 6)
        {
            var before = store.FindMarriage(capture.Character.Id);
            if (before is null || request.Operation == 5 && before.State == 4 || request.Operation == 6 && before.State != 4)
                return await Fail(-3);
            var end = MarriageWeddingRules.EndRelationship(before, timer.GetUtcNow().ToUnixTimeSeconds());
            if (capture.Character.Inventory.Gold < MarriageWeddingRules.StageEndFee(before, catalog)) return await Fail(-7);
            var plan = planner.Plan(capture.Character, [], timer.GetUtcNow(), MarriageWeddingRules.StageEndFee(before, catalog));
            if (plan is null || context.Session.CommitMarriage(capture, plan, before, end.Record, RefundMail(before, end.Principal)) is not { } saved)
                return await Fail(-3);
            await inventory.RefreshAsync(context, saved.Actor);
            await context.Reply((CmdPacketType)1034, MarriageWeddingPackets.RequestAck(0, request.Target, request.Operation), "marriage_ended");
            await PublishRelationshipAsync(context, end.Record, request.Operation);
            return CommandResult.Handled;
        }
        if (request.Operation is not (1 or 2 or 7) || request.Target > ushort.MaxValue || request.Target == capture.Character.Id)
            return await Fail(-3);
        if (online.FindConnectionByCharacterId((ushort)request.Target) is not { } peer) return await Fail(-2);
        if (peer.Session.MarriageOwner.Account == context.Owner.Account || peer.Session.ChannelEntry?.Identity != context.Session.ChannelEntry?.Identity ||
            !peer.Session.CommandGate.Wait(0)) return await Fail(-3);
        try
        {
            if (peer.Session.CaptureMarriage() is not { } second) return await Fail(-3);
            int failure = -3;
            bool valid = AccountOwners.Lease.ExecuteTogether(context.Owner, peer.Session.MarriageOwner, () =>
            {
                if (!context.Session.MatchesMarriage(capture, true) || !peer.Session.MatchesMarriage(second, true)) return false;
                var firstRelation = store.FindMarriage(capture.Character.Id);
                var secondRelation = store.FindMarriage(second.Character.Id);
                if (request.Operation == 1)
                {
                    if (secondRelation is not null) { failure = -151; return false; }
                    long now = timer.GetUtcNow().ToUnixTimeSeconds();
                    if (!MarriageWeddingRules.CanEngageAgain(store.FindLatestMarriage(capture.Character.Id), catalog, now) ||
                        !MarriageWeddingRules.CanEngageAgain(store.FindLatestMarriage(second.Character.Id), catalog, now)) return false;
                    if (!MarriageWeddingRules.EligibleProposal(capture.Character.Id, capture.Character.Level, capture.Character.Job,
                        firstRelation is null, second.Character.Id, second.Character.Level, second.Character.Job, secondRelation is null, catalog.MinLevel)) return false;
                }
                else if (firstRelation is null || secondRelation?.Id != firstRelation.Id || !firstRelation.IsMember(second.Character.Id) ||
                    request.Operation == 2 && firstRelation.State != 1 || request.Operation == 7 && firstRelation.State != 4) return false;
                lock (offerGate)
                {
                    foreach (var key in offers.Where(row => row.Value.Until < timer.GetUtcNow().ToUnixTimeSeconds()).Select(row => row.Key).ToArray()) offers.Remove(key);
                    if (offers.ContainsKey(second.Character.Id) || offers.Values.Any(row => row.First.Character.Id == capture.Character.Id ||
                        row.First.Character.Id == second.Character.Id || row.Second.Character.Id == capture.Character.Id)) return false;
                    offers.Add(second.Character.Id, new(context.Session, capture, peer.Session, second,
                        request.Operation, request.ExtraA, request.ExtraB, timer.GetUtcNow().ToUnixTimeSeconds() + 60));
                }
                return true;
            });
            if (!valid) return await Fail(failure);
            await context.Reply((CmdPacketType)1034, MarriageWeddingPackets.RequestAck(0, request.Target, request.Operation, request.ExtraA, request.ExtraB), "marriage_offer_sent");
            peer.Probe(1000, MarriageWeddingPackets.RequestResult(0, capture.Character.Id, request.Operation, request.ExtraA, request.ExtraB), "marriage_offer_received");
            return CommandResult.Handled;
        }
        finally { peer.Session.CommandGate.Release(); }

        async Task<CommandResult> Fail(int result)
        {
            log.Write("marriage_failure", new { Connection = context.Id, Opcode = 1034, Result = result, request.Operation });
            await context.Reply((CmdPacketType)1034,
                MarriageFailurePackets.Body(1034, result, request.Target, request.Operation, extraA: request.ExtraA, extraB: request.ExtraB),
                "marriage_rejected");
            return CommandResult.Handled;
        }
    }

    async Task<CommandResult> RespondWedding(GameConnectionContext context, MarriageCapture capture, MarriageWeddingResponse response)
    {
        Offer? offer;
        lock (offerGate)
        {
            offers.TryGetValue(capture.Character.Id, out offer);
            if (offer is not null && ReferenceEquals(offer.Target, context.Session) && offer.Operation == response.Operation &&
                response.Target == offer.First.Character.Id && response.Reserved == 0 && response.Response <= 2)
                offers.Remove(capture.Character.Id);
            else offer = null;
        }
        if (offer is null || offer.Until < timer.GetUtcNow().ToUnixTimeSeconds() || !offer.Source.CommandGate.Wait(0))
            return await Fail(MarriageFailurePackets.Unspecified(1035, response.Operation));
        try
        {
            MarriageSessionCommit? saved = null;
            if (response.Response == 0)
            {
                var before = store.FindMarriage(capture.Character.Id);
                MarriageRecord after;
                uint gold = 0;
                List<MarriageItemDelta> delta = [];
                IReadOnlyList<MailboxDelivery>? mail = null;
                if (offer.Operation == 1)
                {
                    if (before is not null || store.FindMarriage(offer.First.Character.Id) is not null) return await Fail(-2);
                    long now = timer.GetUtcNow().ToUnixTimeSeconds();
                    if (!MarriageWeddingRules.CanEngageAgain(store.FindLatestMarriage(capture.Character.Id), catalog, now) ||
                        !MarriageWeddingRules.CanEngageAgain(store.FindLatestMarriage(offer.First.Character.Id), catalog, now))
                        return await Fail(-6);
                    after = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue),
                        offer.First.Character.Id, capture.Character.Id, timer.GetUtcNow().ToUnixTimeSeconds());
                }
                else
                {
                    if (before is null || !before.IsMember(offer.First.Character.Id))
                        return await Fail(MarriageFailurePackets.Unspecified(1035, response.Operation));
                    if (offer.Operation == 2)
                    {
                        // 准备任务提交已校验通关条件；预约以双方完成态为准，不重复依赖婚约累计次数。
                        bool prepared = catalog.Quests.All(id => offer.First.Character.CompletedQuests.Contains(id) && capture.Character.CompletedQuests.Contains(id));
                        var reservation = MarriageWeddingRules.Reserve(before, catalog, Guid.NewGuid(), offer.First.Character.Id,
                            offer.ExtraA, offer.ExtraB, timer.GetUtcNow().ToUnixTimeSeconds(),
                            timer.GetUtcNow().ToUnixTimeSeconds() + catalog.Timing.SelectionSeconds[offer.ExtraB], prepared);
                        after = reservation.Record;
                        gold = reservation.Cost.Gold;
                        if (reservation.Cost.Item.Count > 0) delta.Add(new(offer.First.Character.Id, reservation.Cost.Item.ItemId, -reservation.Cost.Item.Count));
                        after = AssignHall(after, context.Channel);
                    }
                    else
                    {
                        var end = MarriageWeddingRules.EndRelationship(before, timer.GetUtcNow().ToUnixTimeSeconds());
                        after = end.Record; gold = MarriageWeddingRules.StageEndFee(before, catalog);
                        mail = RefundMail(before, end.Principal);
                    }
                }
                if (offer.First.Character.Inventory.Gold < gold) return await Fail(offer.Operation == 2 ? -13 : -7);
                var firstPlan = planner.Plan(offer.First.Character, delta, timer.GetUtcNow(), gold);
                var secondPlan = planner.Plan(capture.Character, [], timer.GetUtcNow());
                if (firstPlan is null || secondPlan is null)
                    return await Fail(offer.Operation == 2 && delta.Count > 0 ? -14 : MarriageFailurePackets.Unspecified(1035, response.Operation));
                saved = GameSession.CommitMarriageTogether(offer.Source, offer.First, context.Session, capture,
                    firstPlan, secondPlan, before, after, mail,
                    () => ReferenceEquals(offer.Second.Character, capture.Character) && offer.Second.Epoch == capture.Epoch);
                if (saved is null) return await Fail(MarriageFailurePackets.Unspecified(1035, response.Operation));
                await inventory.RefreshAsync(context, saved.Peer!);
                if (online.FindConnectionByCharacterId(offer.First.Character.Id)?.Marriage is { } transport)
                    await transport.PublishInventory(saved.Actor);
            }
            await context.Reply((CmdPacketType)1035, MarriageWeddingPackets.ResponseAck(0, response.Target, response.Operation, response.Response), "marriage_offer_resolved");
            online.FindConnectionByCharacterId(offer.First.Character.Id)?.Probe(1001,
                MarriageWeddingPackets.ResponseResult(0, capture.Character.Id, response.Operation, response.Response, offer.ExtraA, offer.ExtraB), "marriage_offer_resolved");
            if (saved is not null) await PublishRelationshipAsync(context, saved.Record, offer.Operation == 7 ? (byte)7 : (byte)0);
            return CommandResult.Handled;
        }
        catch (MarriageRuleException error)
        {
            log.Write("marriage_rejected", new { Connection = context.Id, Opcode = 1035, error.Reason, Error = error.Message });
            return await Fail(MarriageFailurePackets.Rule(1035, error.Reason, response.Operation));
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or
            InvalidOperationException or OverflowException or DbException)
        {
            log.Write("marriage_rejected", new { Connection = context.Id, Opcode = 1035, Error = error.Message });
            return await Fail(MarriageFailurePackets.Unspecified(1035, response.Operation));
        }
        finally { offer.Source.CommandGate.Release(); }

        async Task<CommandResult> Fail(int result)
        {
            return await RejectResponse(context, response, result, offer);
        }
    }

    async Task<CommandResult> RejectResponse(GameConnectionContext context, MarriageWeddingResponse response, int result, Offer? offer = null)
    {
        if (offer is null && context.Session.Selected is { } actor)
        {
            lock (offerGate)
            {
                if (offers.TryGetValue(actor.Id, out var candidate) && ReferenceEquals(candidate.Target, context.Session) &&
                    candidate.Second.Epoch == context.Session.SelectionEpoch && candidate.Second.Generation == context.Owner.Generation &&
                    candidate.Operation == response.Operation && response.Target == candidate.First.Character.Id &&
                    response.Reserved == 0 && response.Response <= 2)
                {
                    offers.Remove(actor.Id);
                    offer = candidate;
                }
            }
        }
        log.Write("marriage_failure", new { Connection = context.Id, Opcode = 1035, Result = result, response.Operation });
        await context.Reply((CmdPacketType)1035, MarriageFailurePackets.Body(1035, result, response.Target,
            response.Operation, response.Response, (byte)response.Reserved, (byte)(response.Reserved >> 8)), "marriage_rejected");
        // 包括门禁变化后的合法答复；只关闭原邀请发起会话，不消费其他人或新选角的邀请。
        if (offer is not null && online.FindConnectionByCharacterId(offer.First.Character.Id) is { } source &&
            ReferenceEquals(source.Session, offer.Source) && source.Session.MarriageOwner.IsCurrent &&
            source.Session.MarriageOwner.Generation == offer.First.Generation && source.Session.SelectionEpoch == offer.First.Epoch)
            source.Probe(1001, MarriageWeddingPackets.ResponseResult(result, offer.Second.Character.Id,
                offer.Operation, response.Response, offer.ExtraA, offer.ExtraB), "marriage_offer_failed");
        return CommandResult.Handled;
    }

    IReadOnlyList<MailboxDelivery> RefundMail(MarriageRecord before, ImmutableArray<MarriageWeddingRefund> ledger)
    {
        List<MailboxDelivery> result = [];
        foreach (var refund in ledger)
        {
            var recipient = store.FindMarriageRecipient(refund.CharacterId) ?? throw new InvalidOperationException("婚礼退款角色不存在");
            ImmutableArray<InventoryItem> attachments = [];
            if (refund.Item.Count > 0)
            {
                var template = items.Get(refund.Item.ItemId);
                attachments = [InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Main, 0),
                    InventoryRewardGrantService.Create(template, refund.Item.Count, timer.GetUtcNow()))];
            }
            uint remaining = refund.Gold;
            int part = 0;
            do
            {
                uint gold = Math.Min(remaining, 100_000_000u);
                result.Add(new($"marriage:{before.Id:N}:{before.Wedding!.Id:N}:refund:{refund.CharacterId}:{refund.GiftId:N}:{part}",
                    recipient, "结婚系统", "婚礼取消，退还实际支付的费用。", gold, part == 0 ? attachments : [], timer.GetUtcNow().ToUnixTimeSeconds()));
                remaining -= gold;
                part++;
            } while (remaining > 0);
        }
        return result;
    }
}
