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
        commands.Register(CmdPacketType.GUILD_MASTER_DELEGATE, MasterDelegateAsync, nameof(GuildGovernanceHandler));
        commands.Register(CmdPacketType.OPEN_GUILD_BOARD, OpenBoardAsync, nameof(GuildGovernanceHandler));
        commands.Register(CmdPacketType.REFRESH_GUILDBOARD, OpenBoardAsync, nameof(GuildGovernanceHandler));
        commands.Register(CmdPacketType.WRITE_ON_THE_GUILDBOARD, WriteBoardAsync, nameof(GuildGovernanceHandler));
        commands.Register(CmdPacketType.DELETE_GUILDBOARD_TEXT, DeleteBoardAsync, nameof(GuildGovernanceHandler));
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

    Task<CommandResult> MasterDelegateAsync(GameConnectionContext context, Packet request)
    {
        if (!GuildGovernanceRequests.TryMasterDelegate(request.Body, out byte region, out string name))
            return RejectAsync(context, request, 86);
        var target = service.Target(name);
        if (target is null || target.Name != name) return RejectAsync(context, request, 86);
        if (region != 0 && service.Capture(context.Session) is { } actor && region != actor.Channel.ServerIndex)
            return RejectAsync(context, request, 86);
        return FinishAsync(context, request, new(GuildGovernanceAction.MasterTransfer, Target: target, Region: region));
    }

    async Task<CommandResult> OpenBoardAsync(GameConnectionContext context, Packet request)
    {
        if (!GuildGovernanceRequests.TryBoardPage(request.Body, out byte page))
            return await RejectAsync(context, request, 7);
        if (service.Capture(context.Session) is not { } actor) return CommandResult.Handled;
        if (service.OpenBoard(actor, page) is not { } result)
            return await RejectAsync(context, request, 34);
        await context.Reply((CmdPacketType)request.Opcode, GuildGovernancePackets.BoardPage(result),
            "guild_board_open", actor.IsCurrent);
        return CommandResult.Handled;
    }

    async Task<CommandResult> WriteBoardAsync(GameConnectionContext context, Packet request)
    {
        if (!GuildGovernanceRequests.TryBoardWrite(request.Body, out string content))
            return await RejectAsync(context, request, 108);
        if (service.Capture(context.Session) is not { } actor) return CommandResult.Handled;
        var commit = service.WriteBoard(actor, content);
        if (!commit.Success) return await RejectAsync(context, request, commit.Error);
        await context.Reply(CmdPacketType.WRITE_ON_THE_GUILDBOARD, GuildGovernancePackets.BoardPostAck(commit.Post!.PostId),
            "guild_board_write", actor.IsCurrent);
        log.Write("guild_board_write", new { Connection = context.Id, Character = actor.CharacterId,
            Guild = commit.Page?.GuildId, commit.Post.PostId });
        return CommandResult.Handled;
    }

    async Task<CommandResult> DeleteBoardAsync(GameConnectionContext context, Packet request)
    {
        if (!GuildGovernanceRequests.TryBoardDelete(request.Body, out uint postId))
            return await RejectAsync(context, request, 7);
        if (service.Capture(context.Session) is not { } actor) return CommandResult.Handled;
        var commit = service.DeleteBoard(actor, postId);
        if (!commit.Success) return await RejectAsync(context, request, commit.Error);
        await context.Reply(CmdPacketType.DELETE_GUILDBOARD_TEXT, GuildGovernancePackets.BoardPostAck(postId),
            "guild_board_delete", actor.IsCurrent);
        log.Write("guild_board_delete", new { Connection = context.Id, Character = actor.CharacterId,
            Guild = commit.Page?.GuildId, PostId = postId });
        return CommandResult.Handled;
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
