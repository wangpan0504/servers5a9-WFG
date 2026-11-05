using DfoServer.Game.Accounts;
using DfoServer.Game.Session;

namespace DfoServer.Game.Party;

internal sealed partial class PartyService
{
    // 调用者已提交城镇位置且持本人连接门禁；所有异步等待均在owner之外。
    internal async Task<PartyNotice[]> UpdatePreyBoardingAsync(GameSession session, CancellationToken stop = default)
    {
        session.RecordPreyBoardingArrival();
        var boarding = session.CapturePreyBoarding();
        if (boarding is null || Capture(session) is not { } member) return [];
        if (boarding.Area is not (4 or 5))
            return member.Actor.Execute(() =>
            {
                if (session.CapturePreyBoarding() != boarding) return [];
                lock (gate)
                {
                    var own = Find(member);
                    return own?.PreyBattleId == boarding.Battle.Instance ? Remove(own, own.Members
                        .Where(current => ReferenceEquals(current.Actor.Session, session) && current.Actor.Epoch == member.Actor.Epoch).ToArray()) : [];
                }
            });

        // 已在实际队伍中的人不等待成队门禁，避免持旧队Commands反向等待新队。
        lock (gate) if (Find(member) is not null) return [];
        await boarding.Battle.PreyBoardingCommands.WaitAsync(stop);
        List<PartyNotice> notices = [];
        try
        {
            while (true)
            {
                stop.ThrowIfCancellationRequested();
                if (!member.IsCurrent() || session.CapturePreyBoarding() != boarding) return notices.ToArray();
                PartySnapshot? target;
                lock (gate)
                {
                    if (Find(member) is not null) return notices.ToArray();
                    target = FindPreyBoardingParty(boarding);
                }
                var waiting = PreyBoardingWaiters(boarding);
                if (waiting.Length < (target is null ? 2 : 1)) return notices.ToArray();
                var additions = waiting.Take(target is null ? 4 : 4 - target.Members.Length).ToArray();
                if (target is not null) await target.Commands.WaitAsync(stop);
                try
                {
                    var all = (target?.Members ?? []).Concat(additions.Select(row => row.Member)).ToArray();
                    var changed = AccountOwners.Lease.ExecuteGroup<PartyNotice[]?>(all
                        .Select(current => (current.Actor.Owner, current.Actor.Character.Id)).ToArray(), () =>
                    {
                        if (additions.Any(row => !row.Member.IsCurrent() ||
                            row.Member.Actor.Session.CapturePreyBoarding() != row.State)) return null;
                        if (target is not null && target.Members.Any(current => current.Actor.Session.CapturePreyBoarding() is not { } position ||
                            position.Battle != boarding.Battle || position.Area != boarding.Area)) return null;
                        lock (gate)
                        {
                            if (additions.Any(row => Find(row.Member) is not null)) return null;
                            PartySnapshot party;
                            if (target is null)
                            {
                                if (FindPreyBoardingParty(boarding) is not null) return null;
                                if (AllocateId() == ushort.MaxValue) return [];
                                party = new PartySnapshot(nextId, all.Select((current, slot) =>
                                    current with { Slot = checked((byte)slot) }).ToArray(), 0, ++revision)
                                {
                                    PreyBattleId = boarding.Battle.Instance, PreyBoardingArea = boarding.Area,
                                    PreyBoardingOrder = additions[0].State.Order,
                                    Settings = PartySettings.Default with { Capacity = 4, Dungeon = uint.MaxValue, Difficulty = 2 }
                                };
                            }
                            else
                            {
                                if (!IsCurrent(target) || !PreyBoardingPartyReady(target, boarding)) return null;
                                party = target with { Members = all.Select((current, slot) =>
                                    current with { Slot = checked((byte)slot) }).ToArray(), Revision = ++revision };
                            }
                            foreach (var row in additions) row.Member.Actor.Session.AdvancePartyMembership();
                            parties.Set(party.Id, party);
                            return party.Members.Select(current => Updated(party, current)).ToArray();
                        }
                    });
                    if (changed is { Length: 0 }) return notices.ToArray();
                    if (changed is not null) notices.AddRange(changed);
                }
                catch (InvalidOperationException) when (!member.Actor.IsCurrent() ||
                    additions.Any(row => !row.Member.Actor.IsCurrent()) ||
                    target?.Members.Any(current => !current.Actor.IsCurrent()) == true)
                {
                    if (!member.Actor.IsCurrent()) return notices.ToArray();
                }
                finally { target?.Commands.Release(); }
            }
        }
        finally { boarding.Battle.PreyBoardingCommands.Release(); }
    }

    (PartyMember Member, PreyBoardingState State)[] PreyBoardingWaiters(PreyBoardingState boarding)
    {
        List<(PartyMember Member, PreyBoardingState State)> waiting = [];
        foreach (var actor in boarding.Actor.Session.CapturePreyBoardingMembers(boarding.Battle.Instance))
        {
            if (!actor.IsCurrent) continue;
            try
            {
                var state = actor.Session.CapturePreyBoarding();
                if (state is not { Order: > 0 } || state.Battle != boarding.Battle || state.Area != boarding.Area ||
                    Capture(actor.Session) is not { } candidate) continue;
                lock (gate) if (Find(candidate) is not null) continue;
                waiting.Add((candidate, state));
            }
            catch (InvalidOperationException) when (!actor.IsCurrent) { }
        }
        return waiting.OrderBy(row => row.State.Order).ToArray();
    }

    PartySnapshot? FindPreyBoardingParty(PreyBoardingState boarding) => parties.SnapshotValues()
        .Where(party => PreyBoardingPartyReady(party, boarding)).OrderBy(party => party.PreyBoardingOrder).FirstOrDefault();

    static bool PreyBoardingPartyReady(PartySnapshot party, PreyBoardingState boarding) =>
        party.PreyBattleId == boarding.Battle.Instance && party.PreyBoardingArea == boarding.Area && party.Members.Length is >= 2 and < 4 &&
        party.Members.All(member => member.IsCurrent() && member.Identity.Channel == boarding.Actor.Channel &&
            member.Actor.Session.CanChangeTownParty && member.Actor.Session.PartyDungeon is null &&
            member.Actor.Session.Selected?.Position is { Town: 59 } position && position.Area == boarding.Area);
}
