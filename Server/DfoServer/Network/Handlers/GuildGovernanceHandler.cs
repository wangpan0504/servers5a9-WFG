using DfoServer.Game.Guild;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildGovernanceHandler(GuildGovernanceService service, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.MODIFY_GUILD_PROMOTE_MSG, SettingsAsync, nameof(GuildGovernanceHandler));
        commands.Register(CmdPacketType.CHANGE_GUILD_GRADE, PermissionsAsync, nameof(GuildGovernanceHandler));
        commands.Register(CmdPacketType.CHANGE_GUILD_GRADE_NAME, NameAsync, nameof(GuildGovernanceHandler));
        commands.Register(CmdPacketType.SET_SUB_GUILD_MASTER, MemberAsync, nameof(GuildGovernanceHandler));
        commands.Register(CmdPacketType.SET_GUILD_RECOMMAND_CHANNEL, ChannelAsync, nameof(GuildGovernanceHandler));
    }

    Task<CommandResult> ChannelAsync(GameConnectionContext context, Packet request) =>
        request.Body.Length == 0
            ? FinishAsync(context, request, new(GuildGovernanceAction.RecommendedChannel))
            : RejectAsync(context, request, 7);

    Task<CommandResult> SettingsAsync(GameConnectionContext context, Packet request) =>
        GuildGovernanceRequests.TrySettings(request.Body, out var change)
            ? FinishAsync(context, request, new(GuildGovernanceAction.Settings, Settings: change))
            : RejectAsync(context, request, 108);

    Task<CommandResult> PermissionsAsync(GameConnectionContext context, Packet request) =>
        GuildGovernanceRequests.TryPermissions(request.Body, out byte grade, out uint mask)
            ? FinishAsync(context, request, new(GuildGovernanceAction.RolePermissions, Grade: grade, Permissions: mask))
            : RejectAsync(context, request, 48);

    Task<CommandResult> NameAsync(GameConnectionContext context, Packet request) =>
        GuildGovernanceRequests.TryRoleName(request.Body, out byte grade, out string name)
            ? FinishAsync(context, request, new(GuildGovernanceAction.RoleName, Grade: grade, Text: name))
            : RejectAsync(context, request, 159);

    Task<CommandResult> MemberAsync(GameConnectionContext context, Packet request)
    {
        if (!GuildGovernanceRequests.TryMemberGrade(request.Body, out byte region, out string name, out byte grade))
            return RejectAsync(context, request, 3);
        var target = service.Target(name);
        if (target is null || target.Name != name) return RejectAsync(context, request, 3);
        return FinishAsync(context, request, new(GuildGovernanceAction.MemberGrade, Grade: grade, Target: target, Region: region));
    }

    async Task<CommandResult> RejectAsync(GameConnectionContext context, Packet request, ushort error)
    {
        if (service.Capture(context.Session) is { } actor)
            await context.Reply((CmdPacketType)request.Opcode, [0, (byte)error, (byte)(error >> 8)],
                "guild_governance_rejected", actor.IsCurrent);
        return CommandResult.Handled;
    }

    async Task<CommandResult> FinishAsync(GameConnectionContext context, Packet request, GuildGovernanceChange change)
    {
        if (service.Capture(context.Session) is not { } actor) return CommandResult.Handled;
        var commit = service.Change(actor, change);
        byte[] body = commit.Success
            ? change.Action == GuildGovernanceAction.MemberGrade
                ? GuildGovernancePackets.MemberGrade(change.Region, change.Target!.Name, change.Grade) : [1]
            : [0, (byte)commit.Error, (byte)(commit.Error >> 8)];
        await context.Reply((CmdPacketType)request.Opcode, body, "guild_governance", actor.IsCurrent);
        if (commit.Success) service.Publish(commit);
        log.Write("guild_governance", new { Connection = context.Id, Character = actor.CharacterId,
            Guild = commit.GuildId, Action = change.Action.ToString(), Error = commit.Error, commit.Revision });
        return CommandResult.Handled;
    }
}
