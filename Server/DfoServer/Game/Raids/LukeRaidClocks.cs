namespace DfoServer.Game.Raids;

// 显式启用已支持的SET/RESET定时规则；原整数按秒解释，不开放未知界面或失败消费者。
internal sealed record LukeRaidClockPolicy(string Source);

// Runtime拥有，Anton复用同一有界所有者与退出回收；每实例至多一个停用/活跃timer和一个同步到期操作，不创建无界后台Task。
internal sealed class LukeRaidClocks(TimeProvider clock, ILukeRaidLifecycleStore store, Action<string, Exception> report,
    int capacity = 64, Action<AntonRaidBattleView>? rewardAbandoned = null,
    Func<AntonRaidBattle, Task>? settleLukeRewards = null, Func<AntonRaidBattle, bool>? rewardDue = null) : IAsyncDisposable
{
    readonly object gate = new();
    readonly Dictionary<Guid, Lease> leases = [];
    readonly Func<AntonRaidBattle, Task>? settleRewards = settleLukeRewards;
    bool stopped;

    internal Lease? Reserve(AntonRaidBattle battle)
    {
        lock (gate)
        {
            if (stopped || capacity is < 1 or > 64 || leases.Count >= capacity || leases.ContainsKey(battle.Instance)) return null;
            var lease = new Lease(this, battle, clock, store, report);
            leases.Add(battle.Instance, lease);
            return lease;
        }
    }

    void Remove(Lease lease)
    {
        lock (gate)
            if (leases.TryGetValue(lease.Battle.Instance, out var current) && ReferenceEquals(current, lease))
                leases.Remove(lease.Battle.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        Lease[] owned;
        lock (gate) { stopped = true; owned = leases.Values.ToArray(); leases.Clear(); }
        // 先取消全部实例，再逐个等待；不能因首实例SQL等待让其它实例继续到期。
        foreach (var lease in owned) lease.Dispose();
        // 不持注册表/账号/Battle锁等待已开始的SQL或发布回调。
        foreach (var lease in owned) await lease.DisposeAsync();
    }

    internal sealed class Lease : IDisposable, IAsyncDisposable
    {
        readonly LukeRaidClocks owner;
        readonly ITimer timer;
        readonly TimeProvider clock;
        long retryAnchor;
        TimeSpan? retryDelay;
        readonly ILukeRaidLifecycleStore store;
        readonly Action<string, Exception> report;
        int running;
        int callbacks;
        int stopped;
        int failures;
        readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal AntonRaidBattle Battle { get; }
        internal bool IsStopped => Volatile.Read(ref stopped) != 0;

        internal Lease(LukeRaidClocks owner, AntonRaidBattle battle, TimeProvider clock,
            ILukeRaidLifecycleStore store, Action<string, Exception> report)
        {
            this.owner = owner; this.clock = clock; Battle = battle; this.store = store; this.report = report;
            timer = clock.CreateTimer(static state => ((Lease)state!).Tick(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        // 仅由Battle gate内读取/更新；成员变化不能取消已密封结算的重试deadline。
        internal void SetRetry(TimeSpan? delay) { retryDelay = delay; retryAnchor = clock.GetTimestamp(); }
        internal TimeSpan? RetryRemaining => retryDelay is { } delay
            ? TimeSpan.FromTicks(Math.Max(0, (delay - clock.GetElapsedTime(retryAnchor)).Ticks)) : null;

        // 只在真实666提交并释放全团owner后激活；停用预留不开始倒计时。
        internal void Activate() => Battle.ActivateLukeClock(this);

        internal void Schedule(TimeSpan due)
        {
            if (IsStopped) return;
            try { timer.Change(due, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) when (IsStopped) { }
        }

        void Report(string stage, Exception error)
        {
            try { report(stage, error); }
            catch (Exception diagnostic) { error.Data["ClockDiagnosticFailure"] = diagnostic.GetType().Name; }
        }

        async void Tick()
        {
            Interlocked.Increment(ref callbacks);
            try { await RunTickAsync(); }
            catch (Exception error) { Report("callback", error); Dispose(); }
            finally
            {
                if (Interlocked.Decrement(ref callbacks) == 0 && IsStopped)
                { owner.Remove(this); drained.TrySetResult(); }
            }
        }

        async Task RunTickAsync()
        {
            if (IsStopped || Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
            try
            {
                if (IsStopped) return;
                bool changed;
                if ((Battle.Kind is RaidKind.Luke or RaidKind.Fiendwar || Battle.HasPreyTeamAutomaticRewards) && owner.settleRewards is { } settle &&
                    (Battle.DueLukeRewards().Length != 0 || Battle.FiendwarRewardExpired || Battle.PreyTeamRewardDue))
                {
                    var beforeReward = Battle.View;
                    try { await settle(Battle); }
                    catch (LukeRewardPublicationException error)
                    {
                        // 已提交资产与阶段推进不因展示失败倒退；仍发布下方最终战况。
                        Report("luke-auto-reward-publish", error);
                    }
                    catch (Exception error)
                    { failures = Math.Min(failures + 1, 30); Report("luke-auto-reward", error); return; }
                    // 普雷P1的最后一笔也可能由离线恢复提交；完整表就绪不一定改变view，仍须唤醒结果投影。
                    if (!ReferenceEquals(beforeReward, Battle.View) || Battle.HasPreyTeamAutomaticRewards && beforeReward.Phase == 1)
                        Battle.Publish(Battle.View, error => Report("recipient", error));
                }
                bool waitingForReward = Battle.Kind == RaidKind.Anton && Battle.HasAntonRewards &&
                    Battle.View.State == AntonRaidBattleState.Reward;
                // 发奖不持Battle锁；在线成员复用原资格和资产事务，失败保留Reward等待重试。
                try
                {
                    if (Battle.AntonAutoRewardDue && !owner.DeliverDueRewards(Battle))
                    { failures = Math.Min(6, failures + 1); return; }
                }
                catch (Exception error) when (store.IsRetryable(error))
                { failures = Math.Min(6, failures + 1); Report("anton-auto-reward", error); return; }
                catch (Exception error)
                { Battle.FaultLukeClock(this); Report("anton-auto-reward-fault", error); Dispose(); return; }
                try { changed = Battle.PrepareLukeNextPhase() | Battle.ExpireDueLukeClock(this); }
                catch (Exception error)
                {
                    // 解释器错误不能冒充正常进行中的攻坚，也不猜测原生失败/退款通知。
                    Battle.FaultLukeClock(this); Report("rule", error); Dispose(); return;
                }
                if (!IsStopped && Battle.PendingLukeSettlement() is { } candidate)
                {
                    LukePhaseCommit receipt;
                    try { receipt = store.CommitLukePhase(candidate); }
                    catch (Exception error) when (store.IsRetryable(error))
                    {
                        failures = failures == int.MaxValue ? failures : failures + 1;
                        if (failures <= 3 || failures % 32 == 0) Report("settlement", error);
                        return;
                    }
                    catch (Exception error)
                    { Battle.FaultLukeClock(this); Report("settlement-fault", error); Dispose(); return; }
                    try { changed |= Battle.AcceptLukeSettlement(candidate, receipt) is not null; }
                    catch (Exception error)
                    { Battle.FaultLukeClock(this); Report("receipt", error); Dispose(); return; }
                }
                if (!IsStopped && Battle.PendingAntonSettlement() is not null)
                {
                    try
                    {
                        if (store is not IAntonRaidLifecycleStore antonStore) throw new InvalidOperationException("安徒恩时钟缺少阶段存储");
                        changed |= Battle.CommitPendingAntonSettlement(antonStore);
                    }
                    catch (Exception error) when (store.IsRetryable(error))
                    { failures = failures == int.MaxValue ? failures : failures + 1; Report("anton-settlement", error); return; }
                    catch (Exception error)
                    { Battle.FaultLukeClock(this); Report("anton-settlement-fault", error); Dispose(); return; }
                }
                if (!IsStopped && Battle.PendingFiendwarFailure() is { } fiendwar)
                {
                    try
                    {
                        if (store is not IFiendwarFailureStore failureStore) throw new InvalidOperationException("漩涡时钟缺少失败退票存储");
                        failureStore.CommitFiendwarFailure(fiendwar);
                        Battle.AcceptFiendwarFailure(fiendwar); changed = true;
                    }
                    catch (Exception error) when (store.IsRetryable(error))
                    { failures = failures == int.MaxValue ? failures : failures + 1; Report("fiendwar-refund", error); return; }
                    catch (Exception error)
                    { Battle.FaultLukeClock(this); Report("fiendwar-refund-fault", error); Dispose(); return; }
                }
                failures = 0;
                if (changed && waitingForReward && Battle.View.State != AntonRaidBattleState.Reward)
                    owner.NotifyRewardAbandoned(Battle.View);
                // 接管/慢连接失败只影响该接收者，不重放SQL，不把已提交奖励倒退为失败。
                if (!IsStopped && changed) Battle.Publish(Battle.View, error => Report("recipient", error));
            }
            finally
            {
                Volatile.Write(ref running, 0);
                if (!IsStopped)
                    Battle.ScheduleLukeClock(this, failures > 0 ? TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures - 1, 5))) : null);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            Battle.StopLukeClock(this);
            timer.Dispose();
            // 仍在回调时保留Runtime所有权，Host必须能等待它结束。
            if (Volatile.Read(ref callbacks) == 0) { owner.Remove(this); drained.TrySetResult(); }
        }

        public async ValueTask DisposeAsync()
        {
            Dispose();
            await timer.DisposeAsync();
            await drained.Task;
        }
    }

    bool DeliverDueRewards(AntonRaidBattle battle) => rewardDue?.Invoke(battle) ?? true;

    void NotifyRewardAbandoned(AntonRaidBattleView view) => rewardAbandoned?.Invoke(view);
}
