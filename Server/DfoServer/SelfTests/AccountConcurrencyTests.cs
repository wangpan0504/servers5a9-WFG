using DfoServer.Game.Accounts;
using DfoServer.Testing;

namespace DfoServer;

internal static class AccountConcurrencyTests
{
    public static async Task RunAsync()
    {
        int before = TestCheck.Count;
        await IsolationAsync();
        await ContentionAsync();
        CallbackFailures();
        Console.WriteLine($"PASS {TestCheck.Count - before} 账号锁隔离、接管、回调与注册表回收断言");
    }

    static async Task IsolationAsync()
    {
        var owners = new AccountOwners();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var acquiring = new ManualResetEventSlim();
        bool finished = false, disconnectedAfter = false;
        using var first = owners.Acquire("first", () => disconnectedAfter = finished);
        using var other = owners.Acquire("other", () => { });
        Task blocked = Task.Run(() => first.Execute(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            TestCheck.That(first.IsCurrent, "接管等待原账号执行结束，不能中途撤销提交资格");
            finished = true;
            return true;
        }));
        TestCheck.That(entered.Wait(TimeSpan.FromSeconds(5)), "原账号进入同步工作");
        var takeover = Task.Factory.StartNew(() =>
        {
            acquiring.Set();
            return owners.Acquire("first", first.Dispose);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            TestCheck.That(acquiring.Wait(TimeSpan.FromSeconds(5)), "同账号接管开始竞争");
            await Task.Run(() =>
            {
                TestCheck.That(other.Execute(() => other.Execute(() => true)), "另一账号可执行且同租约支持重入");
                using var third = owners.Acquire("third", () => { });
                TestCheck.That(third.Execute(() => true), "接管等待不占用全局注册表");
                TestCheck.That(first.IsCurrent, "纯发布可见性检查不等待业务锁");
            }).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); }
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        using var next = await takeover.WaitAsync(TimeSpan.FromSeconds(5));
        TestCheck.That(disconnectedAfter && !first.IsCurrent && next.IsCurrent, "安装新代数前原工作已结束，随后断开旧连接");
        Reject(() => first.Execute(() => true));
        first.Dispose();
        TestCheck.That(next.Execute(() => true), "迟到Dispose不能释放新租约");
        next.Execute(() =>
        {
            Reject(() => other.Execute(() => true));
            Reject(other.Dispose);
            Reject(() => owners.Acquire("another", () => { }));
            TestCheck.That(next.Execute(() => true), "跨账号嵌套拒绝后仍能重入原账号");
            return true;
        });
        next.Dispose();
        other.Dispose();
        TestCheck.That(owners.TrackedAccounts == 0, "最后租约退出后注册表回收");
        using var fresh = owners.Acquire("first", () => { });
        first.Dispose();
        next.Dispose();
        TestCheck.That(fresh.IsCurrent && fresh.Generation > next.Generation, "锁条目回收再建不被旧引用清理或复活");
    }

    static async Task ContentionAsync()
    {
        var owners = new AccountOwners();
        int active = 0, maximum = 0, executed = 0;
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 150; i++)
            {
                using var lease = owners.Acquire("contended", () => { });
                try
                {
                    lease.Execute(() =>
                    {
                        int count = Interlocked.Increment(ref active);
                        maximum = Math.Max(maximum, count);
                        try
                        {
                            Thread.SpinWait(200);
                            TestCheck.That(lease.IsCurrent, "高竞争下执行期租约持续有效");
                            executed++;
                        }
                        finally { Interlocked.Decrement(ref active); }
                        return true;
                    });
                }
                catch (InvalidOperationException ex) when (ex.Message == "Stale account generation") { }
            }
        })).ToArray();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15));
        TestCheck.That(executed > 0 && maximum == 1 && active == 0, "同账号高竞争获取/释放不产生两把同时生效的锁");
        TestCheck.That(owners.TrackedAccounts == 0, "所有等待者与租约结束后无注册表残留");
        for (int i = 0; i < 1000; i++)
        {
            using var lease = owners.Acquire("transient-" + i, () => { });
            TestCheck.That(lease.IsCurrent, "临时账号正常获取");
        }
        TestCheck.That(owners.TrackedAccounts == 0, "历史账号不会累积永久锁条目");
    }

    static void CallbackFailures()
    {
        var owners = new AccountOwners();
        using var first = owners.Acquire("throwing", () => throw new IOException("callback failure"));
        try { owners.Acquire("throwing", () => { }); throw new Exception("Expected callback failure"); }
        catch (IOException) { }
        TestCheck.That(!first.IsCurrent && owners.TrackedAccounts == 0, "断开回调失败不留下调用方未取得的owner");
        AccountOwners.Lease? final = null;
        using var original = owners.Acquire("reentrant", () =>
        {
            final = owners.Acquire("reentrant", () => { });
            throw new IOException("after reentry");
        });
        try { owners.Acquire("reentrant", () => { }); throw new Exception("Expected callback failure"); }
        catch (IOException) { }
        TestCheck.That(final is not null && final.Execute(() => true), "失败清理不能删除回调重入安装的更晚租约");
        final!.Dispose();
        TestCheck.That(owners.TrackedAccounts == 0, "回调重入后精确回收");
    }

    static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { TestCheck.That(true, "拒绝失效租约或跨账号锁顺序"); return; }
        throw new Exception("Expected InvalidOperationException");
    }
}
