using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class RaidHandler
{
    async Task<CommandResult> CheckUserAsync(Protocol.GameConnectionContext context, Packet request)
    {
        Parsers.RaidUserRequest parsed;
        try { parsed = Parsers.RaidRequests.CheckUser(request.Body); }
        catch (InvalidDataException) { return CommandResult.Unhandled; }
        if (Actor(context) is not { } actor) return CommandResult.Unhandled;
        var target = online?.FindReadyCharacter(parsed.Character)?.CaptureRaidRejoinActor(registry);
        bool found = target is not null && target.Channel.ServerIndex == parsed.Server && target.Channel.Number == parsed.Channel &&
            target.Session.ChannelEntry is { } entry && MatchesType(actor, Game.Raids.RaidTypes.FromChannelType(entry.Type));
        var party = found ? registry.Find(target!) : null;
        // 查询只回报当时的真实目录，不建邀请、不加入、不为已离线目标伪造成功。
        await context.Reply(CmdPacketType.RAID_CHECK_RAID_USER,
            found ? Builders.RaidPackets.CheckedUser(party) : Builders.RaidPackets.CreateFailure(),
            found ? "raid_user_checked" : "raid_user_unavailable", () => actor.IsCurrent);
        log.Write("raid_user_check", new { Connection = context.Id, parsed.Character, parsed.Server, parsed.Channel,
            Found = found, Key = party?.Key.ToString("X8"), Members = party?.Members.Length ?? 0 });
        return CommandResult.Handled;
    }

    Task<CommandResult> MemberStateAsync(Protocol.GameConnectionContext context, Packet request)
    {
        bool inactive;
        try { inactive = Parsers.RaidRequests.MemberState(request.Body); }
        catch (InvalidDataException) { return Task.FromResult(CommandResult.Unhandled); }
        if (context.Session.CaptureRaidRejoinActor(registry) is not { } actor) return Task.FromResult(CommandResult.Unhandled);
        var change = registry.ChangeMemberState(actor, inactive);
        if (change is not null) registry.Publish(change);
        return Task.FromResult(CommandResult.Handled);
    }
}
