using DfoServer.Game.Raids;

namespace DfoServer.Game.Party;

internal sealed partial class PartyService
{
    internal PartySnapshot? RestoreAntonRaidMember(PartySnapshot expected, AntonRaidRejoinReservation original,
        RaidActor actor, Func<PartySnapshot, bool> install)
    {
        if (Capture(actor.Session) is not { } member) return null;
        lock (gate)
        {
            if (!IsCurrent(expected) || expected.Id != original.Party || !ReferenceEquals(expected.Commands, original.Commands) ||
                Find(member) is not null || !member.IsCurrent() || !actor.CanManage || expected.Members.Length is < 1 or >= 4 ||
                expected.Members.Any(m => m.Slot == original.PartySlot || !m.IsCurrent()) ||
                expected.Members.Any(m => !original.Members.Any(old => old.Slot == m.Slot && old.Character == m.Actor.Character.Id))) return null;
            var restored = expected with { Members = [..expected.Members, member with { Slot = original.PartySlot }],
                Leader = original.Leader, Revision = revision + 1 };
            if (!restored.Members.Any(m => m.Slot == restored.Leader) || !install(restored)) return null;
            revision++;
            actor.Session.AdvancePartyMembership();
            parties.Set(expected.Id, restored);
            return restored;
        }
    }
}
