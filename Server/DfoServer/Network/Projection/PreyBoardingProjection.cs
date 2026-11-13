using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 复用Battle时钟通知；每连接只有一个执行器和一个待办，不另建计时器。
internal sealed class PreyBoardingProjection(GameConnectionContext context,
    Func<AntonRaidBattleView, RaidActor, Task> dispatch, Action<Exception>? failed = null) : IAsyncDisposable
{
    readonly object gate = new();
    readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
    sealed record Request(AntonRaidBattleView View, RaidActor Actor, PreyLandingCandidate Landing, PartySnapshot? Party,
        IDisposable? DefenceAttempt);
    PreyLandingIdentity? observed;
    Request? requested;
    Task pending = Task.CompletedTask;
    bool running, disposed;
    internal Task Completion { get { lock (gate) return pending; } }

    internal void Notice(AntonRaidBattleView view, RaidActor actor)
    {
        var battle = context.Session.CurrentRaidBattle(actor);
        if (battle?.CapturePreyLandingArrival(view) is not { } arrival) return;
        IDisposable? superseded;
        lock (gate)
        {
            if (disposed || observed == arrival) return;
            if (!actor.IsCurrent || context.Session.Phase != SessionPhase.Town || !context.Session.Ready ||
                context.Session.Selected?.Position is not { Town: 59, Area: 4 or 5 }) { observed = arrival; return; }
            var position = context.Session.Selected.Position;
            var party = context.Session.CurrentParty?.Invoke();
            if (party is not null && !party.Members.Any(member => member.Slot == party.Leader &&
                    ReferenceEquals(member.Actor.Session, context.Session)) ||
                battle.CapturePreyLanding(view, position.Area) is not { } landing ||
                party?.Members.Any(member => !member.IsCurrent() || !member.Actor.Session.Ready ||
                    member.Actor.Session.Phase != SessionPhase.Town ||
                    member.Actor.Session.Selected?.Position is not { Town: 59 } spot || spot.Area != landing.Side) == true) return;
            observed = arrival;
            superseded = requested?.DefenceAttempt;
            requested = new(view, actor, landing, party, battle.TrackPreyDefenceLanding(landing));
            if (!running) { running = true; pending = DrainAsync(); }
        }
        superseded?.Dispose();
    }

    async Task DrainAsync()
    {
        // Notice可来自owner或Battle同步发布；取连接门禁须离开原调用链。
        await Task.Yield();
        bool released = false;
        try
        {
            while (true)
            {
                Request request;
                lock (gate)
                {
                    if (disposed || requested is null) { running = false; released = true; return; }
                    request = requested; requested = null;
                }
                using var attempt = request.DefenceAttempt;
                using var commands = await DispatchGateScope.EnterAsync(context, stop.Token);
                context.DispatchGates = commands;
                if (!ReferenceEquals(context.Session.CurrentParty?.Invoke(), request.Party)) continue;
                using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, request.Party?.Members ?? [], stop.Token);
                if (!request.Actor.IsCurrent || context.Session.SelectionEpoch != request.Actor.Epoch ||
                    context.Session.CurrentRaidBattle(request.Actor) is not { } battle ||
                    !battle.IsPreyLandingCurrent(request.Landing) ||
                    request.Party?.Members.Any(member => !member.IsCurrent() || !member.Actor.Session.Ready ||
                        member.Actor.Session.Phase != SessionPhase.Town ||
                        member.Actor.Session.Selected?.Position is not { Town: 59 } spot || spot.Area != request.Landing.Side) == true) continue;
                await dispatch(request.View, request.Actor);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (InvalidOperationException) when (!context.Owner.IsCurrent) { }
        catch (Exception error)
        {
            try { failed?.Invoke(error); }
            finally { context.Disconnect(); }
        }
        finally
        {
            IDisposable? abandoned = null;
            if (!released) lock (gate) { running = false; abandoned = requested?.DefenceAttempt; requested = null; }
            abandoned?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task task; bool cancel; IDisposable? abandoned;
        lock (gate) { cancel = !disposed; disposed = true; abandoned = requested?.DefenceAttempt; requested = null; task = pending; }
        abandoned?.Dispose();
        try { if (cancel) await stop.CancelAsync(); await task; }
        finally { if (cancel) stop.Dispose(); }
    }
}
