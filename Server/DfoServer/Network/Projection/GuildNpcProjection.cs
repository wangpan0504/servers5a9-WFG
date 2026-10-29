using DfoServer.Game.Guild;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class GuildNpcProjection(GuildNpcService service, GuildNpcBenefitService? benefits = null)
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
        var purchased = benefits?.Read(actor);
        context.Sender.Post(0, (ushort)NotiPacketType.GUILD_NPC_BENEFIT_INFO_ALL,
            purchased is null ? GuildNpcPackets.BenefitsAll(state.Crafts) : GuildNpcPackets.BenefitsAll(state.Crafts, purchased),
            "guild_npc_benefit_info", actor.IsCurrent);
        if (purchased is not null)
            context.Sender.Post(0, (ushort)NotiPacketType.GUILD_TRAINER_BUFF_INFO,
                GuildNpcPackets.Training(purchased, benefits!.Catalog,
                    state.Npcs.Single(row => row.NpcId == 400001064).Level), "guild_training_info", actor.IsCurrent);
    }
}
