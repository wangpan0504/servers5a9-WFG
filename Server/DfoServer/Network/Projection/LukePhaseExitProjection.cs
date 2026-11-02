using System.Data.Common;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 本连接一个退场执行器和一个待办槽；不拥有阶段时钟，不发未知失败状态或伪造死亡。
internal sealed class LukePhaseExitProjection(GameConnectionContext context, PartyDungeonService dungeons, TownProjection town, ServerLog log, ILukeRaidLifecycleStore? lifecycle,
    AntonRaidStart? raidStart = null) : IAsyncDisposable
{
    readonly object gate = new();
    readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
    Task pending = Task.CompletedTask;
    enum ExitReason { PhaseExpired, Energy, Encounter, PhaseCompleted }
    sealed record ExitRequest(AntonRaidBattleView View, RaidActor Actor, Guid? Run, ExitReason Reason);
    (Guid Instance, int Phase, Guid? Run, ExitReason Reason)? scheduled;
    ExitRequest? requested;
    bool running;
    bool disposed;
    internal Task Completion { get { lock (gate) return pending; } }

    internal void Notice(AntonRaidBattleView view, RaidActor actor)
    {
        var battle = context.Session.CurrentRaidBattle(actor);
        var reason = view.PhaseExpired ? ExitReason.PhaseExpired : ExitReason.Energy;
        Guid? run = view.PhaseExpired ? null : battle?.LukeEnergyReturnRun(actor);
        if (!view.PhaseExpired && context.Session.CurrentFormalDungeonScene()?.Snapshot.Identity.InstanceId is { } currentRun &&
            view.FiendwarExpiredRuns.Contains(currentRun)) { run = currentRun; reason = ExitReason.Encounter; }
        if (!view.PhaseExpired && battle?.RequiresLukeCompletedPhaseReturn(actor, view.Instance, view.Phase) == true)
        {
            run = context.Session.CurrentFormalDungeonScene()?.Snapshot.Identity.InstanceId;
            reason = ExitReason.PhaseCompleted;
        }
        if (!view.PhaseExpired && run is null && reason != ExitReason.PhaseCompleted) return;
        lock (gate)
        {
            var identity = (view.Instance, view.Phase, run, reason);
            if (disposed || scheduled == identity) return;
            scheduled = identity;
            requested = new(view, actor, run, reason);
            if (!running) { running = true; pending = DrainAsync(); }
        }
    }

    async Task DrainAsync()
    {
        // Notice可能来自本人owner或Battle发布；等待门禁必须离开该同步调用链。
        await Task.Yield();
        while (true)
        {
            ExitRequest request;
            lock (gate)
            {
                if (disposed || requested is null) { running = false; return; }
                request = requested;
                requested = null;
            }
            await ExitAsync(request);
        }
    }

    async Task ExitAsync(ExitRequest request)
    {
        var (view, actor, run, exitReason) = request;
        string reason = exitReason switch { ExitReason.PhaseCompleted => "completed", ExitReason.Encounter => "encounter",
            ExitReason.Energy => "energy", _ => "phase" };
        int failures = 0;
        bool returnCommitted = false;
        AntonRaidBattle? originalBattle = null;
        string prefix = "luke";
        while (!stop.IsCancellationRequested)
        {
            DispatchGateScope? gates = null;
            try
            {
                try
                {
                    try
                    {
                        gates = await DispatchGateScope.EnterAsync(context, stop.Token);
                        if (!actor.IsCurrent || context.Session.CurrentRaidBattle(actor) is not { } battle ||
                            battle.Instance != view.Instance ||
                            (exitReason == ExitReason.PhaseCompleted
                                ? !battle.RequiresLukeCompletedPhaseReturn(actor, view.Instance, view.Phase)
                                : run is { } runId ? !(battle.RequiresLukeEnergyReturn(actor, runId) ||
                                battle.Kind == RaidKind.Fiendwar && battle.View.FiendwarExpiredRuns.Contains(runId) &&
                                context.Session.CurrentFormalDungeonScene()?.Snapshot.Identity.InstanceId == runId) : !battle.View.PhaseExpired)) return;
                        originalBattle = battle;
                        prefix = battle.Kind.ToString().ToLowerInvariant();
                    }
                    // 只处理门禁/身份读取中的旧owner，不吞后续事务或发布的业务异常。
                    catch (InvalidOperationException) when (!context.Owner.IsCurrent) { return; }
                    if (context.Session.PartyDungeon is not { } instance)
                    {
                        if (run is null && context.Session.Phase == Game.Session.SessionPhase.MapSelect)
                        {
                            var returned = context.Session.ReturnFromDungeonSelection();
                            if (returned is null) { context.Disconnect(); return; }
                            returnCommitted = true;
                            using var publication = context.Sender.QueuePublication(() => actor.IsCurrent);
                            await town.ReturnFromFormalDungeonAsync(context,
                                new(returned.Character, returned.Generation, returned.SelectionEpoch), prefix + "_phase_selection_return");
                            if (exitReason == ExitReason.PhaseCompleted) context.Session.PublishRaidCombat();
                        }
                        LeaveExpiredAnton();
                        return;
                    }
                    // 回调只能退出捕获的原Run；选图或候战时排入的回调不能退出后来安装的新图。
                    if (run is { } expected ? instance.Id != expected : exitReason == ExitReason.PhaseCompleted) return;
                    using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, instance.Members, stop.Token);
                    var departure = dungeons.Depart(context.Session);
                    if (departure.Error != 0)
                    {
                        // 未决资产事务不能丢弃；断开原连接，由既有持久回执/实例清理路径收尾。
                        log.Write(prefix + "_phase_exit_failed", new { Connection = context.Id, view.Instance, view.Phase, Reason = "pending-operation" });
                        context.Disconnect();
                        return;
                    }
                    returnCommitted = true;
                    await PartyDungeonProjection.PublishAsync(departure.DungeonNotices);
                    LeaveExpiredAnton();
                    log.Write(prefix + "_" + reason + "_exit", new { Connection = context.Id, view.Instance, view.Phase, Run = run, Reason = "local-policy", State = "returned" });
                    return;
                }
                finally { gates?.Dispose(); }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { return; }
            catch (Exception error) when (!returnCommitted && lifecycle?.IsRetryable(error) == true)
            {
                failures++;
                if (failures <= 3 || failures % 32 == 0)
                    log.Write(prefix + "_phase_exit_retry", new { Connection = context.Id, view.Instance, view.Phase, Attempts = failures, Reason = error.GetType().Name });
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or DbException)
            {
                log.Write(prefix + "_phase_exit_failed", new { Connection = context.Id, view.Instance, view.Phase, Error = error.Message });
                if (exitReason == ExitReason.PhaseExpired && !returnCommitted && error is (DbException or InvalidDataException)) originalBattle?.FaultExpiredLukePhase(view.Instance);
                context.Disconnect();
                return;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures - 1, 5))), stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
        }

        void LeaveExpiredAnton()
        {
            if (exitReason != ExitReason.PhaseExpired || originalBattle?.Kind != RaidKind.Anton) return;
            if (raidStart?.LeaveExpiredAnton(actor, view.Instance) == true)
            {
                returnCommitted = true;
                log.Write("anton_phase_disband_member", new { Connection = context.Id, view.Instance, view.Phase, State = "left", Reason = "phase-timeout" });
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task task; bool cancel;
        lock (gate) { cancel = !disposed; disposed = true; task = pending; }
        try
        {
            if (cancel) await stop.CancelAsync();
            await task;
        }
        finally { if (cancel) stop.Dispose(); }
    }
}
