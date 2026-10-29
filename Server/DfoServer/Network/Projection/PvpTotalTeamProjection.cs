using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Characters;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class PvpTotalTeamProjection(SkillLearningCatalog skills, CharacterStatComputer stats, ServerLog log)
{
    internal async Task PublishAsync(GameConnectionContext context, PvpTotalTeam team, PvpTotalRosterSelection selection)
    {
        // 完整演员读取不切换Selected，不保存其他队员资产；发送守卫只查已捕获的名册身份。
        var roster = context.Session.CapturePvpTotalBattleRoster(team, selection, skills);
        byte[]? actors = roster is null ? null : PvpTotalPackets.OwnActors(roster, stats.Compute);
        bool Current() => context.Session.CanPublishPvpTotalRoster(selection);
        using var publication = context.Sender.QueuePublication(Current);
        await context.Notify(NotiPacketType.PVP_TOTAL_MATCH_TEAM_INFO,
            PvpMatchingPackets.Team(team.Name, selection.Members.Select(row => row.RosterIndex).ToImmutableArray()),
            "pvp_total_team_updated", Current);
        // 024EA980检验team+8C的三个Player指针；804只写索引，803才调用015D2890填指针。
        if (actors is not null)
            await context.Notify(NotiPacketType.PVP_TOTAL_MATCH_CHARACTER_INFO, actors, "pvp_total_team_actors", Current);
        log.Write("pvp_total_team_projection", new { Connection = context.Id, Team = team.Id,
            team.Revision, Actors = roster?.Characters.Length ?? 0 });
    }
}
