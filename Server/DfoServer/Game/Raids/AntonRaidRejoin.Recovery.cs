using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Session;

namespace DfoServer.Game.Raids;

internal sealed record AntonRaidRejoinReservation(RaidActor Original, AntonRaidBattle Battle,
    AntonRaidBattle.AntonMemberReservation Member, GameSession.AntonRejoinState State,
    ushort Party, SemaphoreSlim Commands, byte PartySlot, byte Leader,
    ImmutableArray<(ushort Character, byte Slot)> Members, PartyDungeonInstance? Run)
{
    internal RaidActor? Bound { get; set; }
}
internal sealed record AntonRaidRecovery(bool Restored, string Reason, Guid? Instance = null, Guid? Run = null);

internal sealed partial class RaidRegistry
{
    internal AntonRaidRecovery RestoreAnton(RaidActor actor, uint key, AntonRaidRejoin candidates, PartyService parties)
    {
        var fact = candidates.Peek(actor.Character);
        if (!UsesRejoinCandidates(candidates) || fact?.Anton is not { } reserved || fact.Key != key ||
            fact.Account != actor.Owner.Account || fact.Channel != actor.Channel || !actor.IsCurrent || fact.OldOwner.IsCurrent)
            return new(false, "identity-mismatch");
        if (fact.Attempted || reserved.Bound is not null) return new(false, "duplicate-rejoin");
        var raid = Find(key, actor.Channel);
        var party = parties.Snapshot().SingleOrDefault(p => p.Id == reserved.Party && ReferenceEquals(p.Commands, reserved.Commands));
        if (raid is not { Type: 0, Started: { } started } || started.InstanceId != fact.Instance || party is null)
            return new(false, "original-party-unavailable");
        var actors = raid.Members.Select(m => m.Actor).Append(actor).ToArray();
        var held = new List<SemaphoreSlim>();
        try
        {
            // Handler已持本人命令门禁；其余门禁只尝试一次，忙时不消费候选，客户端可重试。
            foreach (var command in actors.Where(a => a != actor).Select(a => a.Session.CommandGate).Append(party.Commands).Distinct())
            {
                if (!command.Wait(0)) return new(false, "command-busy");
                held.Add(command);
            }
            return RaidOwnerGroup.Execute(RaidKind.Anton, actors.Select(a => (a.Owner, a.Character)).ToArray(), () =>
            {
                if (!ReferenceEquals(candidates.Peek(actor.Character), fact) || fact.Attempted || reserved.Bound is not null ||
                    !IsCurrent(raid) || !actors.All(a => a.IsCurrent) || !actor.CanManage ||
                    !reserved.Member.CanRestore() || !reserved.State.CanInstall(actor.Session, party, reserved.Run, reserved.Member))
                    return new AntonRaidRecovery(false, "stale-run-or-session");
                reserved.State.CommitPosition(actor.Session);
                var restored = parties.RestoreAntonRaidMember(party, reserved, actor, planned =>
                {
                    lock (gate)
                    {
                        if (!IsCurrent(raid) || FindCore(actor) is not null || raid.Members.Length >= raid.Capacity ||
                            raid.Members.Any(m => m.Slot == fact.Slot || m.Actor.Character == actor.Character) ||
                            raid.Started?.InstanceId != fact.Instance) return false;
                        if (!reserved.Member.Restore(actor, () => reserved.State.Install(actor.Session, actor, planned, reserved.Run))) return false;
                        this.parties[key] = raid with { Members = raid.Members.Add(new(actor, fact.Slot, fact.Squad)), Revision = ++revision };
                        reserved.Bound = actor;
                        fact.Attempted = true;
                        return true;
                    }
                });
                return restored is null ? new(false, "binding-conflict") : new(true, "state-restored-wire-unimplemented", fact.Instance, fact.Run);
            });
        }
        catch (InvalidOperationException) when (actors.Any(a => !a.IsCurrent)) { return new(false, "owner-changed"); }
        finally { foreach (var command in held.AsEnumerable().Reverse()) command.Release(); }
    }
}
