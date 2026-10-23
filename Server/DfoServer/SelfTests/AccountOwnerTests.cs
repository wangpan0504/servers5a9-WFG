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
        Console.WriteLine($"PASS {Count - checks} account owner assertions");
    }
}
