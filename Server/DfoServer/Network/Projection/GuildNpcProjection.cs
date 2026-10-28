using DfoServer.Game.Guild;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class GuildNpcProjection(GuildNpcService service)
{
    public void Post(GameConnectionContext context)
    {
        var actor = context.Session.CaptureGuildManagement(requireTown: false);
        if (actor is null || service.Read(actor) is not { } state) return;
        context.Sender.Post(0, (ushort)NotiPacketType.GUILD_NPC_INFO_ALL, GuildNpcPackets.All(state.Npcs),
            "guild_npc_info", actor.IsCurrent);
        foreach (var craft in state.Crafts)
            context.Sender.Post(0, (ushort)NotiPacketType.GUILD_NPC_BENEFIT_INFO, GuildNpcPackets.Benefit(craft),
                "guild_npc_benefit_info", actor.IsCurrent);
        context.Sender.Post(0, (ushort)NotiPacketType.GUILD_NPC_BENEFIT_INFO_ALL, GuildNpcPackets.BenefitsAll(state.Crafts),
            "guild_npc_benefit_info", actor.IsCurrent);
    }
}
