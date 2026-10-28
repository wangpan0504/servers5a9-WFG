using DfoServer.Game.Guild;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildManagementHandler(GuildManagementService service) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.CALL_GUILD_INVITE, InviteAsync, nameof(GuildManagementHandler));
        commands.Register(CmdPacketType.REPLY_GUILD_INVITE, AnswerAsync, nameof(GuildManagementHandler));
        commands.Register(CmdPacketType.REQ_GUILD_SECEDE, ChangeAsync, nameof(GuildManagementHandler));
        commands.Register(CmdPacketType.BREAK_GUILD, ChangeAsync, nameof(GuildManagementHandler));
        commands.Register(CmdPacketType.NOTIFY_MESSAGE_TO_GUILD, ChangeAsync, nameof(GuildManagementHandler));
        commands.Register(CmdPacketType.WRITE_GUILD_MEMBER_MEMO, ChangeAsync, nameof(GuildManagementHandler));
    }

    async Task<CommandResult> InviteAsync(GameConnectionContext context, Packet request)
    {
        ushort error = 3;
        var actor = service.Capture(context.Session);
        if (actor is not null && GuildManagementRequests.TryTarget(request.Body, false, out byte region, out string name))
            error = service.Offer(actor with { Channel = context.Channel }, region, name);
        await context.Reply(CmdPacketType.CALL_GUILD_INVITE, error == 0 ? [1] : GuildManagementPackets.Error(error),
            "guild_invitation_answer", actor?.IsCurrent);
        return CommandResult.Handled;
    }

    async Task<CommandResult> AnswerAsync(GameConnectionContext context, Packet request)
    {
        var actor = service.Capture(context.Session);
        if (actor is null || request.Body.Length != 1 || request.Body[0] > 1)
        {
            await context.Reply(CmdPacketType.REPLY_GUILD_INVITE, GuildManagementPackets.Error(103), "guild_invitation_invalid");
            return CommandResult.Handled;
        }
        bool accept = request.Body[0] == 1;
        var result = service.Answer(actor with { Channel = context.Channel }, accept);
        // 原生成功152会提示“已加入公会”，拒绝与窗口超时不能发送成功确认。
        if (accept)
            await context.Reply(CmdPacketType.REPLY_GUILD_INVITE, result.Success ? [1] : GuildManagementPackets.Error(result.Error),
                "guild_invitation_decision", actor.IsCurrent);
        if (accept && result.Success) service.Publish(result);
        return CommandResult.Handled;
    }

    async Task<CommandResult> ChangeAsync(GameConnectionContext context, Packet request)
    {
        var opcode = (CmdPacketType)request.Opcode;
        var actor = service.Capture(context.Session);
        if (actor is null)
        {
            await context.Reply(opcode, GuildManagementPackets.Error(34), "guild_management_unavailable");
            return CommandResult.Handled;
        }
        GuildManagementChange? change = null;
        ushort invalid = 3;
        if (opcode == CmdPacketType.REQ_GUILD_SECEDE &&
            GuildManagementRequests.TryTarget(request.Body, true, out byte region, out string name))
        {
            if (name.Length == 0)
            {
                if (region == 0 || region == context.Channel.ServerIndex)
                    change = new(GuildManagementAction.Leave, null);
            }
            else if (region == context.Channel.ServerIndex && service.Target(name) is { } target)
                change = new(GuildManagementAction.Leave, target);
        }
        else if (opcode == CmdPacketType.BREAK_GUILD && request.Body.Length == 0)
            change = new(GuildManagementAction.Disband, null);
        else if (opcode is CmdPacketType.NOTIFY_MESSAGE_TO_GUILD or CmdPacketType.WRITE_GUILD_MEMBER_MEMO)
        {
            bool notice = opcode == CmdPacketType.NOTIFY_MESSAGE_TO_GUILD;
            invalid = 108;
            if (GuildManagementRequests.TryText(request.Body, notice ? 255 : 20, notice, out string text))
                change = new(notice ? GuildManagementAction.Notice : GuildManagementAction.Memo, null, text);
        }
        var result = change is null ? new GuildManagementCommit(invalid) : service.Change(actor, change);
        byte[] body = !result.Success ? GuildManagementPackets.Error(result.Error) :
            opcode == CmdPacketType.REQ_GUILD_SECEDE ? GuildManagementPackets.Departed(result) : [1];
        await context.Reply(opcode, body, "guild_management_change", actor.IsCurrent);
        service.Publish(result);
        return CommandResult.Handled;
    }
}
