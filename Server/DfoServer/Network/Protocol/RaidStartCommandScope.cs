using System.Collections.Immutable;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;

namespace DfoServer.Network.Protocol;

// Handler已经持本Dispatch门禁；扩展只尝试获取，不等待，不创建后台重试。
internal sealed class RaidStartCommandScope : IDisposable
{
    readonly List<SemaphoreSlim> held = [];
    bool active = true;
    readonly (RaidActor Actor, PartySnapshot? Party, long Membership)[] members;
    readonly GameConnectionContext context;
    readonly DispatchGateScope dispatch;
    public ImmutableArray<PartySnapshot> Parties { get; }

    RaidStartCommandScope(GameConnectionContext context, DispatchGateScope dispatch, RaidParty raid)
    {
        this.context = context;
        this.dispatch = dispatch;
        members = raid.Members.OrderBy(m => m.Actor.Owner.Account, StringComparer.Ordinal).ThenBy(m => m.Actor.Character)
            .Select(m => (m.Actor, m.Actor.Session.CurrentParty?.Invoke(), m.Actor.Session.PartyMembershipVersion)).ToArray();
        Parties = members.Where(m => m.Party is not null).Select(m => m.Party!).Distinct().OrderBy(p => p.Id).ToImmutableArray();
    }

    public static RaidStartCommandScope? TryEnter(GameConnectionContext context, RaidParty raid)
    {
        if (context.DispatchGates is not { } dispatch || !dispatch.Holds(context, context.CommandGate)) return null;
        var scope = new RaidStartCommandScope(context, dispatch, raid);
        try
        {
            var gates = scope.Parties.Select(p => p.Commands).Concat(scope.members.Select(m => m.Actor.Session.CommandGate)).Distinct();
            foreach (var gate in gates)
            {
                context.Stop.ThrowIfCancellationRequested();
                if (dispatch.Holds(context, gate)) continue;
                if (!gate.Wait(0)) { scope.Dispose(); return null; }
                scope.held.Add(gate);
            }
            if (!scope.IsCurrent()) { scope.Dispose(); return null; }
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    // 规划和提交前复验原快照与成员版本；同ID重建或取锁前后进退队均不能借旧门禁继续。
    public bool IsCurrent() => active && !context.Stop.IsCancellationRequested && dispatch.Holds(context, context.CommandGate) && members.All(m =>
        m.Actor.IsCurrent && m.Membership == m.Actor.Session.PartyMembershipVersion &&
        ReferenceEquals(m.Party, m.Actor.Session.CurrentParty?.Invoke()));

    public void Dispose()
    {
        active = false;
        for (int index = held.Count - 1; index >= 0; index--) held[index].Release();
        held.Clear();
    }
}
