using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Party;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed record RaidPreparationResult(bool Accepted, string Reason,
    ImmutableArray<PartySnapshot> Parties, ImmutableArray<PartyNotice> Notices)
{
    public static RaidPreparationResult Reject(string reason) => new(false, reason, [], []);
}

// 独立用例入口，尚未接START。已持分派门禁调用只会Busy，不在Handler内扩锁等待。
internal sealed class RaidPreparationService(RaidRegistry raids, PartyService parties, RaidCatalog catalog)
{
    public RaidPreparationResult TryPrepare(RaidPreparationPlan plan, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var definition = catalog.Find(plan.Source.Type);
        if (definition is not { Type: 1, MemberMax: >= 1 and <= 8 } || definition.MemberMax != plan.Source.Capacity)
            return RaidPreparationResult.Reject("raid-definition-mismatch");
        if (!plan.IsCurrent(raids)) return RaidPreparationResult.Reject("raid-preparation-stale");
        var members = plan.Source.Members.OrderBy(m => m.Actor.Owner.Account, StringComparer.Ordinal)
            .ThenBy(m => m.Actor.Character).ToArray();
        var bindings = parties.Snapshot().Where(p => p.Members.Any(m =>
            members.Any(r => ReferenceEquals(r.Actor.Session, m.Actor.Session)))).OrderBy(p => p.Id).ToImmutableArray();
        // 按对象去重而非仅按ID；同一party的不同record版本共享Commands，ID回收则必须复验原对象。
        var gates = bindings.Select(p => p.Commands).Concat(members.Select(m => m.Actor.Session.CommandGate)).Distinct().ToArray();
        int held = 0;
        try
        {
            foreach (var gate in gates)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!gate.Wait(0)) return RaidPreparationResult.Reject("raid-preparation-busy");
                held++;
            }
            return AccountOwners.Lease.ExecuteRaidGroup(members.Select(m => (m.Actor.Owner, m.Actor.Character)).ToArray(),
                definition.MemberMax, () =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!plan.IsCurrent(raids)) return RaidPreparationResult.Reject("raid-preparation-stale");
                    // PublicCharacter和屏蔽查询在两个领域gate之外；所有参与owner已经固定。
                    var (batch, rejection) = parties.PlanRaidPreparation(plan, bindings);
                    if (batch is null) return RaidPreparationResult.Reject(rejection!);
                    cancellation.ThrowIfCancellationRequested();
                    var result = raids.CommitPreparation(plan, parties, batch, cancellation);
                    if (!result.Accepted) return result;
                    return result with { Notices = result.Notices.Select(notice =>
                    {
                        var original = notice.Guard;
                        return notice with { Guard = () => plan.IsCurrent(raids) && (original?.Invoke() ?? true) };
                    }).ToImmutableArray() };
                });
        }
        catch (InvalidOperationException) when (members.Any(m => !m.Actor.IsCurrent))
        {
            return RaidPreparationResult.Reject("raid-preparation-stale");
        }
        finally
        {
            for (int index = held - 1; index >= 0; index--) gates[index].Release();
        }
    }
}
