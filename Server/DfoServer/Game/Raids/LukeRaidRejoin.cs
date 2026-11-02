using System.Collections.Immutable;
using DfoServer.Game.Party;

namespace DfoServer.Game.Raids;

// 预约仅保存原身份与聚合引用，不保存离线角色资产。
internal sealed record LukeRaidPartyIdentity(ushort Id, SemaphoreSlim Commands,
    ImmutableArray<(ushort Character, byte Slot)> Members);
internal sealed class LukeRaidRejoinReservation(RaidActor original, AntonRaidBattle battle,
    PartySnapshot? party, byte partySlot, int phase)
{
    internal RaidActor Original { get; } = original;
    internal AntonRaidBattle Battle { get; } = battle;
    readonly LukeRaidPartyIdentity? originalParty = party is null ? null : new(party.Id, party.Commands,
        party.Members.Select(member => (member.Actor.Character.Id, member.Slot)).ToImmutableArray());
    internal LukeRaidPartyIdentity OriginalParty => originalParty ?? throw new InvalidOperationException("漩涡重返不恢复旧临时队伍");
    internal ushort? OriginalPartyId => originalParty?.Id;
    internal int? FiendwarArea { get; init; }
    internal byte PartySlot { get; } = partySlot;
    internal int Phase { get; } = phase;
    internal RaidActor? Bound { get; set; }
}
internal sealed record LukeRaidDisconnectCandidate(Guid Instance, uint Key, byte RaidSlot, byte Squad,
    ushort PartyId, byte PartySlot, int Phase, bool OldOwnerReleased);
internal sealed record LukeRaidRejoinResult(bool Accepted, string Reason, RaidParty? Raid = null,
    PartySnapshot? Party = null, AntonRaidBattleView? Battle = null);

// 共用五分钟原团恢复；漩涡保留战场、世界结果与随机，只在正式遭遇时重新组成区域临时队伍。
internal sealed class LukeRaidRejoin(RaidRegistry registry, PartyService parties, AntonRaidRejoin candidates)
{
    internal LukeRaidDisconnectCandidate? DescribeCandidate(ushort character) => candidates.DescribeLukeCandidate(character);
    internal AntonRaidDisconnectFact? TryCaptureForDisconnect(DfoServer.Game.Session.GameSession session, out string reason)
    {
        var actor = session.ReadLukeDisconnectActorHint();
        var raid = actor is null ? null : registry.Find(actor);
        if (actor is null || raid is null) { reason = "no-active-luke"; return null; }
        if (raid.Type == 2)
        {
            // 漩涡没有固定普通分队；捕获只读取本人会话及有锁聚合，不能因另一线路短暂持owner而丢重返资格。
            var captured = actor.Owner.Execute(() => CaptureLocked(actor, raid));
            reason = captured is null ? "ineligible" : "captured";
            return captured;
        }
        if (!Accounts.AccountOwners.Lease.TryExecuteLukeDisconnect(raid.Members.Select(m => (m.Actor.Owner, m.Actor.Character)).ToArray(),
            () => CaptureLocked(actor, raid), out AntonRaidDisconnectFact? fact))
        { reason = "owner-busy-or-stale"; return null; }
        reason = fact is null ? "ineligible" : "captured";
        return fact;
    }
    internal AntonRaidDisconnectFact? Capture(RaidActor actor)
    {
        var raid = registry.Find(actor);
        return raid is null ? null : RaidOwnerGroup.Execute<AntonRaidDisconnectFact?>((RaidKind)raid.Type,
            raid.Members.Select(m => (m.Actor.Owner, m.Actor.Character)).ToArray(), () => CaptureLocked(actor, raid));
    }
    AntonRaidDisconnectFact? CaptureLocked(RaidActor actor, RaidParty expected)
    {
        if (!registry.UsesRejoinCandidates(candidates) || !candidates.LukeCandidatesEnabled || !actor.IsCurrent ||
            !registry.IsCurrent(expected) || expected is not { Type: 1 or 2, Started: { } started } ||
            actor.Session.CurrentRaidBattle(actor) is not { Kind: RaidKind.Luke or RaidKind.Fiendwar } battle ||
            expected.Type != (byte)battle.Kind || started.InstanceId != battle.Instance ||
            !battle.CanCaptureLukeRejoin(actor)) return null;
        var member = expected.Members.Single(m => m.Actor == actor);
        var party = parties.Current(actor.Session);
        var own = party?.Members.SingleOrDefault(m => ReferenceEquals(m.Actor.Session, actor.Session));
        if (battle.Kind == RaidKind.Luke && (party is null || own is null ||
            party.Settings != RaidPreparationPlan.NativePartySettings(member.Squad))) return null;
        var fact = new AntonRaidDisconnectFact(actor.Owner.Account, actor.Character, actor.Epoch, actor.Owner,
            expected.Key, battle.Instance, actor.Channel, member.Slot, member.Squad, null, false)
            { Luke = new(actor, battle, party, own?.Slot ?? 0, battle.View.Phase)
                { FiendwarArea = battle.Kind == RaidKind.Fiendwar ? battle.FiendwarPosition(actor.Character) : null },
                LukeDeadlineAnchor = candidates.LukeAnchor };
        if (!candidates.Record(fact)) return null;
        battle.ReserveLukeDisconnect(actor);
        return fact;
    }
    internal bool TryRecordDisconnected(AntonRaidDisconnectFact fact, out string reason)
    {
        var expected = registry.Find(fact.Key, fact.Channel);
        if (expected is null) { reason = "instance-unavailable"; return false; }
        bool recorded;
        if (expected.Type == 2 || expected.Members.IsEmpty) recorded = RecordDisconnected(fact);
        else if (!Accounts.AccountOwners.Lease.TryExecuteLukeDisconnect(expected.Members.Select(m => (m.Actor.Owner, m.Actor.Character)).ToArray(),
            () => RecordDisconnected(fact), out recorded)) { reason = "owner-busy-or-stale"; return false; }
        reason = recorded ? "recorded" : "ineligible";
        return recorded;
    }
    internal bool RecordDisconnected(AntonRaidDisconnectFact fact)
    {
        if (fact.OldOwner.IsCurrent || fact.Luke is not { } reservation || !registry.UsesRejoinCandidates(candidates) ||
            !ReferenceEquals(candidates.Peek(fact.Character), fact) || !reservation.Battle.CanRestoreLukeRejoin(reservation) ||
            registry.Find(fact.Key, fact.Channel) is not { Type: 1 or 2, Started: { } started } raid ||
            raid.Type != (byte)reservation.Battle.Kind || started.InstanceId != fact.Instance)
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
        if (raid is not { Type: 1 or 2, Started: { } started } || raid.Type != (byte)reservation.Battle.Kind || started.InstanceId != fact.Instance)
            return new(false, "instance-unavailable");
        if (reservation.Bound is { } bound)
            return bound == actor && ReferenceEquals(actor.Session.CurrentRaidBattle(actor), reservation.Battle) && registry.Find(actor) is { } current &&
                (raid.Type == 2 || parties.Current(actor.Session) is not null)
                ? new(true, "already-bound", current, parties.Current(actor.Session), reservation.Battle.View) : new(false, "reservation-consumed");
        var ordinary = raid.Type == 2 ? null : parties.Snapshot().SingleOrDefault(p => p.Settings == RaidPreparationPlan.NativePartySettings(fact.Squad) &&
            p.Members.Any(m => raid.Members.Any(r => r.Squad == fact.Squad && ReferenceEquals(r.Actor.Session, m.Actor.Session))));
        var actors = raid.Members.Select(m => m.Actor).Append(actor).ToArray();
        var gates = actors.Select(m => m.Session.CommandGate).Concat(ordinary is null ? [] : new[] { ordinary.Commands })
            .Where(command => !callerCommandHeld || !ReferenceEquals(command, actor.Session.CommandGate)).Distinct().ToArray();
        var held = new List<SemaphoreSlim>();
        try
        {
            foreach (var command in gates)
            { if (!command.Wait(0)) return new(false, "command-busy"); held.Add(command); }
            return RaidOwnerGroup.Execute<LukeRaidRejoinResult>((RaidKind)raid.Type, actors.Select(a => (a.Owner, a.Character)).ToArray(), () =>
            {
                if (!ReferenceEquals(candidates.Peek(actor.Character), fact) || reservation.Bound is not null || !registry.IsCurrent(raid) ||
                    !actor.CanManage || !actors.All(a => a.IsCurrent) ||
                    raid.Members.Any(m => !ReferenceEquals(m.Actor.Session.CurrentRaidBattle(m.Actor), reservation.Battle)) ||
                    !reservation.Battle.CanRestoreLukeRejoin(reservation)) return new(false, "stale-reservation");
                if (!callerCommandHeld && !reservation.Battle.IsLukeWaitingRoom(actor)) return new(false, "waiting-room-required");
                if (raid.Type == 2)
                {
                    // 全团owner和命令门禁已持有；持久CAS在目录/Battle gate外，失败不能发重返成功。
                    if (!actor.Session.ChangeFiendwarRejoin(reservation.Original, actor, reservation.Battle))
                        return new(false, "persistent-rejoin-expired-or-stale");
                    bool installed = false;
                    try
                    {
                        var battlefield = registry.RestoreLukeMember(raid, fact, actor, () => reservation.Battle.RestoreLukeMember(reservation, actor,
                            () => actor.Session.InstallLukeRejoin(actor, reservation.Battle)));
                        if (battlefield is null) return new(false, "binding-conflict");
                        if (!candidates.MarkLukeCandidateBound(fact)) throw new InvalidOperationException("漩涡重返成功后候选已失效");
                        reservation.Bound = actor;
                        installed = true;
                        return new(true, "bound-without-wire", battlefield, null, reservation.Battle.View);
                    }
                    finally
                    {
                        if (!installed && !actor.Session.ChangeFiendwarRejoin(reservation.Original, actor, reservation.Battle, undo: true))
                            throw new InvalidOperationException("漩涡重返安装失败后原持久资格未能归还");
                    }
                }
                RaidParty? restored = null;
                var restoredParty = parties.RestoreLukeRaidMember(ordinary, reservation.OriginalParty, actor, reservation.PartySlot, fact.Squad, _ =>
                {
                    restored = registry.RestoreLukeMember(raid, fact, actor, () => reservation.Battle.RestoreLukeMember(reservation, actor,
                        () => actor.Session.InstallLukeRejoin(actor, reservation.Battle)));
                    return restored is not null;
                });
                if (restoredParty is null) return new(false, "binding-conflict");
                if (!candidates.MarkLukeCandidateBound(fact)) throw new InvalidOperationException("卢克重返成功后候选已失效");
                reservation.Bound = actor;
                return new(true, "bound-without-wire", restored, restoredParty, reservation.Battle.View);
            });
        }
        catch (InvalidOperationException) when (actors.Any(a => !a.IsCurrent)) { return new(false, "owner-changed"); }
        finally { foreach (var command in held.AsEnumerable().Reverse()) command.Release(); }
    }
}
