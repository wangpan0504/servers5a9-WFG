using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class AdventurerBenefitProjection
{
    public static bool Post(GameConnectionContext context, bool clear = false, bool force = false)
    {
        var identity = context.Session.PlanReturningAdventurerIdentity(force);
        var progress = context.Session.PlanReturningAdventurerProgress(force);
        if (progress is not null)
        {
            context.Sender.Post(0, (ushort)NotiPacketType.RETURN_SYSTEM,
                AdventurerBenefitPackets.Returning(progress.State.Until, progress.State.Welcome, progress.State.Daily),
                "returning_adventurer_rewards", progress.IsCurrent);
            context.Sender.Post(0, (ushort)NotiPacketType.RETURN_SYSTEM_MISSION, AdventurerBenefitPackets.ReturningMissions(progress.State),
                "returning_adventurer_missions", progress.IsCurrent);
        }
        else if (identity is not null)
            context.Sender.Post(0, (ushort)NotiPacketType.RETURN_SYSTEM, AdventurerBenefitPackets.Returning(identity.Until),
                "returning_adventurer_identity", identity.IsCurrent);
        if (context.Session.PlanAdventurerBenefitBuffs(clear, force) is not { } receipt) return identity is not null;
        if (!receipt.Removed.IsEmpty)
            context.Sender.Post(0, (ushort)NotiPacketType.CHARACTER_DEL_BUFF,
                GuildEconomyPackets.RemovedBuffs(receipt.Removed.ToArray()), "adventurer_benefit_buff_removed", receipt.IsCurrent);
        if (!receipt.Added.IsEmpty)
            context.Sender.Post(0, (ushort)NotiPacketType.CHARACTER_ADD_BUFF,
                GuildEconomyPackets.AddedBuffs(receipt.Added.Select(id => new DfoServer.Game.Guild.GuildBenefit(Type: 0, Value: id, ExpiresUnix: 0)).ToArray(), 0),
                "adventurer_benefit_buff_added", receipt.IsCurrent);
        return identity is not null;
    }
}
