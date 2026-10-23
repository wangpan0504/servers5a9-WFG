using DfoServer.Game.Adventure;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

// 每周地下城信息页：789回718名册（非Off模式再回整份719），790按请求名回719，791保存客户端提交的5槽位排列。
// 719次数来自角色本周实际入场计数；5号列没有服务端来源，客户端显示"-"。
internal sealed class AdventureWeeklyHandler(AdventureWeeklyProjection? weekly = null) : IGameCommandModule
{
    AdventureWeeklyMode Mode => weekly?.Mode ?? AdventureWeeklyMode.Off;

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.REQUEST_SERVER_CHARACTER_LIST, ServerCharacters, nameof(AdventureWeeklyHandler));
        commands.Register(CmdPacketType.SET_WEEKLY_DUNGEON_INFO_CONFIG, WeeklyConfig, nameof(AdventureWeeklyHandler));
        commands.Register(CmdPacketType.REQUEST_WEEKLY_DUNGEON_INFO, WeeklyInfoRequest, nameof(AdventureWeeklyHandler));
    }

    async Task<CommandResult> ServerCharacters(GameConnectionContext context, Packet packet)
    {
        byte server = AdventureRequests.ServerCharacters(packet.Body);
        var selected = context.Session.Selected;
        long epoch = context.Session.SelectionEpoch;
        if (selected is null && !context.Session.CheckState(UserState.CharacterSelect)) return CommandResult.Handled;
        bool Guard() => context.Session.SelectionEpoch == epoch && ReferenceEquals(context.Session.Selected, selected) &&
            (selected is null ? context.Session.CheckState(UserState.CharacterSelect) : context.Session.Ready);
        var rows = context.Session.ReadAdventureWeeklyRoster();
        await context.Notify(NotiPacketType.SERVER_CHARACTER_LIST, AdventurePackets.ServerCharacters(server, rows),
            "adventure_server_characters", Guard);
        // 718只负责名册；719按客户端请求的服务器序号整份下发，打开面板后客户端仍会按可见行逐名请求790。
        if (Mode != AdventureWeeklyMode.Off)
            await context.Notify(NotiPacketType.WEEKLY_DUNGEON_INFO, AdventurePackets.WeeklyDungeonInfo(server, rows),
                "adventure_weekly_info", Guard);
        return CommandResult.Handled;
    }

    // 790：客户端对可见行与当前在线角色逐名请求；只回请求里点名的角色，未知名字跳过。
    async Task<CommandResult> WeeklyInfoRequest(GameConnectionContext context, Packet packet)
    {
        var request = AdventureRequests.WeeklyInfo(packet.Body);
        if (Mode == AdventureWeeklyMode.Off) return CommandResult.Handled;
        var selected = context.Session.Selected;
        long epoch = context.Session.SelectionEpoch;
        bool Guard() => context.Session.SelectionEpoch == epoch && ReferenceEquals(context.Session.Selected, selected) &&
            (selected is null ? context.Session.CheckState(UserState.CharacterSelect) : context.Session.Ready);
        var rows = context.Session.ReadAdventureWeeklyRoster();
        await context.Notify(NotiPacketType.WEEKLY_DUNGEON_INFO, AdventurePackets.WeeklyDungeonInfo(request.Key, rows, request.Names),
            "adventure_weekly_info_reply", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> WeeklyConfig(GameConnectionContext context, Packet packet)
    {
        var slots = AdventureRequests.WeeklyConfig(packet.Body);
        var result = context.Session.SaveAdventureWeeklyConfig(slots);
        await context.Reply(CmdPacketType.SET_WEEKLY_DUNGEON_INFO_CONFIG, AdventurePackets.Ack(result.Error), "adventure_weekly_config",
            result.Accepted ? () => context.Session.CanPublishAdventureWeekly(result) : null);
        if (result.Accepted && weekly is not null) await weekly.DeliverAsync(context);
        return CommandResult.Handled;
    }
}
