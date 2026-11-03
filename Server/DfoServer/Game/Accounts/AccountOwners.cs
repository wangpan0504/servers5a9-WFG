using DfoServer.Game.Caching;

namespace DfoServer.Game.Accounts;

internal enum OwnerRejectReason
{
    None,
    DuplicateAccountOwner
}

internal sealed class AccountOwners
{
    readonly object registryGate = new();
    readonly GameCache memory;
    readonly GameCacheIndex<string, AccountState> accounts;
    long generation;
    [ThreadStatic] static AccountState? executing;
    [ThreadStatic] static AccountState? pairFirst;
    [ThreadStatic] static AccountState? pairSecond;
    [ThreadStatic] static AccountState[]? executingGroup;

    public AccountOwners(GameCache? memory = null)
    {
        this.memory = memory ?? new GameCache();
        accounts = this.memory.CreateIndex<string, AccountState>();
    }

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
        if (executingGroup is { } group)
        {
            if (!group.Contains(state)) throw new InvalidOperationException("组合锁不能引入外部账号");
            return;
        }
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
        if (executing is not null || executingGroup is not null)
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

                // 旧租约仍为Current时结清在线段；失败不交出owner，也不执行断线或网络回调。
                old?.BeforeReplacement?.Invoke();
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
        internal GameCacheScope RuntimeCache { get; } = owner.memory.CreateScope();
        public string Account => account;
        public long Generation => generation;
        internal GameCacheScope CreateCacheScope() => owner.memory.CreateScope();
        // 仅供发布可见性查询；业务写入仍必须使用 Execute，不可用此快照代替锁。
        public bool IsCurrent => !owner.memory.IsDisposed && ReferenceEquals(Volatile.Read(ref state.Current), this);

        AccountState State => state;
        AccountOwners Registry => owner;

        // 双账号交易唯一入口：锁按账号名排序，普通单锁不能升级，也不能引入第三账号。
        public static T ExecuteTogether<T>(Lease first, Lease second, Func<T> action)
        {
            if (executingGroup is not null) throw new InvalidOperationException("组合锁不能嵌入双账号事务");
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

        // 成员在锁外固定；重入只能复用原组合，禁止从单锁、双锁升级或临时扩充成员。
        public static T ExecuteGroup<T>(IReadOnlyList<(Lease Owner, ushort Character)> members, Func<T> action)
            => ExecuteFixedGroup(members, action, 1, 4);

        // 攻坚开战固定全团；普通副本的 ExecuteGroup 仍只接受一至四人，不能循环分队提交。
        internal static T ExecuteRaid<T>(IReadOnlyList<(Lease Owner, ushort Character)> members, Func<T> action)
            => ExecuteFixedGroup(members, action, 1, 20);

        // 卢克准备使用同一账号锁目录；上限来自已核资源且硬限八人，不放宽普通四人或安徒恩二十人上限。
        internal static T ExecuteRaidGroup<T>(IReadOnlyList<(Lease Owner, ushort Character)> members, int maximum, Func<T> action)
        {
            if (maximum is < 1 or > 8) throw new InvalidOperationException("卢克准备人数上限必须为一至八人");
            return ExecuteFixedGroup(members, action, 1, maximum);
        }

        // 可选的卢克断线捕获：零等待尝试完整原owner组合，忙/失效直接放弃，不改变既有阻塞入口。
        internal static bool TryExecuteLukeDisconnect<T>(IReadOnlyList<(Lease Owner, ushort Character)> members,
            Func<T> action, out T result)
        {
            result = default!;
            if (members.Count is < 1 or > 8 || members.Any(m => m.Character == 0) ||
                members.Select(m => m.Character).Distinct().Count() != members.Count ||
                members.Select(m => m.Owner.Account).Distinct(StringComparer.Ordinal).Count() != members.Count ||
                members.Any(m => !ReferenceEquals(m.Owner.Registry, members[0].Owner.Registry)))
                throw new InvalidOperationException("断线捕获需要同目录一至八个不同账号角色");
            if (executingGroup is not null || executing is not null || pairFirst is not null)
                throw new InvalidOperationException("断线捕获不能从已持owner锁升级或重入");
            var ordered = members.OrderBy(m => m.Owner.Account, StringComparer.Ordinal).ToArray();
            var states = ordered.Select(m => m.Owner.State).ToArray();
            int held = 0;
            try
            {
                foreach (var state in states)
                {
                    if (!Monitor.TryEnter(state.Gate)) return false;
                    held++;
                }
                if (ordered.Any(m => !m.Owner.IsCurrent)) return false;
                executingGroup = states;
                result = action();
                return true;
            }
            finally
            {
                executingGroup = null;
                for (int i = held - 1; i >= 0; i--) Monitor.Exit(states[i].Gate);
            }
        }

        static T ExecuteFixedGroup<T>(IReadOnlyList<(Lease Owner, ushort Character)> members, Func<T> action, int minimum, int maximum)
        {
            if (members.Count < minimum || members.Count > maximum || members.Any(m => m.Character == 0) ||
                members.Select(m => m.Character).Distinct().Count() != members.Count ||
                members.Select(m => m.Owner.Account).Distinct(StringComparer.Ordinal).Count() != members.Count ||
                members.Any(m => !ReferenceEquals(m.Owner.Registry, members[0].Owner.Registry)))
                throw new InvalidOperationException($"组合需要同一目录的{minimum}至{maximum}个不同账号和角色");
            var ordered = members.OrderBy(m => m.Owner.Account, StringComparer.Ordinal).ToArray();
            var states = ordered.Select(m => m.Owner.State).ToArray();
            if (executingGroup is { } active)
            {
                if (!active.SequenceEqual(states)) throw new InvalidOperationException("不能更换已锁定的组合");
                RequireCurrent();
                return action();
            }
            if (executing is not null || pairFirst is not null)
                throw new InvalidOperationException("不能从单账号或双账号锁升级到组合锁");
            int held = 0;
            try
            {
                foreach (var state in states) { Monitor.Enter(state.Gate); held++; }
                RequireCurrent();
                executingGroup = states;
                return action();
            }
            finally
            {
                executingGroup = null;
                for (int i = held - 1; i >= 0; i--) Monitor.Exit(states[i].Gate);
            }
            void RequireCurrent()
            {
                if (ordered.Any(m => !m.Owner.IsCurrent)) throw new InvalidOperationException("组合成员租约已失效");
            }
        }

        internal Action? BeforeReplacement { get; set; }

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
                if (ReferenceEquals(Volatile.Read(ref state.Current), this))
                    Volatile.Write(ref state.Current, null);
                owner.RemoveIdle(account, state);
                RuntimeCache.Dispose();
            }
        }
    }
}
