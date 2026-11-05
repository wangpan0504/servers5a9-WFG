namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    bool HasPreyMovieBarrierLocked() => Kind == RaidKind.Prey && !PreyGuide && current.Phase == 1 &&
        lukePhaseCommits.ContainsKey(1);

    bool PreyMovieRewardsRequestedLocked() => HasPreyMovieBarrierLocked() && lukeSettlements.TryGetValue(1, out var settlement) &&
        (settlement.Members.Any(member => member.Present && lukeMovieCompleted.Contains((1, member.Character))) ||
            settlement.Members.Where(member => member.Present).All(member =>
                lukeMovieCompleted.Contains((1, member.Character)) || lukeMovieDeparted.Contains((1, member.Character))));

    internal bool CanConfirmPreyMovie(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate) return HasPreyMovieBarrierLocked() && actor.IsCurrent && members.ContainsKey(actor) &&
            ReferenceEquals(current, view) && view.State == AntonRaidBattleState.Reward &&
            !view.ClockFaulted && !view.PhaseExpired && lukeSettlements.TryGetValue(1, out var settlement) &&
            settlement.Members.Any(member => member.Character == actor.Character && member.Present) &&
            !lukeMovieDeparted.Contains((1, actor.Character));
    }

    // 首个真实659=1唤醒原时钟交付已冻结的全团权益；各自窗口与终局仍等待原演出确认。
    internal bool CompletePreyMovie(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (!CanConfirmPreyMovie(actor, view) || !lukeMovieCompleted.Add((1, actor.Character))) return false;
            ScheduleLukeClockLocked();
            return true;
        }
    }

    internal bool PreyMovieCardsReady(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate) return HasPreyMovieBarrierLocked() && actor.IsCurrent && members.ContainsKey(actor) &&
            ReferenceEquals(current, view) && (view.State is AntonRaidBattleState.Reward or AntonRaidBattleState.Completed) &&
            !view.ClockFaulted && !view.PhaseExpired && lukeSettlements.TryGetValue(1, out var settlement) &&
            settlement.Members.Any(member => member.Character == actor.Character && member.Present) &&
            !lukeMovieDeparted.Contains((1, actor.Character)) && lukeMovieCompleted.Contains((1, actor.Character));
    }

    // 首次显示会锁存原生团队结果；所有权益提交齐全后才返回完整kind0表，null表示尚未就绪。
    internal (ushort Owner, RaidRewardOffer Offer)[]? PreyTeamCommittedRewards(RaidActor actor, AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (!HasPreyTeamAutomaticRewards || !actor.IsCurrent || !members.ContainsKey(actor) ||
                !ReferenceEquals(current, view) || view.Phase != 1 ||
                view.State is not (AntonRaidBattleState.Reward or AntonRaidBattleState.Completed) ||
                !lukePhaseCommits.TryGetValue(1, out var committed)) return null;
            var rows = new List<(ushort Owner, RaidRewardOffer Offer)>();
            foreach (var member in committed.Members.Where(row => row.Eligible).OrderBy(row => row.Character))
            {
                if (member.Source != PreyGuideRewards.TeamSource || member.Instance != Instance || member.RaidType != RaidKind.Prey ||
                    member.Phase != 1) return null;
                foreach (var offer in member.Offers)
                {
                    if (!lukeSubmittedRewards.TryGetValue((1, member.Character, offer.Kind), out var prepared) ||
                        prepared.Instance != Instance || prepared.RaidType != RaidKind.Prey || prepared.Phase != 1 ||
                        prepared.Character != member.Character || prepared.Source != member.Source || prepared.Week != member.Week ||
                        prepared.Kind != offer.Kind || prepared.Card != offer.Card || !offer.Grants.SequenceEqual(prepared.Grants)) return null;
                    if (offer.Kind == 0) rows.Add((prepared.Character, offer));
                }
            }
            return rows.ToArray();
        }
    }
}
