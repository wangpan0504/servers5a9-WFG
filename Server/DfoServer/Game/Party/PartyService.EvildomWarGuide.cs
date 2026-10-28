using DfoServer.Game.Session;

namespace DfoServer.Game.Party;

internal sealed partial class PartyService
{
    PartyResult SetEvildomWarGuideInformation(GameSession session, PartyMember member, byte operation, PartySettings settings)
    {
        PartySnapshot? previous;
        PartySnapshot candidate;
        bool reserved = false;
        lock (gate)
        {
            if (!member.IsCurrent() || !session.CanChangeTownParty) return Reject(21);
            previous = Find(member);
            settings = settings with { Option = settings.Option ?? previous?.Settings.Option ?? PartySettings.Default.Option };
            if (previous is null)
            {
                if (operation != 0 || AllocateId() == ushort.MaxValue) return Reject(21);
                candidate = new(nextId, [member], 0, ++revision) { Settings = settings };
                reservedRaidSquads.Add(candidate.Id);
                reserved = true;
            }
            else
            {
                if (previous.Members.Length != 1 || previous.Members[0].Slot != previous.Leader) return Reject(21);
                candidate = previous with { Settings = settings, Revision = ++revision };
            }
        }
        try
        {
            // 本人owner保持不变；单人成员的变化也需该owner，SQL不占用全服Party gate。
            if (!session.TryBindEvildomWarGuide(guideStore!)) return Reject(21);
            lock (gate)
            {
                if (!member.IsCurrent() || !session.CanChangeTownParty || (previous is null ? Find(member) is not null :
                    !parties.TryGetValue(previous.Id, out var current) || !ReferenceEquals(current, previous)))
                    throw new InvalidOperationException("魔界引导绑定期间原队伍发生变化");
                if (previous?.Settings == settings) return new(0, []);
                if (previous is null) session.AdvancePartyMembership();
                parties.Set(candidate.Id, candidate);
                return new(0, candidate.Members.Select(member => Updated(candidate, member)).ToArray());
            }
        }
        finally { if (reserved) lock (gate) reservedRaidSquads.Remove(candidate.Id); }
    }
}
