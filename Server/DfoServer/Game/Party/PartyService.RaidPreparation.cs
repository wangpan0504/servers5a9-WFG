using System.Collections.Immutable;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;

namespace DfoServer.Game.Party;

internal sealed record RaidPartyGroup(ImmutableArray<PartyMember> Members, PartySettings Settings, PartySnapshot? Existing);
internal sealed record RaidPartyBatch(RaidPreparationPlan Raid, ImmutableArray<PartySnapshot> Bindings,
    ImmutableArray<RaidPartyGroup> Groups);

internal sealed partial class PartyService
{
    internal (RaidPartyBatch? Batch, string? Rejection) PlanRaidPreparation(RaidPreparationPlan plan,
        ImmutableArray<PartySnapshot> bindings)
    {
        lock (gate)
            if (!RaidBindingsCurrent(plan, bindings)) return (null, "ordinary-party-changed");
        var groups = ImmutableArray.CreateBuilder<RaidPartyGroup>();
        foreach (var squad in plan.Squads)
        {
            var related = bindings.Where(p => p.Members.Any(m => squad.Members.Any(r =>
                ReferenceEquals(r.Actor.Session, m.Actor.Session)))).ToArray();
            var settings = RaidPreparationPlan.NativePartySettings(squad.Squad);
            if (related.Length > 1) return (null, "ordinary-party-conflict");
            if (related.Length == 1)
            {
                var existing = related[0];
                if (existing.Members.Length != squad.Members.Length || existing.Members.Any(m => !m.IsCurrent() ||
                    !squad.Members.Any(r => ReferenceEquals(r.Actor.Session, m.Actor.Session) &&
                        ReferenceEquals(r.Actor.Owner, m.Actor.Owner) && r.Actor.Epoch == m.Actor.Epoch &&
                        r.Actor.Character == m.Actor.Character.Id)) ||
                    !existing.Members.Any(m => m.Slot == existing.Leader && ReferenceEquals(m.Actor.Session, squad.Anchor.Session)))
                    return (null, "ordinary-party-conflict");
                if (existing.Settings != settings) return (null, "ordinary-party-settings-conflict");
                groups.Add(new([..existing.Members], settings, existing));
                continue;
            }
            var captured = ImmutableArray.CreateBuilder<PartyMember>();
            foreach (var member in squad.Members)
            {
                if (Capture(member.Actor.Session) is not { } current || !ReferenceEquals(current.Actor.Owner, member.Actor.Owner) ||
                    current.Actor.Epoch != member.Actor.Epoch) return (null, "raid-member-not-current");
                captured.Add(current with { Slot = checked((byte)captured.Count) });
            }
            var row = captured.ToImmutable();
            // 已有匹配队伍不因后来屏蔽而被拆掉；仅新组队沿原邀请的锚点双向屏蔽条件。
            if (row.Skip(1).Any(member => Blocked(row[0], member))) return (null, "ordinary-party-blocked");
            groups.Add(new(row, settings, null));
        }
        return (new(plan, bindings, groups.ToImmutable()), null);
    }

    bool RaidBindingsCurrent(RaidPreparationPlan plan, ImmutableArray<PartySnapshot> expected)
    {
        var current = parties.SnapshotValues().Where(p => p.Members.Any(m => plan.Source.Members.Any(r =>
            ReferenceEquals(r.Actor.Session, m.Actor.Session)))).ToArray();
        return current.Length == expected.Length && current.All(p => expected.Any(old => ReferenceEquals(old, p))) &&
            current.All(p => p.Members.All(m => m.IsCurrent() && m.Actor.Session.Phase == SessionPhase.Town));
    }

    static bool RaidBatchMatches(RaidPartyBatch batch)
    {
        if (batch.Groups.IsDefault || batch.Bindings.IsDefault || batch.Bindings.Length > batch.Raid.Source.Members.Length ||
            batch.Groups.Length != batch.Raid.Squads.Length || batch.Bindings.Any(bound => bound is null)) return false;
        for (int index = 0; index < batch.Groups.Length; index++)
        {
            var group = batch.Groups[index];
            var squad = batch.Raid.Squads[index];
            if (group is null || group.Members.IsDefault || group.Members.Length != squad.Members.Length ||
                group.Settings != RaidPreparationPlan.NativePartySettings(squad.Squad)) return false;
            foreach (var expected in squad.Members)
                if (group.Members.Count(member => member is not null && member.Actor is not null &&
                    ReferenceEquals(member.Actor.Session, expected.Actor.Session) && ReferenceEquals(member.Actor.Owner, expected.Actor.Owner) &&
                    member.Actor.Epoch == expected.Actor.Epoch && member.Actor.Character?.Id == expected.Actor.Character) != 1) return false;
            if (group.Members.Any(member => member.Identity is null || !ReferenceEquals(member.Identity.Character, member.Actor.Character) ||
                member.Identity.Channel != batch.Raid.Source.Channel)) return false;
            if (group.Existing is { } existing)
            {
                if (!batch.Bindings.Any(bound => ReferenceEquals(bound, existing)) || existing.Settings != group.Settings ||
                    existing.Members.Length != group.Members.Length ||
                    !existing.Members.Select((member, at) => ReferenceEquals(member, group.Members[at])).All(value => value) ||
                    !existing.Members.Any(member => member.Slot == existing.Leader && ReferenceEquals(member.Actor.Session, squad.Anchor.Session)))
                    return false;
            }
            else
            {
                for (int at = 0; at < group.Members.Length; at++)
                    if (group.Members[at].Slot != at || !ReferenceEquals(group.Members[at].Actor.Session, squad.Members[at].Actor.Session)) return false;
                if (batch.Bindings.Any(bound => bound.Members.Any(member => squad.Members.Any(expected =>
                    ReferenceEquals(member.Actor.Session, expected.Actor.Session))))) return false;
            }
        }
        return true;
    }

    // 调用方已持固定current全团owner并完成Raid复验；不持Raid gate进入本gate，避免StartedRaid查询反向锁链。
    internal RaidPreparationResult CommitRaidPreparation(RaidPartyBatch batch, CancellationToken cancellation)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!RaidBatchMatches(batch)) return RaidPreparationResult.Reject("invalid-raid-party-batch");
            if (!RaidBindingsCurrent(batch.Raid, batch.Bindings) ||
                batch.Groups.Any(g => g.Members.Any(m => !m.IsCurrent() || !m.Actor.Session.CanChangeTownParty || m.Actor.Session.StartedRaid is not null)))
                return RaidPreparationResult.Reject("ordinary-party-changed");
            ushort plannedId = nextId;
            long plannedRevision = revision;
            var reserved = new HashSet<ushort>();
            var completed = ImmutableArray.CreateBuilder<PartySnapshot>();
            var created = new List<PartySnapshot>();
            foreach (var group in batch.Groups)
            {
                if (group.Existing is { } existing) { completed.Add(existing); continue; }
                bool found = false;
                for (int attempt = 0; attempt < ushort.MaxValue; attempt++)
                {
                    plannedId = (ushort)((plannedId + 1) % ushort.MaxValue);
                    if (!parties.TryGetValue(plannedId, out _) && !reservedRaidSquads.Contains(plannedId) && reserved.Add(plannedId)) { found = true; break; }
                }
                if (!found) return RaidPreparationResult.Reject("ordinary-party-capacity");
                var party = new PartySnapshot(plannedId, [..group.Members], 0, ++plannedRevision) { Settings = group.Settings };
                completed.Add(party); created.Add(party);
            }
            // 全部业务校验、ID规划、对象与通知构造完成后才安装；不逐队提交再补偿删除。
            var notices = created.SelectMany(p => p.Members.Select(m => Updated(p, m))).ToImmutableArray();
            cancellation.ThrowIfCancellationRequested();
            foreach (var party in created)
            {
                foreach (var member in party.Members) member.Actor.Session.AdvancePartyMembership();
                parties.Set(party.Id, party);
            }
            nextId = plannedId; revision = plannedRevision;
            return new(true, created.Count == 0 ? "ordinary-parties-already-prepared" : "ordinary-parties-prepared",
                completed.ToImmutable(), notices);
        }
    }
}
