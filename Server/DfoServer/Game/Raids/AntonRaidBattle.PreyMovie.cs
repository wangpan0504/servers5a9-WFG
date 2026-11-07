namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 引导两阶段均有独立演出；普通一阶段仍沿退场后直接交付的原合同。
    bool HasPreyMovieBarrierLocked() => HasPreyAutomaticRewards && (PreyGuide || current.Phase == 1) &&
        lukePhaseCommits.ContainsKey(current.Phase);

    bool PreyMovieRewardsRequestedLocked() => HasPreyMovieBarrierLocked() && lukeSettlements.TryGetValue(current.Phase, out var settlement) &&
        (settlement.Members.Any(member => member.Present && lukeMovieCompleted.Contains((current.Phase, member.Character))) ||
            settlement.Members.Where(member => member.Present).All(member =>
                lukeMovieCompleted.Contains((current.Phase, member.Character)) || lukeMovieDeparted.Contains((current.Phase, member.Character))));

    internal bool CanConfirmPreyMovie(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate) return HasPreyMovieBarrierLocked() && actor.IsCurrent && members.ContainsKey(actor) &&
            ReferenceEquals(current, view) && view.State == AntonRaidBattleState.Reward &&
            !view.ClockFaulted && !view.PhaseExpired && lukeSettlements.TryGetValue(view.Phase, out var settlement) &&
            settlement.Members.Any(member => member.Character == actor.Character && member.Present) &&
            !lukeMovieDeparted.Contains((view.Phase, actor.Character));
    }

    // 首个真实659=1唤醒原时钟交付已冻结的全团权益；各自窗口与终局仍等待原演出确认。
    internal bool CompletePreyMovie(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (!CanConfirmPreyMovie(actor, view) || !lukeMovieCompleted.Add((view.Phase, actor.Character))) return false;
            ScheduleLukeClockLocked();
            return true;
        }
    }

    internal bool PreyMovieCardsReady(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate) return HasPreyMovieBarrierLocked() && actor.IsCurrent && members.ContainsKey(actor) &&
            ReferenceEquals(current, view) && (view.State is AntonRaidBattleState.Reward or AntonRaidBattleState.Completed) &&
            !view.ClockFaulted && !view.PhaseExpired && lukeSettlements.TryGetValue(view.Phase, out var settlement) &&
            settlement.Members.Any(member => member.Character == actor.Character && member.Present) &&
            !lukeMovieDeparted.Contains((view.Phase, actor.Character)) && lukeMovieCompleted.Contains((view.Phase, actor.Character));
    }

    // 首次显示会锁存原生团队结果；所有权益提交齐全后才返回完整kind0表，null表示尚未就绪。
    internal (ushort Owner, RaidRewardOffer Offer)[]? PreyTeamCommittedRewards(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (!HasPreyAutomaticRewards || !actor.IsCurrent || !members.ContainsKey(actor) ||
                !ReferenceEquals(current, view) ||
                view.State is not (AntonRaidBattleState.Reward or AntonRaidBattleState.Completed) ||
                !lukePhaseCommits.TryGetValue(view.Phase, out var committed)) return null;
            var rows = new List<(ushort Owner, RaidRewardOffer Offer)>();
            foreach (var member in committed.Members.Where(row => row.Eligible).OrderBy(row => row.Character))
            {
                if (!MatchesPreyRewardSource(member.Source) || member.Instance != Instance || member.RaidType != RaidKind.Prey ||
                    member.Phase != view.Phase) return null;
                foreach (var offer in member.Offers)
                {
                    if (!lukeSubmittedRewards.TryGetValue((view.Phase, member.Character, offer.Kind), out var prepared) ||
                        prepared.Instance != Instance || prepared.RaidType != RaidKind.Prey || prepared.Phase != view.Phase ||
                        prepared.Character != member.Character || prepared.Source != member.Source || prepared.Week != member.Week ||
                        prepared.Kind != offer.Kind || prepared.Card != offer.Card || !offer.Grants.SequenceEqual(prepared.Grants)) return null;
                    if (offer.Kind == 0) rows.Add((prepared.Character, offer));
                }
            }
            return rows.ToArray();
        }
    }
}
