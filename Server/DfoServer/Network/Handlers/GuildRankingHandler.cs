using DfoServer.Game.Guild;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class GuildRankingHandler(GuildRankingService ranking) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.GET_JOINED_GUILD_RANKING_INFO, SummaryAsync, nameof(GuildRankingHandler));
        commands.Register(CmdPacketType.SEARCH_GUILD_RANKING, PageAsync, nameof(GuildRankingHandler));
    }

    async Task<CommandResult> SummaryAsync(GameConnectionContext context, Packet request)
    {
        if (context.Session.CaptureGuildManagement(false) is not { } actor) return CommandResult.Handled;
        var summary = GuildRankingRequests.TrySummary(request.Body, out _) ? ranking.Summary(actor) : null;
        await context.Reply(CmdPacketType.GET_JOINED_GUILD_RANKING_INFO,
            summary is null ? [0, 0, 0] : GuildRankingPackets.Summary(summary), "guild_ranking_summary", actor.IsCurrent);
        return CommandResult.Handled;
    }

    async Task<CommandResult> PageAsync(GameConnectionContext context, Packet request)
    {
        if (context.Session.CaptureGuildPanelQuery() is not { } query) return CommandResult.Handled;
        byte[] body = GuildRankingRequests.TryPage(request.Body, out byte mode, out byte sort, out ushort page)
            ? GuildRankingPackets.Page(ranking.Page(mode, sort, page)) : [0, 0, 0];
        await context.Reply(CmdPacketType.SEARCH_GUILD_RANKING, body, "guild_ranking_page", query.IsCurrent);
        return CommandResult.Handled;
    }
}
