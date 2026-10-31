using System.Collections.Immutable;
using DfoServer.Game.Party;

namespace DfoServer.Game.Raids;

// 预约仅保存原身份与聚合引用，不保存离线角色资产。
internal sealed record LukeRaidPartyIdentity(ushort Id, SemaphoreSlim Commands,
    ImmutableArray<(ushort Character, byte Slot)> Members);
internal sealed class LukeRaidRejoinReservation(RaidActor original, AntonRaidBattle battle,
    PartySnapshot party, byte partySlot, int phase)
{
    internal RaidActor Original { get; } = original;
    internal AntonRaidBattle Battle { get; } = battle;
    internal LukeRaidPartyIdentity OriginalParty { get; } = new(party.Id, party.Commands,
        party.Members.Select(member => (member.Actor.Character.Id, member.Slot)).ToImmutableArray());
    internal byte PartySlot { get; } = partySlot;
    internal int Phase { get; } = phase;
    internal RaidActor? Bound { get; set; }
}
internal sealed record LukeRaidDisconnectCandidate(Guid Instance, uint Key, byte RaidSlot, byte Squad,
    ushort PartyId, byte PartySlot, int Phase, bool OldOwnerReleased);
internal sealed record LukeRaidRejoinResult(bool Accepted, string Reason, RaidParty? Raid = null,
    PartySnapshot? Party = null, AntonRaidBattleView? Battle = null);

// 统一回候战恢复；不把新会话塞回旧房间，不保存未完成Boss的战斗状态。
internal sealed class LukeRaidRejoin(RaidRegistry registry, PartyService parties, AntonRaidRejoin candidates)
{
    internal LukeRaidDisconnectCandidate? DescribeCandidate(ushort character) => candidates.DescribeLukeCandidate(character);
    internal AntonRaidDisconnectFact? TryCaptureForDisconnect(DfoServer.Game.Session.GameSession session, out string reason)
    {
        var actor = session.ReadLukeDisconnectActorHint();
        var raid = actor is null ? null : registry.Find(actor);
        if (actor is null || raid is null) { reason = "no-active-luke"; return null; }
        if (!Accounts.AccountOwners.Lease.TryExecuteLukeDisconnect(raid.Members.Select(m => (m.Actor.Owner, m.Actor.Character)).ToArray(),
            () => CaptureLocked(actor, raid), out AntonRaidDisconnectFact? fact))
        { reason = "owner-busy-or-stale"; return null; }
        reason = fact is null ? "ineligible" : "captured";
        return fact;
    }
    internal AntonRaidDisconnectFact? Capture(RaidActor actor)
    {
        var raid = registry.Find(actor);
        return raid is null ? null : RaidOwnerGroup.Execute<AntonRaidDisconnectFact?>(RaidKind.Luke,
            raid.Members.Select(m => (m.Actor.Owner, m.Actor.Character)).ToArray(), () => CaptureLocked(actor, raid));
    }
    AntonRaidDisconnectFact? CaptureLocked(RaidActor actor, RaidParty expected)
    {
        if (!registry.UsesRejoinCandidates(candidates) || !candidates.LukeCandidatesEnabled || !actor.IsCurrent ||
            !registry.IsCurrent(expected) || expected is not { Type: 1, Started: { } started } ||
            actor.Session.CurrentRaidBattle(actor) is not { Kind: RaidKind.Luke } battle || started.InstanceId != battle.Instance ||
            parties.Current(actor.Session) is not { } party ||
            party.Members.SingleOrDefault(m => ReferenceEquals(m.Actor.Session, actor.Session)) is not { } own ||
            !battle.CanCaptureLukeRejoin(actor)) return null;
        var member = expected.Members.Single(m => m.Actor == actor);
        if (party.Settings != RaidPreparationPlan.NativePartySettings(member.Squad)) return null;
        var fact = new AntonRaidDisconnectFact(actor.Owner.Account, actor.Character, actor.Epoch, actor.Owner,
            expected.Key, battle.Instance, actor.Channel, member.Slot, member.Squad, null, false)
            { Luke = new(actor, battle, party, own.Slot, battle.View.Phase), LukeDeadlineAnchor = candidates.LukeAnchor };
        if (!candidates.Record(fact)) return null;
        battle.ReserveLukeDisconnect(actor);
        return fact;
    }
    internal bool TryRecordDisconnected(AntonRaidDisconnectFact fact, out string reason)
    {
        var expected = registry.Find(fact.Key, fact.Channel);
        if (expected is null) { reason = "instance-unavailable"; return false; }
        bool recorded;
        if (expected.Members.IsEmpty) recorded = RecordDisconnected(fact);
        else if (!Accounts.AccountOwners.Lease.TryExecuteLukeDisconnect(expected.Members.Select(m => (m.Actor.Owner, m.Actor.Character)).ToArray(),
            () => RecordDisconnected(fact), out recorded)) { reason = "owner-busy-or-stale"; return false; }
        reason = recorded ? "recorded" : "ineligible";
        return recorded;
    }
    internal bool RecordDisconnected(AntonRaidDisconnectFact fact)
    {
        if (fact.OldOwner.IsCurrent || fact.Luke is not { } reservation || !registry.UsesRejoinCandidates(candidates) ||
            !ReferenceEquals(candidates.Peek(fact.Character), fact) || !reservation.Battle.CanRestoreLukeRejoin(reservation) ||
            registry.Find(fact.Key, fact.Channel) is not { Type: 1, Started: { } started } raid || started.InstanceId != fact.Instance)
            return false;
        return registry.RecordLukeDisconnected(raid, fact, candidates);
    }
    internal bool CanNotice(RaidActor actor) => candidates.Peek(actor.Character) is { Luke: { } reservation } fact &&
        fact.Account == actor.Owner.Account && fact.Channel == actor.Channel && !fact.OldOwner.IsCurrent &&
        registry.Find(fact.Key, fact.Channel)?.Started?.InstanceId == fact.Instance && reservation.Battle.CanRestoreLukeRejoin(reservation);

    internal LukeRaidRejoinResult Restore(RaidActor actor, uint key, bool callerCommandHeld = false)
    {
        if (!registry.UsesRejoinCandidates(candidates)) return new(false, "candidate-store-unbound");
        var fact = candidates.Peek(actor.Character);
        if (fact?.Luke is not { } reservation || fact.Key != key || fact.Account != actor.Owner.Account ||
            fact.Channel != actor.Channel || !actor.IsCurrent || fact.OldOwner.IsCurrent || ReferenceEquals(actor.Owner, fact.OldOwner))
            return new(false, "identity-mismatch");
        var raid = registry.Find(key, actor.Channel);
        if (raid is not { Type: 1, Started: { } started } || started.InstanceId != fact.Instance) return new(false, "instance-unavailable");
        if (reservation.Bound is { } bound)
            return bound == actor && ReferenceEquals(actor.Session.CurrentRaidBattle(actor), reservation.Battle) && registry.Find(actor) is { } current &&
                parties.Current(actor.Session) is { } currentParty
                ? new(true, "already-bound", current, currentParty, reservation.Battle.View) : new(false, "reservation-consumed");
        var ordinary = parties.Snapshot().SingleOrDefault(p => p.Settings == RaidPreparationPlan.NativePartySettings(fact.Squad) &&
            p.Members.Any(m => raid.Members.Any(r => r.Squad == fact.Squad && ReferenceEquals(r.Actor.Session, m.Actor.Session))));
        var actors = raid.Members.Select(m => m.Actor).Append(actor).ToArray();
        var gates = actors.Select(m => m.Session.CommandGate).Concat(ordinary is null ? [] : new[] { ordinary.Commands })
            .Where(command => !callerCommandHeld || !ReferenceEquals(command, actor.Session.CommandGate)).Distinct().ToArray();
        var held = new List<SemaphoreSlim>();
        try
        {
            foreach (var command in gates)
            { if (!command.Wait(0)) return new(false, "command-busy"); held.Add(command); }
            return RaidOwnerGroup.Execute<LukeRaidRejoinResult>(RaidKind.Luke, actors.Select(a => (a.Owner, a.Character)).ToArray(), () =>
            {
                if (!ReferenceEquals(candidates.Peek(actor.Character), fact) || reservation.Bound is not null || !registry.IsCurrent(raid) ||
                    !actor.CanManage || !actors.All(a => a.IsCurrent) ||
                    raid.Members.Any(m => !ReferenceEquals(m.Actor.Session.CurrentRaidBattle(m.Actor), reservation.Battle)) ||
                    !reservation.Battle.CanRestoreLukeRejoin(reservation)) return new(false, "stale-reservation");
                if (!callerCommandHeld && !reservation.Battle.IsLukeWaitingRoom(actor)) return new(false, "waiting-room-required");
                RaidParty? restored = null;
                var restoredParty = parties.RestoreLukeRaidMember(ordinary, reservation.OriginalParty, actor, reservation.PartySlot, fact.Squad, _ =>
                {
                    restored = registry.RestoreLukeMember(raid, fact, actor, () => reservation.Battle.RestoreLukeMember(reservation, actor,
                        () => actor.Session.InstallLukeRejoin(actor, reservation.Battle)));
                    return restored is not null;
                });
                if (restoredParty is null) return new(false, "binding-conflict");
                reservation.Bound = actor;
                return new(true, "bound-without-wire", restored, restoredParty, reservation.Battle.View);
            });
        }
        catch (InvalidOperationException) when (actors.Any(a => !a.IsCurrent)) { return new(false, "owner-changed"); }
        finally { foreach (var command in held.AsEnumerable().Reverse()) command.Release(); }
    }
}
