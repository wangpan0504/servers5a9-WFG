namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    sealed class PreyDefenceArrival(PreyLandingCandidate landing, Action<Exception>? recipientFailure)
    {
        internal readonly PreyLandingCandidate Landing = landing;
        internal Action<Exception>? RecipientFailure = recipientFailure;
        internal int Publications, Attempts;
        internal bool Published, Entered, Resolved;
    }

    PreyDefenceArrival? preyDefenceArrival;

    // 同一次发布先登记全部入场尝试；异步SQL未结束前不把待入场小队判为漏守。
    IDisposable? BeginPreyDefencePublication(AntonRaidBattleView view, Action<Exception>? recipientFailure)
    {
        if (view.Phase != 0 || CapturePreyLanding(view, 5) is not { } landing) return null;
        if (preyDefenceArrival?.Landing != landing) preyDefenceArrival = new(landing, recipientFailure);
        if (preyDefenceArrival.Resolved) return null;
        preyDefenceArrival.RecipientFailure ??= recipientFailure;
        preyDefenceArrival.Published = true;
        preyDefenceArrival.Publications++;
        return new PreyDefenceAttempt(this, preyDefenceArrival, publication: true);
    }

    internal IDisposable? TrackPreyDefenceLanding(PreyLandingCandidate landing)
    {
        lock (gate)
        {
            if (landing.Arrival.Phase != 0 || landing.Side != 5 || !IsPreyLandingCurrent(landing)) return null;
            // 668直接投影可能早于时钟发布；先登记到站，仍由完整发布结束后裁定无人。
            if (preyDefenceArrival?.Landing != landing) preyDefenceArrival = new(landing, null);
            var arrival = preyDefenceArrival;
            if (arrival.Resolved) return null;
            arrival.Attempts++;
            return new PreyDefenceAttempt(this, arrival, publication: false);
        }
    }

    void RecordPreyDefenceEntry(RaidDungeonRun run)
    {
        if (run.Phase != 0 || CapturePreyLanding(current, 5) is not { } landing || run.Dungeon != landing.Dungeon) return;
        if (preyDefenceArrival?.Landing != landing) preyDefenceArrival = new(landing, null);
        // 正式安装就是成功接战；随后撤退已单独处罚，不能再按本次漏守重复扣血。
        preyDefenceArrival.Entered = true;
    }

    sealed class PreyDefenceAttempt(AntonRaidBattle battle, PreyDefenceArrival arrival, bool publication) : IDisposable
    {
        int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) battle.FinishPreyDefenceLanding(arrival, publication);
        }
    }

    void FinishPreyDefenceLanding(PreyDefenceArrival arrival, bool publication)
    {
        AntonRaidBattleView? changed = null;
        lock (gate)
        {
            if (publication) arrival.Publications--; else arrival.Attempts--;
            if (arrival.Resolved || !arrival.Published || arrival.Publications != 0 || arrival.Attempts != 0) return;
            arrival.Resolved = true;
            if (!IsPreyLandingCurrent(arrival.Landing) || arrival.Entered || dungeonRuns.Values.Any(run => run.Installed &&
                run.Phase == current.Phase && !run.Cleared && run.Active.Count != 0 &&
                run.Dungeon == arrival.Landing.Dungeon && !current.PreyExpelledRuns.Contains(run.Id))) return;
            var before = current;
            // 漏守只扣血，保留阻击；下圈新的到站generation会重新判定。
            var candidate = DamagePreyShip(before, PreyHeader("[defence fail]"));
            if (candidate.PhaseExpired) candidate = ExpelAll(candidate);
            CommitBattleCandidate(before, candidate);
            if (!ReferenceEquals(before, current)) changed = current;
        }
        if (changed is not null) Publish(changed, arrival.RecipientFailure);
    }
}
