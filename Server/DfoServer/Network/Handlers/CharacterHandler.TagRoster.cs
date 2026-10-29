using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class CharacterHandler
{
    async Task<CommandResult> AllServerRosterAsync(GameConnectionContext context, Packet request)
    {
        CharacterRequests.AllServerRoster(request.Body);
        var roster = context.Session.PublishedTagEvildomRoster();
        bool Guard() => roster is not null && !context.BlocksTownActions &&
            context.Session.ChannelEntry?.Type is 39 or 53 &&
            ReferenceEquals(context.Session.PublishedTagEvildomRoster(), roster);
        if (!Guard())
        {
            log.Write("tag_roster_unavailable", new { Connection = context.Id, Reason = "town-roster-unavailable" });
            return CommandResult.Handled;
        }
        // 本地区名单已由NOTI2安装；1408仅声明还需接收的其他大区，不能再把本区人数当作待收人数。
        // 原生0176B5F0在无待收角色时完成ready并重开300/299，不消费同号ACK1470。
        await context.Notify(NotiPacketType.TAG_TOURNAMENT_ALL_SERVER_CHARAC_COUNT,
            CharacterPackets.AllServerRosterCounts(), "tag_roster_regions", Guard);
        return CommandResult.Handled;
    }
}
