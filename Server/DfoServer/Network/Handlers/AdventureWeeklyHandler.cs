using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

// 每周地下城信息页：789只读名册并回718，791保存客户端提交的5槽位排列。
// 790/719/720/721尚未接入；客户端未发送790前不伪造周次数或配置回显。
internal sealed class AdventureWeeklyHandler : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.REQUEST_SERVER_CHARACTER_LIST, ServerCharacters, nameof(AdventureWeeklyHandler));
        commands.Register(CmdPacketType.SET_WEEKLY_DUNGEON_INFO_CONFIG, WeeklyConfig, nameof(AdventureWeeklyHandler));
    }

    async Task<CommandResult> ServerCharacters(GameConnectionContext context, Packet packet)
    {
        byte server = AdventureRequests.ServerCharacters(packet.Body);
        var selected = context.Session.Selected;
        long epoch = context.Session.SelectionEpoch;
        if (selected is null && !context.Session.CheckState(UserState.CharacterSelect)) return CommandResult.Handled;
        var rows = context.Session.ReadAdventureCharacterList();
        bool Guard() => context.Session.SelectionEpoch == epoch && ReferenceEquals(context.Session.Selected, selected) &&
            (selected is null ? context.Session.CheckState(UserState.CharacterSelect) : context.Session.Ready);
        await context.Notify(NotiPacketType.SERVER_CHARACTER_LIST, AdventurePackets.ServerCharacters(server, rows),
            "adventure_server_characters", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> WeeklyConfig(GameConnectionContext context, Packet packet)
    {
        var slots = AdventureRequests.WeeklyConfig(packet.Body);
        var result = context.Session.SaveAdventureWeeklyConfig(slots);
        await context.Reply(CmdPacketType.SET_WEEKLY_DUNGEON_INFO_CONFIG, AdventurePackets.Ack(result.Error), "adventure_weekly_config",
            result.Accepted ? () => context.Session.CanPublishAdventureWeekly(result) : null);
        return CommandResult.Handled;
    }
}
