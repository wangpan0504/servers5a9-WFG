using DfoServer.Game.Raids;
using DfoServer.Game.Session;

namespace DfoServer.Game.Party;

internal sealed partial class PartyService
{
    // 调用方已取得本区域成员命令门禁与有序owner；不把招募Squad当作固定路线。
    internal (PartySnapshot Party, PartyNotice[] Notices)? GroupFiendwar(Guid battle, RaidActor[] actors)
    {
        if (actors.Length is < 1 or > 4 || actors.Select(actor => actor.Character).Distinct().Count() != actors.Length) return null;
        var captured = actors.Select(actor => Capture(actor.Session)).ToArray();
        if (captured.Any(member => member is null || !member.Actor.IsCurrent() ||
            member.Actor.Session.Phase != SessionPhase.Town || !member.Actor.Session.CanChangeTownParty)) return null;
        lock (gate)
        {
            var old = captured.Select(member => Find(member!)).OfType<PartySnapshot>().DistinctBy(party => party.Id).ToArray();
            if (old.Any(party => party.FiendwarBattleId != battle || party.Members.Any(member =>
                !actors.Any(actor => ReferenceEquals(actor.Session, member.Actor.Session))))) return null;
            // 同一批遭遇成员的重复入口不能因请求者不同而拆队重建；选图仍由原队长授权。
            if (old is [var existing] && existing.Members.Length == actors.Length)
                return (existing, []);
            if (AllocateId() == ushort.MaxValue) return null;
            var notices = new List<PartyNotice>();
            foreach (var previous in old) notices.AddRange(Remove(previous, previous.Members));
            var members = captured.Select((member, index) => member! with { Slot = checked((byte)index) }).ToArray();
            var created = new PartySnapshot(nextId, members, 0, ++revision)
            { FiendwarBattleId = battle, Settings = PartySettings.Default with { Capacity = 4, Dungeon = uint.MaxValue, Difficulty = 2 } };
            foreach (var member in members) member.Actor.Session.AdvancePartyMembership();
            parties.Set(created.Id, created);
            notices.AddRange(members.Select(member => Updated(created, member)));
            return (created, notices.ToArray());
        }
    }

    internal PartyNotice[] LeaveFiendwarArea(GameSession session, Guid battle)
    {
        lock (gate)
        {
            var party = parties.SnapshotValues().SingleOrDefault(party => party.FiendwarBattleId == battle &&
                party.Members.Any(member => ReferenceEquals(member.Actor.Session, session)));
            return party is null ? [] : Remove(party, party.Members.Where(member => ReferenceEquals(member.Actor.Session, session)).ToArray());
        }
    }
}
