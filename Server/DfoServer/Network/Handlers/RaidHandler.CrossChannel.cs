using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class RaidHandler
{
    async Task<CommandResult> CrossRequestAsync(GameConnectionContext context, Packet request)
    {
        RaidCrossRequest parsed;
        try { parsed = RaidRequests.CrossRequest(request.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        if (Actor(context) is not { } actor || online is null) return CommandResult.Unhandled;
        int delivered = 0;
        foreach (var locator in parsed.Targets.Distinct())
        {
            var target = online.FindReadyCharacter(locator.Character)?.CaptureRaidActor(registry);
            if (target is null || target.Channel.ServerIndex != locator.Server) continue;
            var invitation = registry.InviteCross(actor, target, parsed.Coins, parsed.AntiMagic);
            if (invitation is not null && RaidCrossChannelProjection.Request(online, registry, invitation, lukeStart is not null, context.MarriageAppearance))
                delivered++;
        }
        log.Write("raid_cross_request", new { Connection = context.Id, Self = actor.Character,
            Requested = parsed.Targets.Length, Delivered = delivered });
        await context.Reply(CmdPacketType.RAID_OTHER_CHANNEL_REQUEST_JOIN,
            delivered > 0 ? [1] : RaidCrossChannelPackets.Failure, "raid_cross_requested", () => actor.IsCurrent);
        return CommandResult.Handled;
    }

    async Task<CommandResult> CrossResponseAsync(GameConnectionContext context, Packet request)
    {
        RaidUserRequest parsed;
        try { parsed = RaidRequests.CrossResponse(request.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        if (Actor(context) is not { } actor || online is null) return CommandResult.Unhandled;
        var applicant = online.FindReadyCharacter(parsed.Character)?.CaptureRaidActor(registry);
        RaidCrossTransfer? transfer = applicant is not null && applicant.Channel.ServerIndex == parsed.Server &&
            applicant.Channel.Number == parsed.Channel ? registry.AcceptCross(actor, applicant) : null;
        bool delivered = transfer is not null && RaidCrossChannelProjection.Response(online, registry, transfer, lukeStart is not null, context.MarriageAppearance);
        log.Write("raid_cross_response", new { Connection = context.Id, Self = actor.Character,
            Target = parsed.Character, Delivered = delivered, Raid = transfer?.Party.Key });
        await context.Reply(CmdPacketType.RAID_OTHER_CHANNEL_RESPONSE_JOIN,
            delivered ? [1] : RaidCrossChannelPackets.Failure, "raid_cross_responded", () => actor.IsCurrent);
        return CommandResult.Handled;
    }

    Task<CommandResult> CrossCancelAsync(GameConnectionContext context, Packet request)
    {
        RaidCrossTarget parsed;
        try { parsed = RaidRequests.CrossCancel(request.Body); }
        catch (InvalidDataException) { return Task.FromResult(CommandResult.Unhandled); }
        if (Actor(context) is not { } actor) return Task.FromResult(CommandResult.Unhandled);
        bool cancelled = registry.CancelCross(actor, parsed.Server, parsed.Character);
        log.Write("raid_cross_cancel", new { Connection = context.Id, Self = actor.Character, Target = parsed.Character, Cancelled = cancelled });
        // 826是失败收尾通知，未证ACK消费者，不回造成功。
        return Task.FromResult(CommandResult.Handled);
    }
}
