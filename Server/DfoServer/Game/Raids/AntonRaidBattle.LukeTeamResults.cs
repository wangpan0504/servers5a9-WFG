using System.Collections.Immutable;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 仅供本Battle自动收尾精确重放已提交资产，不保存另一份资格或重新规划随机结果。
    readonly Dictionary<(int Phase, ushort Character, byte Kind), PreparedRaidReward> lukeSubmittedRewards = [];

    internal void RecordLukeRewardReceipt(RaidActor actor, RaidRewardReceipt receipt)
    {
        lock (gate)
        {
            var prepared = receipt.Prepared;
            if (Kind is not (RaidKind.Luke or RaidKind.Fiendwar) && !HasPreyTeamAutomaticRewards ||
                Kind == RaidKind.Prey && prepared.Source != PreyGuideRewards.TeamSource ||
                !actor.IsCurrent || !members.ContainsKey(actor) ||
                prepared.Instance != Instance || prepared.RaidType != Kind || prepared.Phase != current.Phase ||
                current.State != AntonRaidBattleState.Reward || prepared.Character != actor.Character ||
                LukeEligibility(actor.Character, current.Phase) is not { Eligible: true } eligibility ||
                prepared.Source != eligibility.Source || prepared.Week != eligibility.Week ||
                !prepared.Grants.SequenceEqual(receipt.Offer.Grants) ||
                eligibility.Offers.SingleOrDefault(offer => offer.Kind == prepared.Kind &&
                    offer.Card == (SelectableRewardCards ? 0 : prepared.Card)) is not { } frozen ||
                !SameLukeOffers([frozen with { Card = prepared.Card }], [receipt.Offer])) return;
            lukeSubmittedRewards.TryAdd((prepared.Phase, prepared.Character, prepared.Kind), prepared);
        }
    }

    internal PreparedRaidReward? SubmittedLukeReward(ushort character, int phase, byte kind)
    { lock (gate) return lukeSubmittedRewards.GetValueOrDefault((phase, character, kind)); }

    // 第二页展示阶段资格事务已经密封的精确团队权益；显示不等于660资产领取，不另抽奖励。
    internal ImmutableArray<LukeRewardResult> LukeTeamEntitlements(AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Luke || !ReferenceEquals(current, view) ||
                !lukePhaseCommits.TryGetValue(view.Phase, out var committed)) return default;
            var rows = ImmutableArray.CreateBuilder<LukeRewardResult>();
            foreach (var member in committed.Members.Where(row => row.Eligible).OrderBy(row => row.Character))
            {
                var offers = member.Offers.Where(offer => offer.Kind == 0).ToArray();
                if (offers.Length == 0) continue;
                if (offers is not [var offer] || offer.LukeResult is not { } spec || !spec.Matches(offer)) return default;
                rows.Add(new(0, member.Character, offer.Card, spec) { AdditionalItems = offer.LukeAdditionalResults });
            }
            return rows.ToImmutable();
        }
    }
}
