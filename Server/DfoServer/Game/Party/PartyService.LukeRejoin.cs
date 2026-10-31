using DfoServer.Game.Raids;

namespace DfoServer.Game.Party;

internal sealed partial class PartyService
{
    // 只恢复仍存活的原普通分队；不分配新PartyId，不抢回已经继任的队长。
    internal PartySnapshot? RestoreLukeRaidMember(PartySnapshot? expected, LukeRaidPartyIdentity original,
        RaidActor actor, byte slot, byte squad, Func<PartySnapshot, bool> install)
    {
        if (Capture(actor.Session) is not { } member) return null;
        lock (gate)
        {
            if (Find(member) is not null || !member.IsCurrent() || !actor.CanManage ||
                !original.Members.Any(m => m.Slot == slot && m.Character == actor.Character) ||
                expected is not null && (!IsCurrent(expected) || expected.Settings != RaidPreparationPlan.NativePartySettings(squad) ||
                expected.Members.Length is < 1 or >= 4 || expected.Members.Any(m => m.Slot == slot || !m.IsCurrent()))) return null;
            ushort id = expected?.Id ?? AllocateId();
            if (id == ushort.MaxValue) return null;
            var restored = expected is null ? new PartySnapshot(id, [member with { Slot = slot }], slot, revision + 1)
                { Settings = RaidPreparationPlan.NativePartySettings(squad) } :
                expected with { Members = [..expected.Members, member with { Slot = slot }], Revision = revision + 1 };
            if (!install(restored)) return null;
            revision++;
            actor.Session.AdvancePartyMembership();
            parties.Set(id, restored);
            return restored;
        }
    }
}
