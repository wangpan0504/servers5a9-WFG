using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class PvpChannelHandler(GameChannelListeners channels, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.PVP_CHANNEL_INFO, InfoAsync, nameof(PvpChannelHandler));

    async Task<CommandResult> InfoAsync(GameConnectionContext context, Packet packet)
    {
        SessionRequests.Empty(packet.Body);
        var actor = context.Session.CaptureSocialActor();
        var endpoints = channels.Endpoints;
        var available = endpoints.Where(row => row.Entry.Type is 4 or 5 or 8 or 24 && row.Entry.Group == "[pvp]").ToArray();
        bool accepted = actor is not null && available.Length != 0;
        if (accepted)
        {
            // 目录取实际监听快照，保留当前频道供客户端选择返回；不伪造未监听入口。
            var entries = endpoints.Where(row => available.Contains(row) || row.Entry == context.Session.ChannelEntry)
                .Select(row => row.Entry.Identity.Number).ToArray();
            using var publication = context.Sender.QueuePublication(actor!.IsCurrent);
            await context.Reply(CmdPacketType.PVP_CHANNEL_INFO, PvpChannelPackets.Available(entries),
                "pvp_channel_info", actor.IsCurrent);
        }
        else
            await context.Reply(CmdPacketType.PVP_CHANNEL_INFO, PvpChannelPackets.Unavailable(), "pvp_channel_unavailable");
        log.Write("pvp_channel_info", new { Connection = context.Id, Accepted = accepted, Channels = available.Length });
        return CommandResult.Handled;
    }
}
