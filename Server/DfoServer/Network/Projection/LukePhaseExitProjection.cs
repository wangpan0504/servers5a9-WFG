using System.Data.Common;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Infrastructure;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 本连接一个退场执行器和一个待办槽；不拥有阶段时钟，不发未知失败状态或伪造死亡。
internal sealed class LukePhaseExitProjection(GameConnectionContext context, PartyDungeonService dungeons, TownProjection town, ServerLog log, ILukeRaidLifecycleStore? lifecycle) : IAsyncDisposable
{
    readonly object gate = new();
    readonly CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
    Task pending = Task.CompletedTask;
    sealed record ExitRequest(AntonRaidBattleView View, RaidActor Actor, Guid? Run);
    (Guid Instance, int Phase, Guid? Run)? scheduled;
    ExitRequest? requested;
    bool running;
    bool disposed;
    internal Task Completion { get { lock (gate) return pending; } }

    internal void Notice(AntonRaidBattleView view, RaidActor actor)
    {
        Guid? run = view.PhaseExpired ? null : context.Session.CurrentRaidBattle(actor)?.LukeEnergyReturnRun(actor);
        if (!view.PhaseExpired && context.Session.CurrentFormalDungeonScene()?.Snapshot.Identity.InstanceId is { } currentRun &&
            view.FiendwarExpiredRuns.Contains(currentRun)) run = currentRun;
        if (!view.PhaseExpired && run is null) return;
        lock (gate)
        {
            var identity = (view.Instance, view.Phase, run);
            if (disposed || scheduled == identity) return;
            scheduled = identity;
            requested = new(view, actor, run);
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
        var (view, actor, run) = request;
        string reason = run is null ? "phase" : view.Fiendwar is not null ? "encounter" : "energy";
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
                            (run is { } runId ? !(battle.RequiresLukeEnergyReturn(actor, runId) ||
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
                        }
                        return;
                    }
                    // 能源回调只能退当时被标记的原Run；旧回调不能退出后来安装的新图。
                    if (run is { } expected && instance.Id != expected) return;
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
                if (run is null && !returnCommitted && error is (DbException or InvalidDataException)) originalBattle?.FaultExpiredLukePhase(view.Instance);
                context.Disconnect();
                return;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures - 1, 5))), stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
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
