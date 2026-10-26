using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class TagEvildomHandler(TagEvildomProjection projection, TownProjection town, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.TAG_EVILDOM_TEAM_SETTING, (context, packet) => ChangeAsync(context, packet, false), nameof(TagEvildomHandler));
        commands.Register(CmdPacketType.TAG_EVILDOM_TEAM_INIT, (context, packet) => ChangeAsync(context, packet, true), nameof(TagEvildomHandler));
    }

    async Task<CommandResult> ChangeAsync(GameConnectionContext context, Packet packet, bool reset)
    {
        TagEvildomSettingRequest? request = null;
        if (reset) TagEvildomRequests.Initialize(packet.Body);
        else request = TagEvildomRequests.Setting(packet.Body);
        var opcode = reset ? CmdPacketType.TAG_EVILDOM_TEAM_INIT : CmdPacketType.TAG_EVILDOM_TEAM_SETTING;
        Game.TagEvildom.TagEvildomCharacters? readiness = null;
        bool selectedNonempty = request is { SelectedTeam: >= 0 and < 3 } &&
            request.Members.Skip(request.SelectedTeam * 2).Take(2).Any(member => member.Server != 0 || member.RosterIndex != -1);
        bool needsInitialization = false;
        if (!context.BlocksTownActions && context.Session.ChannelEntry?.Type == 53 && selectedNonempty)
        {
            readiness = context.Session.CaptureTagEvildomSettingCharacters(projection.Store, request!.Members, request.SelectedTeam, projection.Now);
            needsInitialization = readiness is null || !projection.CharactersCurrent(readiness);
        }
        var result = context.BlocksTownActions || context.Session.ChannelEntry?.Type != 53
            ? (Projection: (Game.TagEvildom.TagEvildomProjection?)null, Reason: "town-actions-blocked")
            : needsInitialization ? (Projection: (Game.TagEvildom.TagEvildomProjection?)null, Reason: "selected-characters-not-ready")
            : context.Session.ConfigureTagEvildom(projection.Store, request?.Members, request?.SelectedTeam ?? 0, projection.Now, readiness);
        log.Write(result.Projection is null ? "tag_evildom_configuration_rejected" : "tag_evildom_configuration_committed",
            new { Connection = context.Id, Reset = reset, result.Reason, Revision = result.Projection?.State.Configuration.Revision });
        if (result.Projection is not { } snapshot)
        {
            // 两个原生失败回调不读75B；初始化先解除pending，再检查失败。提示走已有明确文本通道。
            await context.Reply(opcode, [0, 19, 0], "tag_evildom_configuration_rejected");
            await context.Notify(NotiPacketType.AAS_TIPS, ServerNoticePackets.Build("再战魔界", needsInitialization
                ? "编队未变更：选中两名角色的技能或穿戴期限资料需要更新。请先分别登录两名95级角色，再重新编队。"
                : "编队未变更：请重新选角后确认同账号的95级角色；当天已领取奖励的编队不可变更。"),
                "tag_evildom_configuration_notice");
            return CommandResult.Handled;
        }
        bool Guard() => context.Session.CanPublishTagEvildom(snapshot);
        await context.Reply(opcode, TagEvildomPackets.Accepted(snapshot), "tag_evildom_configuration_committed", Guard);
        // 非空成功与初始化由原生CMD7重选；active双Actor仅在新角色入城安装，避免重选前重复清/建。
        await projection.DeliverAsync(context, snapshot, activeCharacters: false);
        if (!reset && snapshot.State.Configuration.Teams[snapshot.State.Configuration.SelectedTeam].Empty)
            // 原生1662在选中空队时不自动发CMD7；沿既有真实小退流程结束旧active TAG实例，不伪造1393空身份。
            await town.ReturnToSelectionAsync(context, Guard);
        // 成功ACK由原生客户端决定是否发CMD7；服务器不假装已重选或启动战斗。
        return CommandResult.Handled;
    }
}
