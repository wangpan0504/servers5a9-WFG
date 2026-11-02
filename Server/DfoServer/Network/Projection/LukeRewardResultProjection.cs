using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 仅本连接本阶段的实际写出记账；不是领取资格，不保存第二份资产或在线角色。
internal sealed class LukeRewardResultProjection
{
    RaidCombatDisplayIdentity? identity;
    readonly HashSet<PreparedRaidReward> written = new(ReferenceEqualityComparer.Instance);
    RaidCombatDisplayIdentity? teamIdentity;
    byte[]? teamBody;
    bool personalEmptyWritten;
    RaidCombatDisplayIdentity? ineligibleIdentity;

    internal void PostIneligible(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view)
    {
        if (view.State != AntonRaidBattleState.Reward ||
            battle.LukeEligibility(actor.Character, view.Phase) is not { Eligible: false } eligibility) return;
        var expected = RaidCombatDisplayIdentity.Capture(actor, view);
        context.Sender.Post(0, (ushort)NotiPacketType.RAID_MESSAGE, LukeRewardPackets.Ineligible(eligibility.Reason),
            "luke_reward_ineligible", () => context.Session.CanPublishRaidBattle(actor, view) &&
                context.RaidDisplay == expected && ineligibleIdentity != expected,
            () => ineligibleIdentity = expected);
    }

    internal void PostTeam(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view, bool afterPersonal = false)
    {
        context.Owner.Execute(() =>
        {
            if (!context.Session.CanPublishRaidBattle(actor, view) ||
                battle.LukeEligibility(actor.Character, view.Phase) is not { Eligible: true }) return false;
            if (view.State is not (AntonRaidBattleState.Reward or AntonRaidBattleState.Completed) ||
                view.Phase == 0 && !afterPersonal) return false;
            var results = battle.LukeTeamEntitlements(view);
            if (results.IsDefault) return false;
            var expected = RaidCombatDisplayIdentity.Capture(actor, view) with { State = AntonRaidBattleState.Reward };
            if (teamIdentity != expected) { teamIdentity = expected; teamBody = null; personalEmptyWritten = false; }
            var body = LukeRewardPackets.TeamResults(results);
            bool Ready() => teamIdentity == expected && actor.IsCurrent && context.Session.CurrentRaidBattle(actor) == battle &&
                battle.View is { } latest && latest.Phase == view.Phase &&
                (latest.State is AntonRaidBattleState.Reward or AntonRaidBattleState.Completed ||
                    view.Phase == 0 && latest.State == AntonRaidBattleState.Intermission) &&
                !latest.ClockFaulted && !latest.PhaseExpired && context.RaidDisplay == expected &&
                (!afterPersonal || identity == expected && written.Any(reward => reward.Phase == view.Phase && reward.Kind == 1));
            // 第二页一次性装配，P2须在state4前预填完整权益；P1本人结果后发空页设置跳页标志。
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_REWARD_LIST, body, "luke_team_reward_entitlements",
                () => Ready() && (teamBody is null || !body.AsSpan().SequenceEqual(teamBody) || afterPersonal && !personalEmptyWritten),
                () => { teamBody = body; if (afterPersonal) personalEmptyWritten = true; });
            return true;
        });
    }

    internal bool Post(GameConnectionContext context, RaidRewardReceipt receipt, LukeRewardResult result)
    {
        var body = LukeRewardPackets.Result(result);
        return context.Owner.Execute(() =>
        {
            if (result.Owner != receipt.Prepared.Character || result.Kind != receipt.Prepared.Kind || result.Card != receipt.Prepared.Card ||
                result.Item != receipt.Offer.LukeResult || !result.AdditionalItems.SequenceEqual(receipt.Offer.LukeAdditionalResults) ||
                context.Session.LukeResultView(receipt) is not { } captured) return false;
            var expected = RaidCombatDisplayIdentity.Capture(captured.Actor, captured.View);
            if (identity != expected) { identity = expected; written.Clear(); }
            bool Current() => identity == expected && context.Session.LukeResultView(receipt) is { } current &&
                RaidCombatDisplayIdentity.Capture(current.Actor, current.View) == expected;
            bool DisplayReady() => Current() && context.RaidDisplay == expected;
            if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST, body, "luke_reward_result",
                () => DisplayReady() && !written.Contains(receipt.Prepared),
                () => { if (DisplayReady()) written.Add(receipt.Prepared); })) return true;
            if (result.Kind == 1 && captured.View.Phase == 0 &&
                captured.Actor.Session.CurrentRaidBattle(captured.Actor) is { } battle)
                PostTeam(context, captured.Actor, battle, captured.View, afterPersonal: true);
            context.Sender.TryPost(1, (ushort)CmdPacketType.SELECT_RAID_REWARD_CARD, [1], "luke_reward_selected",
                () => DisplayReady() && written.Contains(receipt.Prepared), afterWritten: () =>
                {
                    if (context.Owner.Execute(() => DisplayReady() && written.Contains(receipt.Prepared)))
                        context.Session.ConfirmRaidRewardPublished(receipt);
                });
            return true;
        });
    }
}
