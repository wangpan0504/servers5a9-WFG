using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class AdventurerBenefitProjection
{
    public static void Post(GameConnectionContext context, bool clear = false, bool force = false)
    {
        if (context.Session.PlanReturningAdventurerIdentity(force) is { } identity)
            context.Sender.Post(0, (ushort)NotiPacketType.RETURN_SYSTEM, AdventurerBenefitPackets.Returning(identity.Until),
                "returning_adventurer_identity", identity.IsCurrent);
        if (context.Session.PlanAdventurerBenefitBuffs(clear, force) is not { } receipt) return;
        if (!receipt.Removed.IsEmpty)
            context.Sender.Post(0, (ushort)NotiPacketType.CHARACTER_DEL_BUFF,
                GuildEconomyPackets.RemovedBuffs(receipt.Removed.ToArray()), "adventurer_benefit_buff_removed", receipt.IsCurrent);
        if (!receipt.Added.IsEmpty)
            context.Sender.Post(0, (ushort)NotiPacketType.CHARACTER_ADD_BUFF,
                GuildEconomyPackets.AddedBuffs(receipt.Added.Select(id => new DfoServer.Game.Guild.GuildBenefit(Type: 0, Value: id, ExpiresUnix: 0)).ToArray(), 0),
                "adventurer_benefit_buff_added", receipt.IsCurrent);
    }
}
