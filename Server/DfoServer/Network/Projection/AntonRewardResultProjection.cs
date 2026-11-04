using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 本连接实际写出的完整团队表；不持有库存、资格或领取状态，换连接自然重建。
internal sealed class AntonRewardResultProjection
{
    RaidCombatDisplayIdentity? writtenIdentity;

    internal bool HasTeam(RaidActor actor, AntonRaidBattleView view) =>
        writtenIdentity == Identity(actor, view);

    internal bool PostTeam(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view)
    {
        return context.Owner.Execute(() =>
        {
            if (!context.Session.CanPublishRaidBattle(actor, view) ||
                battle.AntonEligibility(actor.Character, view.Phase) is not { Eligible: true, Source: AntonResourceRewards.Source } ||
                battle.AntonTeamEntitlements(view) is not { IsDefaultOrEmpty: false } results) return false;
            var expected = Identity(actor, view);
            var body = AntonRewardPackets.TeamResults(results);
            bool Current() => actor.IsCurrent && context.Session.CurrentRaidBattle(actor) == battle &&
                battle.View is { ClockFaulted: false, PhaseExpired: false } latest && latest.Phase == view.Phase &&
                latest.State is AntonRaidBattleState.Reward or AntonRaidBattleState.Intermission or AntonRaidBattleState.Completed;
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_REWARD_LIST, body, "anton_team_reward_entitlements",
                () => Current() && writtenIdentity != expected,
                () => writtenIdentity = expected);
            return true;
        });
    }

    static RaidCombatDisplayIdentity Identity(RaidActor actor, AntonRaidBattleView view) =>
        RaidCombatDisplayIdentity.Capture(actor, view) with { State = AntonRaidBattleState.Reward };
}
