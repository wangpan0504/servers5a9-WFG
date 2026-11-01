using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class RaidHandler
{
    async Task<CommandResult> PvpRecordAsync(GameConnectionContext context, RaidActor actor, RaidOtherUserRequest request)
    {
        var target = online?.FindReadyCharacter(request.Character)?.CaptureSocialActor();
        var channel = target?.Session.ChannelEntry?.Identity;
        bool Located() => target is not null && target.IsCurrent() && channel is { } destination &&
            destination.ServerIndex == request.Server && destination.Number == request.Channel &&
            target.Session.ChannelEntry?.Identity == destination;
        // 先释放请求人的owner再捕获目标；不跨账号嵌套取锁，也不把房间胜数伪造成排位记录。
        var snapshot = Located() ? records?.Capture(target!, request.Character, checked((byte)(request.RawView + 1)), false, -1) : null;
        if (!Located()) snapshot = null;
        bool Current() => actor.IsCurrent && context.Session.ChannelEntry?.Identity == actor.Channel;
        bool Publish() => Current() && Located();
        await context.Notify(NotiPacketType.RAID_OTHER_CHANNEL_USER_PVP_SCORE,
            RaidPvpRecordPackets.Build(request.Server, request.Character, request.Channel, request.RawView, request.RequestId, snapshot),
            snapshot is null ? "raid_pvp_record_unavailable" : "raid_pvp_record", snapshot is null ? Current : Publish);
        log.Write("raid_pvp_record", new { Connection = context.Id, request.Character, request.Server, request.Channel,
            request.RawView, request.RequestId, Found = snapshot is not null });
        return CommandResult.Handled;
    }
}
