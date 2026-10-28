using DfoServer.Game.Guild;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildApplicationHandler(GuildApplications applications, GuildManagementService management,
    ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        foreach (var opcode in new[] { CmdPacketType.SEARCH_GUILD_LIST, CmdPacketType.REQUEST_ACCOUNT_GUILD_LIST,
            CmdPacketType.CHECK_JOIN_GUILD, CmdPacketType.REQUEST_JOIN_GUILD, CmdPacketType.CANCEL_JOIN_GUILD,
            CmdPacketType.APPROVE_JOIN_GUILD, CmdPacketType.DENY_JOIN_GUILD, CmdPacketType.GUILD_JOIN_LIST,
            CmdPacketType.REQ_GUILD_INFO_OF_MY_CHARS, CmdPacketType.REQ_GUILD_SERCH_FOR_JOIN, CmdPacketType.REQ_RECOMMEND_GUILD,
            CmdPacketType.JOIN_GUILD_INFO })
            commands.Register(opcode, HandleAsync, nameof(GuildApplicationHandler));
    }

    async Task<CommandResult> HandleAsync(GameConnectionContext context, Packet request)
    {
        var opcode = (CmdPacketType)request.Opcode;
        if (opcode == CmdPacketType.JOIN_GUILD_INFO && request.Body.Length != 0) return CommandResult.Unhandled;
        var actor = management.Capture(context.Session);
        if (actor is null || context.BlocksTownActions)
        {
            if (opcode != CmdPacketType.JOIN_GUILD_INFO)
                await FailureAsync(context, request, opcode == CmdPacketType.GUILD_JOIN_LIST ? (ushort)36 : (ushort)23);
            return CommandResult.Handled;
        }
        try
        {
            switch (opcode)
            {
                case CmdPacketType.JOIN_GUILD_INFO:
                    // 原生申请窗口只有一个槽；多份真实申请按仓储的创建时间、公会ID顺序选择首项。
                    var pending = applications.Account(actor).Applications.FirstOrDefault()?.Guild;
                    await context.Notify(NotiPacketType.JOIN_GUILD_INFO,
                        GuildApplicationPackets.PendingApplication(pending), "guild_pending_application", actor.IsCurrent);
                    break;
                case CmdPacketType.SEARCH_GUILD_LIST:
                    await context.Reply(opcode, GuildApplicationPackets.Search(applications.Search(
                        GuildApplicationRequests.Search(request.Body))), "guild_search", actor.IsCurrent);
                    break;
                case CmdPacketType.CHECK_JOIN_GUILD:
                case CmdPacketType.REQ_GUILD_SERCH_FOR_JOIN:
                {
                    // 745带尾字段，347不带；两者共用同一条查找与回复分支。
                    string name = (opcode == CmdPacketType.REQ_GUILD_SERCH_FOR_JOIN
                        ? GuildRequests.SearchText(request.Body) : GuildRequests.Text(request.Body)).Trim();
                    var guild = applications.Find(name);
                    if (guild is null) await FailureAsync(context, request, 23, actor);
                    else await context.Reply(opcode, opcode == CmdPacketType.CHECK_JOIN_GUILD
                        ? GuildApplicationPackets.JoinCheck(guild, management.MemberLimit)
                        : GuildApplicationPackets.SearchResult(guild), "guild_join_detail", actor.IsCurrent);
                    break;
                }
                case CmdPacketType.REQUEST_ACCOUNT_GUILD_LIST:
                case CmdPacketType.REQ_GUILD_INFO_OF_MY_CHARS:
                    RequireEmpty(request);
                    await DeliverAccountAsync(context, actor);
                    break;
                case CmdPacketType.REQ_RECOMMEND_GUILD:
                {
                    RequireEmpty(request);
                    var guilds = applications.Search(new(2)).Guilds;
                    var account = applications.Account(actor);
                    var index = account with { Guilds = guilds.Concat(account.Guilds).DistinctBy(guild => guild.Id).Take(255).ToArray() };
                    await context.Reply(CmdPacketType.REQUEST_ACCOUNT_GUILD_LIST,
                        GuildApplicationPackets.AccountIndex(index), "guild_recommendation_index", actor.IsCurrent);
                    await context.Reply(opcode, GuildApplicationPackets.Recommendations(guilds), "guild_recommendations", actor.IsCurrent);
                    break;
                }
                case CmdPacketType.REQUEST_JOIN_GUILD:
                {
                    var (name, message) = GuildApplicationRequests.Apply(request.Body);
                    var result = Mutate(context, () => applications.Apply(actor, name, message));
                    if (!result.Success) await FailureAsync(context, request, result.Error, actor);
                    else
                    {
                        await context.Reply(opcode, GuildApplicationPackets.Applied(result.Guild!), "guild_application", actor.IsCurrent);
                        applications.Publish(result);
                        await DeliverAccountAsync(context, actor);
                    }
                    break;
                }
                case CmdPacketType.CANCEL_JOIN_GUILD:
                {
                    uint id = GuildApplicationRequests.Cancel(request.Body);
                    var result = Mutate(context, () => applications.Cancel(actor, id));
                    if (!result.Success) await FailureAsync(context, request, result.Error, actor);
                    else
                    {
                        await context.Reply(opcode, [1], "guild_application_cancel", actor.IsCurrent);
                        await DeliverAccountAsync(context, actor);
                    }
                    break;
                }
                case CmdPacketType.GUILD_JOIN_LIST:
                    RequireEmpty(request);
                    await DeliverListAsync(context, actor);
                    break;
                case CmdPacketType.APPROVE_JOIN_GUILD:
                case CmdPacketType.DENY_JOIN_GUILD:
                {
                    bool approve = opcode == CmdPacketType.APPROVE_JOIN_GUILD;
                    var (id, region) = GuildApplicationRequests.Decision(request.Body, approve);
                    var result = Mutate(context, () => applications.Decide(actor, id, region, approve));
                    if (!result.Success) await FailureAsync(context, request, result.Error, actor);
                    else
                    {
                        await context.Reply(opcode, [1, .. request.Body], "guild_application_decision", actor.IsCurrent);
                        applications.Publish(result);
                    }
                    // 原生窗口先乐观移除行，失败时必须从已提交集合恢复，不能丢掉仍有效的申请。
                    await DeliverListAsync(context, actor);
                    break;
                }
            }
        }
        catch (InvalidDataException)
        {
            await FailureAsync(context, request, opcode == CmdPacketType.GUILD_JOIN_LIST ? (ushort)36 : (ushort)23, actor);
            if (opcode is CmdPacketType.APPROVE_JOIN_GUILD or CmdPacketType.DENY_JOIN_GUILD)
                await DeliverListAsync(context, actor);
        }
        return CommandResult.Handled;
    }

    GuildApplicationCommit Mutate(GameConnectionContext context, Func<GuildApplicationCommit> mutation)
    {
        try { return mutation(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log.Write("guild_application_failed", new { Connection = context.Id, Error = error.Message });
            return new(23);
        }
    }

    public async Task DeliverAccountAsync(GameConnectionContext context, GuildManagementIdentity? actor = null)
    {
        actor ??= management.Capture(context.Session);
        if (actor is null) return;
        var account = applications.Account(actor);
        await context.Reply(CmdPacketType.REQUEST_ACCOUNT_GUILD_LIST,
            GuildApplicationPackets.AccountIndex(account), "guild_account_index", actor.IsCurrent);
        await context.Reply(CmdPacketType.REQ_GUILD_INFO_OF_MY_CHARS,
            GuildApplicationPackets.AccountApplications(account), "guild_account_applications", actor.IsCurrent);
    }

    public async Task DeliverListAsync(GameConnectionContext context, GuildManagementIdentity actor)
    {
        var result = applications.List(actor);
        await context.Reply(CmdPacketType.GUILD_JOIN_LIST, result.Error == 0
            ? GuildApplicationPackets.Applications(result.Rows) : GuildApplicationPackets.Failure(36),
            "guild_application_list", actor.IsCurrent);
    }

    static Task FailureAsync(GameConnectionContext context, Packet request, ushort error, GuildManagementIdentity? actor = null)
    {
        byte[] echo = request.Opcode == (ushort)CmdPacketType.APPROVE_JOIN_GUILD
            ? request.Body.Length == 5 ? request.Body : new byte[5] : [];
        return context.Reply((CmdPacketType)request.Opcode, GuildApplicationPackets.Failure(error, echo),
            "guild_application_rejected", actor?.IsCurrent);
    }

    static void RequireEmpty(Packet request)
    {
        if (request.Body.Length != 0) throw new InvalidDataException("Unexpected guild query body");
    }
}
