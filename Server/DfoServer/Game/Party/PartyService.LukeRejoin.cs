using DfoServer.Game.Raids;

namespace DfoServer.Game.Party;

internal sealed partial class PartyService
{
    // 只恢复仍存活的原普通分队；不分配新PartyId，不抢回已经继任的队长。
    internal PartySnapshot? RestoreLukeRaidMember(PartySnapshot expected, LukeRaidPartyIdentity original,
        RaidActor actor, byte slot, byte squad, Func<PartySnapshot, bool> install)
    {
        if (Capture(actor.Session) is not { } member) return null;
        lock (gate)
        {
            if (!IsCurrent(expected) || expected.Id != original.Id || !ReferenceEquals(expected.Commands, original.Commands) ||
                Find(member) is not null || !member.IsCurrent() || !actor.CanManage ||
                expected.Settings != RaidPreparationPlan.NativePartySettings(squad) || expected.Members.Length is < 1 or >= 4 ||
                expected.Members.Any(m => m.Slot == slot || !m.IsCurrent()) ||
                !original.Members.Any(m => m.Slot == slot && m.Character == actor.Character) ||
                expected.Members.Any(m => !original.Members.Any(old => old.Slot == m.Slot &&
                    old.Character == m.Actor.Character.Id))) return null;
            var restored = expected with { Members = [..expected.Members, member with { Slot = slot }], Revision = revision + 1 };
            if (!install(restored)) return null;
            revision++;
            actor.Session.AdvancePartyMembership();
            parties.Set(expected.Id, restored);
            return restored;
        }
    }
}
