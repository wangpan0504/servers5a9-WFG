namespace DfoServer.Game.Accounts;

internal enum OwnerRejectReason
{
    None,
    DuplicateAccountOwner
}

internal sealed class AccountOwners
{
    readonly object registryGate = new();
    readonly Dictionary<string, AccountState> accounts = new(StringComparer.Ordinal);
    long generation;
    [ThreadStatic] static AccountState? executing;
    [ThreadStatic] static AccountState? pairFirst;
    [ThreadStatic] static AccountState? pairSecond;

    internal sealed class AccountState
    {
        internal readonly object Gate = new();
        internal Lease? Current;
        internal int Reservations;
    }

    internal int TrackedAccounts { get { lock (registryGate) return accounts.Count; } }

    void RemoveIdle(string account, AccountState state)
    {
        // 调用方已持账号锁；注册表锁内不等待账号锁，不执行外部回调。
        lock (registryGate)
            if (state.Current is null && state.Reservations == 0 &&
                accounts.TryGetValue(account, out var registered) && ReferenceEquals(registered, state))
                accounts.Remove(account);
    }

    static void CheckLockOrder(AccountState state)
    {
        if (executing is not null && !ReferenceEquals(executing, state) &&
            !ReferenceEquals(pairFirst, state) && !ReferenceEquals(pairSecond, state))
            throw new InvalidOperationException("Cannot nest different account locks");
    }

    public Lease Acquire(string account, Action disconnect)
    {
        // 仅保留离线工具及开发测试的兼容入口；在线调用必须显式选择策略。
        TryAcquire(account, disconnect, DuplicateLoginPolicy.ReplaceExisting, out var lease, out _);
        return lease!;
    }

    public bool TryAcquire(string account, Action disconnect, DuplicateLoginPolicy policy, out Lease? lease, out OwnerRejectReason reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentNullException.ThrowIfNull(disconnect);
        if (policy is not (DuplicateLoginPolicy.Reject or DuplicateLoginPolicy.ReplaceExisting))
            throw new ArgumentOutOfRangeException(nameof(policy));
        if (executing is not null)
            throw new InvalidOperationException("Cannot acquire ownership inside Lease.Execute");
        AccountState state;
        lock (registryGate)
        {
            if (!accounts.TryGetValue(account, out state!))
                accounts.Add(account, state = new());
            state.Reservations++;
        }
        Lease? old;
        Lease next;
        try
        {
            lock (state.Gate)
            {
                old = state.Current;
                if (old is not null && policy == DuplicateLoginPolicy.Reject)
                {
                    lease = null;
                    reason = OwnerRejectReason.DuplicateAccountOwner;
                    return false;
                }

                lock (registryGate)
                    next = new(this, state, account, checked(++generation), disconnect);
                Volatile.Write(ref state.Current, next);
            }
        }
        finally
        {
            lock (state.Gate)
            {
                lock (registryGate)
                    state.Reservations--;
                RemoveIdle(account, state);
            }
        }

        // 断开回调不得在 owner 锁内执行，旧 lease 的 Dispose 只能释放自身。
        try { old?.Disconnect(); }
        catch
        {
            // 回调失败时调用方拿不到新租约，不能留下无人释放的 owner。
            next.Dispose();
            throw;
        }
        lease = next;
        reason = OwnerRejectReason.None;
        return true;
    }

    internal sealed class Lease(AccountOwners owner, AccountState state, string account, long generation, Action disconnect) : IDisposable
    {
        public string Account => account;
        public long Generation => generation;
        // 仅供发布可见性查询；业务写入仍必须使用 Execute，不可用此快照代替锁。
        public bool IsCurrent => ReferenceEquals(Volatile.Read(ref state.Current), this);

        AccountState State => state;
        AccountOwners Registry => owner;

        // 双账号交易唯一入口：锁按账号名排序，普通单锁不能升级，也不能引入第三账号。
        public static T ExecuteTogether<T>(Lease first, Lease second, Func<T> action)
        {
            if (ReferenceEquals(first.State, second.State) || first.Account == second.Account ||
                !ReferenceEquals(first.Registry, second.Registry))
                throw new InvalidOperationException("双账号事务必须属于同一目录中的两个不同账号");
            if (pairFirst is not null)
            {
                if (!(ReferenceEquals(pairFirst, first.State) && ReferenceEquals(pairSecond, second.State) ||
                    ReferenceEquals(pairFirst, second.State) && ReferenceEquals(pairSecond, first.State)))
                    throw new InvalidOperationException("双账号事务不能嵌入第三账号");
                RequireCurrent();
                return action();
            }
            if (executing is not null) throw new InvalidOperationException("不能从单账号锁升级到双账号锁");
            var low = string.CompareOrdinal(first.Account, second.Account) < 0 ? first : second;
            var high = ReferenceEquals(low, first) ? second : first;
            lock (low.State.Gate)
            lock (high.State.Gate)
            {
                RequireCurrent();
                executing = pairFirst = low.State;
                pairSecond = high.State;
                try { return action(); }
                finally { executing = pairFirst = pairSecond = null; }
            }
            void RequireCurrent()
            {
                if (!first.IsCurrent || !second.IsCurrent) throw new InvalidOperationException("双账号事务租约已失效");
            }
        }

        internal void Disconnect() => disconnect();
        public T Execute<T>(Func<T> action)
        {
            CheckLockOrder(state);
            lock (state.Gate)
            {
                if (!IsCurrent)
                    throw new InvalidOperationException("Stale account generation");
                var previous = executing;
                executing = state;
                try { return action(); }
                finally { executing = previous; }
            }
        }

        public void Dispose()
        {
            CheckLockOrder(state);
            lock (state.Gate)
            {
                if (IsCurrent)
                    Volatile.Write(ref state.Current, null);
                owner.RemoveIdle(account, state);
            }
        }
    }
}
