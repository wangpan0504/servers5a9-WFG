using System.Collections.Immutable;
using DfoServer.Game.Party;

namespace DfoServer.Game.Raids;

// 不保留PartyMember/PublicCharacterSnapshot，避免身份预约间接保存旧资产快照。
internal sealed record LukeRaidPartyIdentity(ushort Id, SemaphoreSlim Commands,
    ImmutableArray<(ushort Character, byte Slot)> Members);

// 只保存原身份和原聚合引用，不保存角色资产；当前没有Runtime/协议装配。
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

internal sealed record LukeRaidRejoinResult(bool Accepted, string Reason, RaidParty? Raid = null,
    PartySnapshot? Party = null, AntonRaidBattleView? Battle = null);

// 默认未接入835/668及断线事件。先完成可复验的领域事务，不猜恢复通知包序。
internal sealed class LukeRaidRejoin(RaidRegistry registry, PartyService parties, AntonRaidRejoin candidates)
{
    internal AntonRaidDisconnectFact? Capture(RaidActor actor)
    {
        var expected = registry.Find(actor);
        if (expected is null) return null;
        return RaidOwnerGroup.Execute<AntonRaidDisconnectFact?>(RaidKind.Luke, expected.Members.Select(m => (m.Actor.Owner, m.Actor.Character)).ToArray(), () =>
        {
        if (!registry.UsesRejoinCandidates(candidates) || !candidates.Enabled || !actor.IsCurrent || !actor.CanManage ||
            !registry.IsCurrent(expected) || registry.Find(actor) is not { Type: 1, Started: { } started } raid ||
            actor.Session.CurrentRaidBattle(actor) is not { Kind: RaidKind.Luke } battle ||
            started.InstanceId != battle.Instance || parties.Current(actor.Session) is not { } party ||
            party.Members.SingleOrDefault(m => ReferenceEquals(m.Actor.Session, actor.Session)) is not { } own ||
            !battle.CanCaptureLukeRejoin(actor)) return null;
        var member = raid.Members.Single(m => m.Actor == actor);
        if (party.Settings != RaidPreparationPlan.NativePartySettings(member.Squad) ||
            !party.Members.Any(m => !ReferenceEquals(m.Actor.Session, actor.Session) && m.IsCurrent() &&
                raid.Members.Any(r => r.Squad == member.Squad && ReferenceEquals(r.Actor.Session, m.Actor.Session) &&
                    ReferenceEquals(r.Actor.Session.CurrentRaidBattle(r.Actor), battle)))) return null;
        return new AntonRaidDisconnectFact(actor.Owner.Account, actor.Character, actor.Epoch, actor.Owner,
            raid.Key, battle.Instance, actor.Channel, member.Slot, member.Squad, null, false)
            { Luke = new(actor, battle, party, own.Slot, battle.View.Phase) };
        });
    }

    internal bool RecordDisconnected(AntonRaidDisconnectFact fact)
    {
        if (!registry.UsesRejoinCandidates(candidates) || fact.Luke is not { } reservation || fact.OldOwner.IsCurrent ||
            !reservation.Battle.CanRestoreLukeRejoin(reservation) ||
            registry.Find(fact.Key, fact.Channel) is not { Type: 1, Started: { } started } raid ||
            started.InstanceId != fact.Instance || raid.Members.Any(m => m.Actor.Character == fact.Character) ||
            !HasSurvivor(raid, fact)) return false;
        return registry.RecordLukeDisconnected(raid, fact, candidates);
    }

    bool HasSurvivor(RaidParty raid, AntonRaidDisconnectFact fact)
    {
        if (fact.Luke is not { } reservation) return false;
        foreach (var member in raid.Members.Where(m => m.Squad == fact.Squad))
        {
            try { if (member.Actor.IsCurrent && ReferenceEquals(member.Actor.Session.CurrentRaidBattle(member.Actor), reservation.Battle)) return true; }
            catch (InvalidOperationException) when (!member.Actor.IsCurrent) { }
        }
        return false;
    }

    // 此领域入口自己取得命令门禁；将来Handler接入必须另核对已持门禁及恢复投影，不直接调用冒充成功。
    internal LukeRaidRejoinResult Restore(RaidActor actor, uint key)
    {
        if (!registry.UsesRejoinCandidates(candidates)) return new(false, "candidate-store-unbound");
        var fact = candidates.Peek(actor.Character);
        if (fact?.Luke is not { } reservation || fact.Key != key || fact.Account != actor.Owner.Account ||
            fact.Channel != actor.Channel || !actor.IsCurrent || fact.OldOwner.IsCurrent || ReferenceEquals(actor.Owner, fact.OldOwner))
            return new(false, "identity-mismatch");
        var raid = registry.Find(key, actor.Channel);
        if (raid is not { Type: 1, Started: { } started } || started.InstanceId != fact.Instance)
            return new(false, "instance-unavailable");
        if (reservation.Bound is { } bound)
            return bound == actor && ReferenceEquals(actor.Session.CurrentRaidBattle(actor), reservation.Battle) &&
                registry.Find(actor) is { } current && current.Started?.InstanceId == fact.Instance &&
                parties.Current(actor.Session) is { } currentParty && currentParty.Id == reservation.OriginalParty.Id &&
                ReferenceEquals(currentParty.Commands, reservation.OriginalParty.Commands)
                ? new(true, "already-bound", current, currentParty, reservation.Battle.View) : new(false, "reservation-consumed");
        if (!HasSurvivor(raid, fact)) return new(false, "squad-unavailable");
        var ordinary = parties.Snapshot().SingleOrDefault(p => p.Id == reservation.OriginalParty.Id);
        if (ordinary is null || !ReferenceEquals(ordinary.Commands, reservation.OriginalParty.Commands))
            return new(false, "ordinary-party-replaced");
        var actors = raid.Members.Select(m => m.Actor).Append(actor).ToArray();
        var gates = actors.Select(m => m.Session.CommandGate).Append(ordinary.Commands).Distinct().ToArray();
        var held = new List<SemaphoreSlim>();
        try
        {
            foreach (var gate in gates)
            {
                if (!gate.Wait(0)) return new(false, "command-busy");
                held.Add(gate);
            }
            return RaidOwnerGroup.Execute<LukeRaidRejoinResult>(RaidKind.Luke, actors.Select(a => (a.Owner, a.Character)).ToArray(), () =>
            {
                if (!ReferenceEquals(candidates.Peek(actor.Character), fact) || reservation.Bound is not null ||
                    !registry.IsCurrent(raid) || !actors.All(a => a.IsCurrent && a.CanManage) ||
                    raid.Members.Any(m => !ReferenceEquals(m.Actor.Session.CurrentRaidBattle(m.Actor), reservation.Battle)) ||
                    ordinary.Members.Any(m => !raid.Members.Any(r => r.Squad == fact.Squad &&
                        ReferenceEquals(r.Actor.Session, m.Actor.Session) && r.Actor.Epoch == m.Actor.Epoch)) ||
                    !reservation.Battle.CanRestoreLukeRejoin(reservation)) return new LukeRaidRejoinResult(false, "stale-reservation");
                if (actors.Any(a => !reservation.Battle.IsLukeWaitingRoom(a))) return new(false, "waiting-room-required");
                var anchor = raid.Members.Where(m => m.Squad == fact.Squad)
                    .Append(new(actor, fact.Slot, fact.Squad)).MinBy(m => m.Slot)!;
                if (!ordinary.Members.Any(m => m.Slot == ordinary.Leader && m.Actor.Character.Id == anchor.Actor.Character))
                    return new(false, "squad-leader-contract-unavailable");
                RaidParty? restored = null;
                var restoredParty = parties.RestoreLukeRaidMember(ordinary, reservation.OriginalParty, actor,
                    reservation.PartySlot, fact.Squad, planned =>
                    {
                        restored = registry.RestoreLukeMember(raid, fact, actor, () =>
                            reservation.Battle.RestoreLukeMember(reservation, actor, () =>
                                actor.Session.InstallLukeRejoin(actor, reservation.Battle)));
                        return restored is not null;
                    });
                if (restoredParty is null) return new(false, "binding-conflict");
                reservation.Bound = actor;
                return new(true, "bound-without-wire", restored, restoredParty, reservation.Battle.View);
            });
        }
        catch (InvalidOperationException) when (actors.Any(a => !a.IsCurrent)) { return new(false, "owner-changed"); }
        finally { foreach (var gate in held.AsEnumerable().Reverse()) gate.Release(); }
    }
}
