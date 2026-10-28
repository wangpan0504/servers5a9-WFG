using System.Security.Cryptography;
using DfoServer.Game.Marriage;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class MarriageHandler
{
    MarriageRecord AssignHall(MarriageRecord record, ChannelIdentity channel)
    {
        var occupied = store.FindPendingWeddings().Where(row => row.Id != record.Id &&
            row.Wedding!.ServerIndex == channel.ServerIndex && row.Wedding.ChannelNumber == channel.Number)
            .Select(row => row.Wedding!.HallArea).ToHashSet();
        for (byte area = 8; area < 38; area++)
            if (!occupied.Contains(area) && maps.Areas.ContainsKey((39, area)))
                return record with { Wedding = record.Wedding! with { HallArea = area, ServerIndex = channel.ServerIndex, ChannelNumber = channel.Number } };
        throw new InvalidOperationException("本频道的婚礼礼堂已满");
    }

    async Task<CommandResult> PartnerCardAsync(GameConnectionContext context, MarriageRecord record)
    {
        if (context.Session.CaptureMarriage(false) is not { } capture || !record.IsMember(capture.Character.Id) ||
            store.FindMarriageCard(record.Other(capture.Character.Id)) is not { } card)
            return await Reject(context, 1037, -2);
        await context.Reply((CmdPacketType)1037, MarriageProjection.Card(record.ContextId, card), "marriage_partner_card",
            () => context.Session.MatchesMarriage(capture, false));
        return CommandResult.Handled;
    }

    async Task PublishRelationshipAsync(GameConnectionContext context, MarriageRecord record, byte reason = 0)
    {
        var projection = new MarriageProjection(store, catalog, online, timer);
        await projection.PublishAsync(context, record, reason);
        if (record.State != 2 || record.Wedding is not { State: 1 } wedding ||
            wedding.StartsAt <= timer.GetUtcNow().ToUnixTimeSeconds()) return;
        // 双方确认预约后的自动入场也复用正式旅行收据；不能只发位置包而不保存位置。
        foreach (ushort id in new[] { record.FirstId, record.SecondId })
        {
            if (online.FindConnectionByCharacterId(id) is not { Marriage: { } transport } connection ||
                connection.Session.CaptureMarriage() is not { } capture ||
                connection.Session.ChannelEntry?.Identity != new ChannelIdentity(wedding.ServerIndex, wedding.ChannelNumber) ||
                HallPosition(capture, wedding) is not { } position ||
                connection.Session.CommitMarriageTravel(capture, position, record, timer.GetUtcNow()) is not { } receipt) continue;
            await transport.Travel(receipt);
        }
    }

    async Task<CommandResult> InviteAsync(GameConnectionContext context, MarriageCapture capture,
        MarriageRecord record, MarriageLetterRequest request)
    {
        if (record.Wedding is not { } wedding || timer.GetUtcNow().ToUnixTimeSeconds() >= wedding.StartsAt)
            return await Reject(context, 1040, -2);
        if (request.Option == 1)
        {
            if (request.Target != 0 || request.TargetTempId != 0 || request.TargetType != 0)
                return await Reject(context, 1040, -2);
            // 客户端按钮5为“随机发送”；从当前频道的实际在线角色选择，不能生成虚假收件人。
            var candidates = online.Snapshot().Where(row => row.Ready && !record.IsMember(row.CharacterId) &&
                !wedding.Invitations.Any(invitation => invitation.CharacterId == row.CharacterId))
                .Where(row => online.FindConnectionByCharacterId(row.CharacterId)?.Session.ChannelEntry?.Identity == context.Channel).ToArray();
            if (candidates.Length == 0) return await Reject(context, 1040, -2);
            request = new(0, candidates[RandomNumberGenerator.GetInt32(candidates.Length)].CharacterId, 0, 0);
        }
        if (request.Target is 0 or > ushort.MaxValue || request.Option != 0 ||
            request.TargetTempId != 0 && request.TargetTempId != request.Target ||
            store.FindMarriageRecipient((ushort)request.Target) is not { } target)
            return await Reject(context, 1040, -2);
        var after = MarriageWeddingRules.SendInvitation(record, capture.Character.Id, target.Id, target.Name, timer.GetUtcNow().ToUnixTimeSeconds());
        var plan = planner.Plan(capture.Character, [], timer.GetUtcNow());
        if (plan is null || context.Session.CommitMarriage(capture, plan, record, after) is not { } saved)
            return await Reject(context, 1040, -3);
        await context.Reply((CmdPacketType)1040, MarriageWeddingPackets.LetterAck(0,
            checked((ushort)saved.Record.Wedding!.InviteRemaining[capture.Character.Id])), "marriage_invitation_sent");
        if (online.FindConnectionByCharacterId(target.Id) is { Marriage: { } transport } peer &&
            peer.Session.CaptureMarriage(false) is { } targetCapture)
            await transport.Notify(1008, MarriageWeddingPackets.Letter(new MarriageProjection(store, catalog, online, timer).Wire(saved.Record), 0, 0),
                "marriage_invitation", () => peer.Session.MatchesMarriage(targetCapture, false));
        return CommandResult.Handled;
    }

    async Task<CommandResult> EnterWeddingAsync(GameConnectionContext context, MarriageCapture capture,
        MarriageRecord record, MarriageEnterCeremonyRequest request)
    {
        if (request.Context != record.ContextId || request.Flag > 1 || record.Wedding is not { State: 1 } wedding ||
            request.Destination != wedding.HallArea || context.Channel != new ChannelIdentity(wedding.ServerIndex, wedding.ChannelNumber) ||
            timer.GetUtcNow().ToUnixTimeSeconds() >= wedding.StartsAt ||
            !record.IsMember(capture.Character.Id) && (!wedding.Invitations.Any(row => row.CharacterId == capture.Character.Id) ||
                !wedding.Gifts.Any(row => row.CharacterId == capture.Character.Id && !row.Settled)) ||
            HallPosition(capture, wedding) is not { } position)
            return await Reject(context, 1041, -2);
        if (context.Session.CommitMarriageTravel(capture, position, record, timer.GetUtcNow()) is not { } receipt) return await Reject(context, 1041, -2);
        await context.Reply((CmdPacketType)1041, MarriageWeddingPackets.ResultAck(0), "marriage_enter_ceremony",
            () => context.Session.CanPublishTravel(receipt));
        await town.TravelAsync(context, receipt);
        return CommandResult.Handled;
    }

    Location? HallPosition(MarriageCapture capture, MarriageWeddingState wedding)
    {
        if (!maps.Areas.TryGetValue((39, wedding.HallArea), out var area)) return null;
        var map = area.Map(capture.Character.CompletedQuests.ToHashSet());
        var bounds = map.Bounds.FirstOrDefault(row => row.Conditions.Length == 0 && row.Quests.All(capture.Character.CompletedQuests.Contains))?.Bounds;
        if (bounds is null || map.Error is not null) return null;
        return new(39, wedding.HallArea, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
    }
}
