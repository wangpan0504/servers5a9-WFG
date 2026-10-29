using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class PvpRecordHandler(PvpRecordQuery records, OnlineSessionDirectory online, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.FAIR_PVP_SCORE, ScoreAsync, nameof(PvpRecordHandler));

    async Task<CommandResult> ScoreAsync(GameConnectionContext context, Packet packet)
    {
        var actor = context.Session.CaptureSocialActor();
        bool valid = PvpRecordRequests.TryRead(packet.Body, out var request);
        bool self = actor?.Character.Id == request.Target;
        var target = valid && actor is not null && request.Self == (self ? 1 : 0)
            ? self ? actor : online.FindReadyCharacter(request.Target)?.CaptureSocialActor() : null;
        var snapshot = target is null ? null : records.Capture(target, request.Target, request.View, self, request.TeamMember);
        if (snapshot is null)
        {
            // 原生失败分支不读错误正文；不编造其它协议的错误码。
            await context.Reply(CmdPacketType.FAIR_PVP_SCORE, [0], "pvp_record_rejected");
            log.Write("pvp_record_rejected", new { Connection = context.Id, Valid = valid, Target = request.Target });
            return CommandResult.Handled;
        }
        bool Current() => actor!.IsCurrent() && target!.IsCurrent();
        using var publication = context.Sender.QueuePublication(Current);
        await context.Reply(CmdPacketType.FAIR_PVP_SCORE, PvpRecordPackets.Score(snapshot), "pvp_record", Current);
        return CommandResult.Handled;
    }
}
