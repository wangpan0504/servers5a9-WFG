using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;

namespace DfoServer;

internal static class AccountOwnerTests
{
    public static void Run()
    {
        int checks = Count;

        var owners = new AccountOwners();
        int disconnects = 0;
        That(owners.TryAcquire("account-a", () => disconnects++, DuplicateLoginPolicy.Reject, out var first, out var reason) && reason == OwnerRejectReason.None, "first acquisition");
        That(!owners.TryAcquire("account-a", () =>
        {
        }, DuplicateLoginPolicy.Reject, out var rejected, out reason) && rejected is null && reason == OwnerRejectReason.DuplicateAccountOwner, "duplicate reject");
        That(first!.Execute(() => true) && disconnects == 0, "rejection preserves original owner");
        That(owners.TryAcquire("account-b", () =>
        {
        }, DuplicateLoginPolicy.Reject, out var other, out _), "independent account");
        That(other!.Account != "account-a" && other.Execute(() => true), "independent lease");
        That(owners.TryAcquire("account-a", () =>
        {
        }, DuplicateLoginPolicy.ReplaceExisting, out var replacement, out _) && disconnects == 1, "explicit replacement disconnects old owner");
        bool stale = false;
        try
        {
            first.Execute(() => true);
        }
        catch (InvalidOperationException)
        {
            stale = true;
        }

        That(stale && replacement!.Generation > first.Generation, "stale generation rejected");
        first.Dispose();
        That(replacement!.Execute(() => true), "stale dispose preserves replacement");
        replacement.Dispose();
        replacement.Dispose();
        other.Dispose();
        var contenders = new AccountOwners.Lease?[16];
        Parallel.For(0, contenders.Length, i => owners.TryAcquire("parallel", () =>
        {
        }, DuplicateLoginPolicy.Reject, out contenders[i], out _));
        That(contenders.Count(lease => lease is not null) == 1, "parallel rejection admits one owner");
        foreach (var lease in contenders)
            lease?.Dispose();
        using var a = owners.Acquire("pair-a", () => { });
        using var b = owners.Acquire("pair-b", () => { });
        using var c = owners.Acquire("pair-c", () => { });
        static bool Refuses(Action action)
        {
            try { action(); return false; }
            catch (InvalidOperationException) { return true; }
        }
        That(AccountOwners.Lease.ExecuteTogether(b, a, () => a.Execute(() => b.Execute(() =>
            AccountOwners.Lease.ExecuteTogether(a, b, () => true)))), "pair permits both leases and reversed pair reentry");
        That(Refuses(() => a.Execute(() => AccountOwners.Lease.ExecuteTogether(a, b, () => true))), "single lock cannot upgrade");
        That(Refuses(() => AccountOwners.Lease.ExecuteTogether(a, a, () => true)), "same account pair refused");
        That(AccountOwners.Lease.ExecuteTogether(a, b, () =>
            Refuses(() => c.Execute(() => true)) &&
            Refuses(() => AccountOwners.Lease.ExecuteTogether(a, c, () => true))), "pair refuses third account");
        That(Refuses(() => AccountOwners.Lease.ExecuteTogether(a, b, (Func<bool>)(() => throw new InvalidOperationException()))), "pair propagates failure");
        That(c.Execute(() => true) && AccountOwners.Lease.ExecuteTogether(a, b, () => true), "exception releases pair context");
        int paired = 0;
        var pairTasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (int n = 0; n < 100; n++)
                AccountOwners.Lease.ExecuteTogether(i % 2 == 0 ? a : b, i % 2 == 0 ? b : a, () => ++paired);
        })).ToArray();
        That(Task.WaitAll(pairTasks, TimeSpan.FromSeconds(10)) && paired == 800, "reversed concurrent pairs serialize without deadlock");
        using var newer = owners.Acquire("pair-a", () => { });
        That(Refuses(() => AccountOwners.Lease.ExecuteTogether(a, b, () => true)), "pair rejects replaced lease");
        using var foreign = new AccountOwners().Acquire("pair-other", () => { });
        That(Refuses(() => AccountOwners.Lease.ExecuteTogether(newer, foreign, () => true)), "pair rejects different owner registries");
        int customerPublications = 0, ownerPublications = 0;
        var failure = DfoServer.Game.ExpertJob.ExpertJobTradePublication.PublishAsync(
            () => { customerPublications++; throw new IOException("customer disconnected after commit"); },
            () => { ownerPublications++; return Task.CompletedTask; }).GetAwaiter().GetResult();
        That(failure is IOException && customerPublications == 1 && ownerPublications == 1,
            "顾客提交后断线仍尝试店主投影且不重跑事务");
        failure = DfoServer.Game.ExpertJob.ExpertJobTradePublication.PublishAsync(
            () => { customerPublications++; return Task.CompletedTask; },
            () => { ownerPublications++; throw new Microsoft.Data.Sqlite.SqliteException("busy after commit", 5); }).GetAwaiter().GetResult();
        That(failure is Microsoft.Data.Sqlite.SqliteException && customerPublications == 2 && ownerPublications == 2,
            "发布BUSY被隔离为发布故障不进入整请求重试");
        VerifyGroup();
        Console.WriteLine($"PASS {Count - checks} account owner assertions");
    }

    static void VerifyGroup()
    {
        var owners = new AccountOwners();
        var members = Enumerable.Range(0, 4).Select(i => (Owner: owners.Acquire("group-" + i, () => { }), Character: (ushort)(40 - i))).ToArray();
        using var outside = owners.Acquire("outside", () => { });
        try
        {
            bool Reject(Action action)
            {
                try { action(); return false; }
                catch (InvalidOperationException) { return true; }
            }
            That(AccountOwners.Lease.ExecuteGroup(members, () => members.All(m => m.Owner.Execute(() => true))),
                "四人组合按固定锁序进入，并允许组内账号重入");
            That(AccountOwners.Lease.ExecuteGroup(members, () => Reject(() => outside.Execute(() => true)) &&
                AccountOwners.Lease.ExecuteGroup(members.Reverse().ToArray(), () => true) &&
                Reject(() => AccountOwners.Lease.ExecuteGroup(members.Take(2).ToArray(), () => true)) &&
                Reject(() => owners.Acquire("nested", () => { }))), "组合仅允许同组重入，拒绝外部账号、改变成员及新owner");
            That(Reject(() => AccountOwners.Lease.ExecuteGroup(members, (Func<bool>)(() => throw new InvalidOperationException("planned")))) &&
                outside.Execute(() => true), "组合异常释放全部线程上下文和账号锁");
            int completed = 0;
            var workers = Enumerable.Range(0, 3).Select(i => Task.Run(() =>
            {
                var order = i == 0 ? members : members.Reverse().ToArray();
                for (int run = 0; run < 100; run++)
                {
                    if (i == 0) AccountOwners.Lease.ExecuteGroup(order, () => ++completed);
                    else if (i == 1) AccountOwners.Lease.ExecuteGroup(order, () => ++completed);
                    else AccountOwners.Lease.ExecuteTogether(members[1].Owner, members[0].Owner, () => ++completed);
                }
            })).ToArray();
            That(Task.WaitAll(workers, TimeSpan.FromSeconds(5)) && completed == 300,
                "账号名与角色ID顺序相反时，组合、组队双锁与副职业双锁并发不会死锁或丢失更新");
            using var replacement = owners.Acquire(members[2].Owner.Account, () => { });
            bool invoked = false;
            That(Reject(() => AccountOwners.Lease.ExecuteGroup(members, () => invoked = true)) && !invoked,
                "任一成员generation失效时不执行全队写入");
            That(Reject(() => AccountOwners.Lease.ExecuteGroup([members[0], members[0]], () => true)), "重复账号或角色不能组成组合锁");
        }
        finally { foreach (var member in members) member.Owner.Dispose(); }
    }
}
