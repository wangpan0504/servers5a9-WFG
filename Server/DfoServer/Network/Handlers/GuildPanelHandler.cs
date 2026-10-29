using DfoServer.Game.Guild;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;

namespace DfoServer.Network.Handlers;

internal sealed class GuildPanelHandler(GuildPanelService service, ServerLog log, GuildNpcProjection? npcs = null,
    GuildDungeonProjection? dungeons = null, GuildRankingService? ranking = null) : IGameCommandModule
{
    const int RowsPerPacket = 128;

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.REFRESH_GUILD_INFO, RefreshAsync, nameof(GuildPanelHandler));
        commands.Register(CmdPacketType.GUILD_MEMER_LIST, MembersAsync, nameof(GuildPanelHandler));
        commands.Register(CmdPacketType.GUILD_ALL_MEMBER_LIST, MembersAsync, nameof(GuildPanelHandler));
        commands.Register(CmdPacketType.GUILD_ALL_MEMBER_GRADE, GradesAsync, nameof(GuildPanelHandler));
    }

    async Task<CommandResult> GradesAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0) return CommandResult.Unhandled;
        if (context.Session.CaptureGuildPanelQuery() is not { } query) return CommandResult.Handled;
        var panel = service.Read(context.Owner, query.CharacterId, context.Channel.ServerIndex);
        if (panel is null)
        {
            await context.Reply(CmdPacketType.GUILD_ALL_MEMBER_GRADE, [0, 0, 0],
                "guild_member_grades_unavailable", query.IsCurrent);
            return CommandResult.Handled;
        }
        for (int offset = 0; offset < panel.Members.Length || offset == 0; offset += RowsPerPacket)
        {
            bool first = offset == 0;
            int count = Math.Min(RowsPerPacket, panel.Members.Length - offset);
            await context.Reply(first ? CmdPacketType.GUILD_ALL_MEMBER_GRADE : CmdPacketType.GUILD_ALL_MEMBER_GRADE_NEXT,
                GuildPackets.MemberGrades(panel, offset, count, first), "guild_member_grades", query.IsCurrent);
        }
        log.Write("guild_member_grades", new { Connection = context.Id, Character = query.CharacterId,
            Guild = panel.Guild.Id, Count = panel.Members.Length });
        return CommandResult.Handled;
    }

    async Task<CommandResult> RefreshAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 0) return CommandResult.Unhandled;
        if (context.Session.CaptureGuildPanelQuery() is not { } query) return CommandResult.Handled;
        var panel = service.Read(context.Owner, query.CharacterId, context.Channel.ServerIndex);
        if (panel is not null)
        {
            await RankingAsync(context, query.IsCurrent);
            await context.Notify(NotiPacketType.GUILD_INFO,
                GuildPackets.Information(panel.Guild, context.Channel.ServerIndex, panel), "guild_panel_information", query.IsCurrent);
            npcs?.Post(context);
            dungeons?.Post(context, panel, query.IsCurrent);
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> MembersAsync(GameConnectionContext context, Packet request)
    {
        bool all = request.Opcode == (ushort)CmdPacketType.GUILD_ALL_MEMBER_LIST;
        if (!GuildRequests.TryMemberList(request.Body, all, out uint reference)) return CommandResult.Unhandled;
        if (context.Session.CaptureGuildPanelQuery() is not { } query) return CommandResult.Handled;
        var panel = service.Read(context.Owner, query.CharacterId, context.Channel.ServerIndex);
        if (panel is null)
        {
            // 02365BE0失败分支不消费成员正文，不伪造空公会成功或未知业务错误文案。
            await context.Reply((CmdPacketType)request.Opcode, [0, 0, 0], "guild_panel_unavailable", query.IsCurrent);
            return CommandResult.Handled;
        }
        await RankingAsync(context, query.IsCurrent);
        await context.Notify(NotiPacketType.GUILD_INFO,
            GuildPackets.Information(panel.Guild, context.Channel.ServerIndex, panel), "guild_panel_information", query.IsCurrent);
        for (int offset = 0; offset < panel.Members.Length || offset == 0; offset += RowsPerPacket)
        {
            bool first = offset == 0;
            var opcode = first ? (CmdPacketType)request.Opcode :
                all ? CmdPacketType.GUILD_ALL_MEMBER_LIST_NEXT : CmdPacketType.GUILD_MEMER_LIST_NEXT;
            int count = Math.Min(RowsPerPacket, panel.Members.Length - offset);
            await context.Reply(opcode, GuildPackets.Members(panel, offset, count, first, all),
                "guild_panel_members", query.IsCurrent);
        }
        // 窗口可能在登录初始化之后才创建；成员列表安装后再次恢复驻地控件缓存。
        npcs?.Post(context);
        log.Write("guild_panel", new { Connection = context.Id, Character = query.CharacterId,
            Guild = panel.Guild.Id, Count = panel.Members.Length, Reference = reference });
        return CommandResult.Handled;
    }

    async Task RankingAsync(GameConnectionContext context, Func<bool> guard)
    {
        // 侧栏构造只读已安装的当前名次；1506回调仅复制缓存，不会重建已经打开的侧栏。
        if (ranking is null || context.Session.CaptureGuildManagement(false) is not { } actor ||
            ranking.Summary(actor) is not { } summary) return;
        await context.Reply(CmdPacketType.GET_JOINED_GUILD_RANKING_INFO, GuildRankingPackets.Summary(summary),
            "guild_panel_ranking", () => guard() && actor.IsCurrent());
    }
}
