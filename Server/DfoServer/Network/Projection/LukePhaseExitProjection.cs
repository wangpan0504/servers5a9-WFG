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

    internal bool ReturnPending(Guid instance)
    { lock (gate) return running && scheduled is { } exit && exit.Instance == instance; }

    internal void Notice(AntonRaidBattleView view, RaidActor actor)
    {
        var battle = context.Session.CurrentRaidBattle(actor);
        var reason = view.PhaseExpired ? ExitReason.PhaseExpired : ExitReason.Energy;
        Guid? run = view.PhaseExpired ? null : battle?.LukeEnergyReturnRun(actor);
        if (view.PhaseExpired && battle is { Kind: RaidKind.Prey, PreyGuide: false })
        {
            if (context.Session.Phase == Game.Session.SessionPhase.Town && battle.LukeWaitingRoom is { } waiting &&
                context.Session.Selected?.Position is { } position && position.Town == waiting.Town && position.Area == waiting.Area &&
                context.Session.CurrentParty?.Invoke()?.PreyBattleId != view.Instance) return;
            run = context.Session.CurrentFormalDungeonScene()?.Snapshot.Identity.InstanceId;
        }
        if (!view.PhaseExpired && battle?.Kind == RaidKind.Fiendwar && context.Session.NeedsFiendwarFinalReturn())
            reason = ExitReason.Encounter;
        if (!view.PhaseExpired && context.Session.CurrentFormalDungeonScene()?.Snapshot.Identity.InstanceId is { } currentRun &&
            (view.FiendwarExpiredRuns.Contains(currentRun) || view.PreyExpelledRuns.Contains(currentRun) ||
                battle?.Kind == RaidKind.Fiendwar && view.Fiendwar is { Cleared: true }))
        { run = currentRun; reason = ExitReason.Encounter; }
        if (!view.PhaseExpired && battle?.RequiresLukeCompletedPhaseReturn(actor, view.Instance, view.Phase) == true)
        {
            run = context.Session.CurrentFormalDungeonScene()?.Snapshot.Identity.InstanceId;
            reason = ExitReason.PhaseCompleted;
        }
        if (!view.PhaseExpired && run is null && reason is not (ExitReason.PhaseCompleted or ExitReason.Encounter)) return;
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
            bool finished;
            lock (gate)
            {
                finished = requested is null;
                if (finished) running = false;
            }
            // Departure中的24已真正经原发送队列完成；此前同步Reward通知均由pending守卫拦住。
            // 无需重复退场或重提交终结，补发的只是当前原Battle展示。
            if (finished)
            {
                if (request.Actor.IsCurrent && context.Session.CurrentRaidBattle(request.Actor) is { } battle &&
                    (battle.Kind == RaidKind.Fiendwar || battle is { Kind: RaidKind.Prey, PreyGuide: false }))
                    context.Session.PublishRaidCombat();
                return;
            }
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
            bool leaveExpired = false;
            try
            {
                try
                {
                    try
                    {
                        gates = await DispatchGateScope.EnterAsync(context, stop.Token);
                        if (!actor.IsCurrent || context.Session.CurrentRaidBattle(actor) is not { } battle ||
                            battle.Instance != view.Instance ||
                            (exitReason == ExitReason.PhaseExpired
                                ? !battle.View.PhaseExpired || run is { } expiredRun &&
                                    context.Session.CurrentFormalDungeonScene()?.Snapshot.Identity.InstanceId != expiredRun
                                : exitReason == ExitReason.PhaseCompleted
                                ? !battle.RequiresLukeCompletedPhaseReturn(actor, view.Instance, view.Phase)
                                : run is { } runId ? !(battle.RequiresLukeEnergyReturn(actor, runId) ||
                                battle.Kind == RaidKind.Prey && battle.View.PreyExpelledRuns.Contains(runId) &&
                                    context.Session.CurrentFormalDungeonScene()?.Snapshot.Identity.InstanceId == runId ||
                                battle.Kind == RaidKind.Fiendwar && (battle.View.FiendwarExpiredRuns.Contains(runId) ||
                                    battle.View.Fiendwar is { Cleared: true }) &&
                                context.Session.CurrentFormalDungeonScene()?.Snapshot.Identity.InstanceId == runId) :
                                !(battle.View.PhaseExpired || exitReason == ExitReason.Encounter && context.Session.NeedsFiendwarFinalReturn()))) return;
                        originalBattle = battle;
                        prefix = battle.Kind.ToString().ToLowerInvariant();
                    }
                    // 只处理门禁/身份读取中的旧owner，不吞后续事务或发布的业务异常。
                    catch (InvalidOperationException) when (!context.Owner.IsCurrent) { return; }
                    if (context.Session.PartyDungeon is not { } instance)
                    {
                        if (run is null && exitReason == ExitReason.Encounter && context.Session.Phase == Game.Session.SessionPhase.Town)
                        {
                            var returned = context.Session.ReturnFromFiendwarBattlefield();
                            if (returned is null) return;
                            returnCommitted = true;
                            await town.ReturnFromFormalDungeonAsync(context, returned, "fiendwar_final_return");
                            context.Session.PublishRaidCombat();
                            return;
                        }
                        bool preyFinal = exitReason == ExitReason.PhaseCompleted &&
                            originalBattle is { Kind: RaidKind.Prey, PreyGuide: false } prey && view.Phase == 1 &&
                            view.Phase + 1 == prey.PhaseCount;
                        if (run is null && context.Session.Phase == Game.Session.SessionPhase.MapSelect)
                        {
                            // 末阶段选图者只消费原菜单，随后一次保存到备战区，不先中转甲板。
                            var returned = context.Session.ReturnFromDungeonSelection(restorePosition: !preyFinal);
                            if (returned is null) { context.Disconnect(); return; }
                            if (!preyFinal)
                            {
                                returnCommitted = true;
                                using var publication = context.Sender.QueuePublication(() => actor.IsCurrent);
                                await town.ReturnFromFormalDungeonAsync(context,
                                    new(returned.Character, returned.Generation, returned.SelectionEpoch), prefix + "_phase_selection_return");
                                if (exitReason is ExitReason.PhaseCompleted or ExitReason.Encounter) context.Session.PublishRaidCombat();
                            }
                        }
                        await CompleteWaitingAsync();
                        leaveExpired = true;
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
                    await CompleteWaitingAsync();
                    leaveExpired = true;
                    log.Write(prefix + "_" + reason + "_exit", new { Connection = context.Id, view.Instance, view.Phase, Run = run, Reason = "local-policy", State = "returned" });
                    return;
                }
                finally
                {
                    gates?.Dispose();
                    if (leaveExpired) await LeaveExpiredAntonAsync();
                }
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

        async Task CompleteWaitingAsync()
        {
            if (exitReason == ExitReason.PhaseCompleted && originalBattle is { Kind: RaidKind.Prey, PreyGuide: false } prey &&
                view.Phase == 1 && view.Phase + 1 == prey.PhaseCount && prey.LukeWaitingRoom is { } room)
            {
                bool arrived = context.Session.Selected?.Position is { } position && position.Town == room.Town && position.Area == room.Area;
                if (arrived && context.Session.CurrentParty?.Invoke()?.PreyBattleId != view.Instance) return;
                // 正式Run先完成原24；无Run成员先保存位置。两种情形均由同一实例协调者收尾临时Party。
                var waiting = raidStart?.ReturnPreyWaiting(actor, view.Instance, completedPhase: true);
                if (waiting is null) { context.Disconnect(); return; }
                returnCommitted = true;
                if (!arrived)
                {
                    using var publication = context.Sender.QueuePublication(() => actor.IsCurrent);
                    await town.ReturnFromFormalDungeonAsync(context, waiting, "prey_completed_waiting_return");
                }
                log.Write("prey_phase_waiting_return", new { Connection = context.Id, view.Instance, view.Phase,
                    State = "returned", Reason = "raid-completed" });
                return;
            }
            if (exitReason != ExitReason.PhaseExpired || originalBattle is not { Kind: RaidKind.Prey, PreyGuide: false }) return;
            if (raidStart?.ReturnPreyWaiting(actor, view.Instance) is { } returned)
            {
                returnCommitted = true;
                await town.ReturnFromFormalDungeonAsync(context, returned, "prey_failed_waiting_return");
                log.Write("prey_phase_waiting_return", new { Connection = context.Id, view.Instance, view.Phase, State = "returned", Reason = "raid-failed" });
            }
        }

        async Task LeaveExpiredAntonAsync()
        {
            if (exitReason != ExitReason.PhaseExpired || originalBattle?.Kind is not (RaidKind.Anton or RaidKind.Fiendwar)) return;
            if (originalBattle.Kind == RaidKind.Fiendwar)
            {
                // 失败状态必须在原团身份仍有效时写出；先脱团会使排队中的1523被旧实例守卫丢弃。
                using (var failureGates = await DispatchGateScope.EnterAsync(context, stop.Token))
                {
                    // 图内退场推进版本；同团其它成员退场不能撤销已发生的总期限失败。
                    var failed = originalBattle.View;
                    if (!actor.IsCurrent || !ReferenceEquals(context.Session.CurrentRaidBattle(actor), originalBattle) ||
                        failed.Instance != view.Instance || failed.Phase != view.Phase || !failed.PhaseExpired) return;
                    RaidCombatProjection.PostFiendwarState(context, failed, actor,
                        () => actor.IsCurrent && ReferenceEquals(context.Session.CurrentRaidBattle(actor), originalBattle) &&
                            originalBattle.View.PhaseExpired);
                }
                // 不持共享队伍门禁或其它成员命令门禁等待慢连接。
                await context.Sender.DrainAsync().WaitAsync(stop.Token);
            }
            using var leaveGates = await DispatchGateScope.EnterAsync(context, stop.Token);
            if (raidStart?.LeaveExpiredAnton(actor, view.Instance) == true)
            {
                returnCommitted = true;
                log.Write(originalBattle.Kind == RaidKind.Fiendwar ? "fiendwar_phase_disband_member" : "anton_phase_disband_member",
                    new { Connection = context.Id, view.Instance, view.Phase, State = "left", Reason = "phase-timeout" });
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
