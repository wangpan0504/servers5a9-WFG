namespace DfoServer.Game.Raids;

internal sealed partial class RaidRegistry
{
    internal bool UsesRejoinCandidates(AntonRaidRejoin candidates) => ReferenceEquals(rejoin, candidates);

    // 与RemoveCore/DropParty共用目录gate；不能在末位成员清理之后重新挂回旧候选。
    internal bool RecordLukeDisconnected(RaidParty expected, AntonRaidDisconnectFact fact, AntonRaidRejoin candidates)
    {
        lock (gate)
        {
            if (!UsesRejoinCandidates(candidates) || fact.Luke is not { } reservation ||
                !IsCurrent(expected) || expected.Type != 1 || expected.Key != fact.Key || expected.Channel != fact.Channel ||
                expected.Started?.InstanceId != fact.Instance || fact.OldOwner.IsCurrent ||
                expected.Members.Any(m => m.Actor.Character == fact.Character) ||
                !expected.Members.Any(m => m.Squad == fact.Squad && m.Actor.IsCurrent) ||
                !reservation.Battle.CanRestoreLukeRejoin(reservation)) return false;
            return candidates.Record(fact);
        }
    }

    internal RaidParty? RestoreLukeMember(RaidParty expected, AntonRaidDisconnectFact fact,
        RaidActor replacement, Func<bool> install)
    {
        lock (gate)
        {
            if (fact.Luke is null || !IsCurrent(expected) || expected.Type != 1 ||
                expected.Started?.InstanceId != fact.Instance || expected.Key != fact.Key || expected.Channel != fact.Channel ||
                replacement.Character != fact.Character || replacement.Owner.Account != fact.Account ||
                !replacement.IsCurrent || fact.OldOwner.IsCurrent || FindCore(replacement) is not null ||
                expected.Members.Length >= expected.Capacity || expected.Members.Any(m => m.Slot == fact.Slot ||
                    m.Actor.Character == fact.Character || m.Actor.Owner.Account == fact.Account)) return null;
            var restored = expected with { Members = expected.Members.Add(new(replacement, fact.Slot, fact.Squad)), Revision = revision + 1 };
            if (!install()) return null;
            revision++;
            parties[expected.Key] = restored;
            return restored;
        }
    }
}
