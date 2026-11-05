using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class ClientNoticeHandler(ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        foreach (var opcode in new[]
        {
            CmdPacketType.USE_SKILL,
            CmdPacketType.SECURITY_STATUS,
            CmdPacketType.FPS_PERFORMANCE_STATISTIC
        })
            commands.Register(opcode, NoticeAsync, nameof(ClientNoticeHandler));
    }

    async Task<CommandResult> NoticeAsync(GameConnectionContext context, Packet request)
    {
        if (!ClientNotices.TryParse(request.Opcode, request.Body, out var notice)) return CommandResult.Unhandled;
        log.Write("client_notice", new { Connection = context.Id, request.Opcode, notice.Action, notice.Detail });
        if (request.Opcode == (ushort)CmdPacketType.USE_SKILL &&
            DfoServer.Network.Parsers.SkillRequests.TryParseUse(request.Body, out var use) &&
            use is { Unknown1: DfoServer.Game.BattlePass.BattlePassQuickReboundSource.SkillId } &&
            context.Session.ObserveQuickReboundSkill(new(use.Unknown0, use.Unknown1, use.Unknown8, use.ChecksumMatches)) is
                { Accepted: true, State: { } state, Receipt: { } receipt })
        {
            await DfoServer.Network.Projection.BattlePassMissionProjection.MissionsAsync(context, state, receipt);
        }
        return CommandResult.Handled;
    }
}
