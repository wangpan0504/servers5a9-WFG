using System.Diagnostics;
using System.Text.Json;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    sealed record PromotionLatency(int Index, double PlannedMilliseconds, double ActualMilliseconds, double DebtMilliseconds);

    // 单轮有界固定计划：预热2分钟，正式5分钟；每10秒一辆64行满包车，另账号每秒2次真实背包移动。
    static async Task<int> PromotionCostAsync(TcpTestFixture fixture, TcpPeer buyer)
    {
        That(!System.Diagnostics.Debugger.IsAttached, "成本测量不挂调试器");
        string output = Environment.GetEnvironmentVariable("DFO_PROMOTION_COST_OUTPUT") ??
            throw new InvalidOperationException("正式成本测量需显式指定仓库外结果路径");
        bool smoke = Environment.GetEnvironmentVariable("DFO_PROMOTION_COST_SMOKE") == "1";
#if DEBUG
        if (!smoke) throw new InvalidOperationException("正式成本测量必须使用Release；Debug仅允许有界驱动冒烟");
#endif
        using var other = Open(fixture, "20001"); other.Select();
        var cart = Cart([.. Enumerable.Repeat(105681, 63), 102925]);
        int cartCount = smoke ? 6 : 42, moveCount = smoke ? 12 : 840;
        var purchases = new List<PromotionLatency>(cartCount);
        var following = new List<PromotionLatency>(cartCount);
        var moves = new List<PromotionLatency>(moveCount);
        long origin = Stopwatch.GetTimestamp();
        long allocated = GC.GetTotalAllocatedBytes();
        long startPrivate;
        using (var process = Process.GetCurrentProcess()) startPrivate = process.PrivateMemorySize64;
        var watch = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource();
        int attemptedPurchases = 0, attemptedFollowing = 0, attemptedMoves = 0;
        int purchaseIndex = -1, moveIndex = -1;
        string stage = "purchase";
        Exception? failure = null, moveFailure = null, cleanupFailure = null;
        bool moverStopped = false;
        string? fault = smoke ? Environment.GetEnvironmentVariable("DFO_PROMOTION_COST_FAULT") : null;
        var mover = Task.Run(async () =>
        {
            try
            {
                for (int index = 0; index < moveCount; index++)
                {
                    double planned = index * 500;
                    await WaitFor(planned);
                    moveIndex = index;
                    Interlocked.Increment(ref attemptedMoves);
                    if (fault == "mover" && index == 2) other.Dispose();
                    short source = (short)(index % 2 == 0 ? 65 : 67), target = (short)(index % 2 == 0 ? 67 : 65);
                    var body = Build(writer =>
                    {
                        writer.Write((byte)0); writer.Write(source); writer.Write(2058); writer.Write(0);
                        writer.Write((byte)0); writer.Write(target); writer.Write(0); writer.Write(0); writer.Write(-1); writer.Write((ushort)0);
                    });
                    double actual = Elapsed();
                    other.Send(19, body);
                    other.Expect(1, 19, Build(writer =>
                    { writer.Write((byte)1); writer.Write((byte)0); writer.Write(source); writer.Write(10); writer.Write((byte)0); writer.Write(target); }));
                    double done = Elapsed();
                    lock (moves) moves.Add(new(index, done - planned, done - actual, Math.Max(0, actual - planned)));
                }
            }
            catch (Exception error) when (!cancellation.IsCancellationRequested)
            { moveFailure = error; throw; }
        });
        try
        {
            for (int index = 0; index < cartCount; index++)
            {
                double planned = index * (smoke ? 1000 : 10_000);
                await WaitFor(planned);
                stage = "purchase";
                purchaseIndex = index;
                attemptedPurchases++;
                if (fault == "buyer" && index == 2) buyer.Dispose();
                double actual = Elapsed();
                buyer.Send(64, cart);
                double followActual = Elapsed();
                attemptedFollowing++;
                buyer.SendBarrier();
                for (int row = 0; row < 64; row++)
                    That(U32(buyer.Expect(1, 64).Body, 6) == (row == 63 ? 102925 : 105681), "成本轮逐行保留64个原ACK");
                var packet = buyer.Read();
                if (packet is { Kind: 0, Opcode: 14 }) packet = buyer.Read();
                That(packet is { Kind: 0, Opcode: 53 }, "成本轮保留真实余额投影");
                buyer.Expect(0, 99, [1, 0]);
                double bought = Elapsed();
                stage = "following";
                try { buyer.Expect(1, 1347, [1]); }
                catch
                {
                    purchases.Add(new(index, bought - planned, bought - actual, Math.Max(0, actual - planned)));
                    throw;
                }
                double done = Elapsed();
                purchases.Add(new(index, bought - planned, bought - actual, Math.Max(0, actual - planned)));
                following.Add(new(index, done - planned, done - followActual, Math.Max(0, followActual - planned)));
                stage = "memory";
                using var process = Process.GetCurrentProcess();
                That(process.PrivateMemorySize64 < 4L * 1024 * 1024 * 1024 && watch.Elapsed < TimeSpan.FromMinutes(8),
                    "单进程成本轮遵守4GiB和8分钟边界");
            }
            stage = "mover";
            await mover.WaitAsync(TimeSpan.FromSeconds(20));
            moverStopped = true;
            stage = "barrier";
            other.Barrier(); buyer.Barrier();
            stage = "budget";
            var measuredPurchases = purchases.Skip(smoke ? 2 : 12).ToArray();
            var measuredFollowing = following.Skip(smoke ? 2 : 12).ToArray();
            var measuredMoves = moves.Skip(smoke ? 4 : 240).ToArray();
            That(measuredPurchases.All(sample => sample.PlannedMilliseconds <= 500), "正式满包64行整车完整响应在500ms内");
            That(Percentile(measuredMoves.Select(sample => sample.PlannedMilliseconds), 0.95) <= 50 &&
                Percentile(measuredMoves.Select(sample => sample.PlannedMilliseconds), 0.99) <= 100,
                "另一账号真实背包移动P95≤50ms、P99≤100ms");
            That(measuredFollowing.All(sample => sample.PlannedMilliseconds <= 500), "同连接紧随请求没有超出低频整车预算");
            stage = "output";
            File.WriteAllText(output, JsonSerializer.Serialize(Result(), new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Promotion cost summary: " + JsonSerializer.Serialize(new
            { Purchase = Summarize(measuredPurchases), Following = Summarize(measuredFollowing), OtherAccount = Summarize(measuredMoves) }));
            return cartCount;
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            if (failure is not null)
            {
                // 解除同步读与固定计划等待；收尾错误不得覆盖原故障。
                try { cancellation.Cancel(); other.Dispose(); }
                catch (Exception error) { cleanupFailure = error; }
                try { await mover.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) { cleanupFailure ??= error; }
                moverStopped = mover.IsCompleted;
                try { File.WriteAllText(output + ".partial.json", JsonSerializer.Serialize(Result(), new JsonSerializerOptions { WriteIndented = true })); }
                catch (Exception error) { Console.Error.WriteLine("Promotion partial output failed: " + error); }
            }
        }

        object Result()
        {
            PromotionLatency[] moveSamples;
            lock (moves) moveSamples = moves.ToArray();
            using var final = Process.GetCurrentProcess();
            int failedPurchases = failure is not null && stage == "purchase" && attemptedPurchases > purchases.Count ? 1 : 0;
            int failedFollowing = failure is not null && stage == "following" && attemptedFollowing > following.Count ? 1 : 0;
            int failedMoves = moveFailure is not null ? 1 : 0;
            return new
            {
                Configuration = "Release required; hosted TCP; one process; 2m warmup + 5m measured; 64 lines (63 promotion + 102925 with 2 fixed bonuses); full main bag",
                Smoke = smoke, Passed = failure is null,
                Runtime = Environment.Version.ToString(), Environment.ProcessorCount,
                ElapsedMilliseconds = watch.ElapsedMilliseconds, StartPrivateBytes = startPrivate,
                EndPrivateBytes = final.PrivateMemorySize64, final.PeakWorkingSet64,
                AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                PlannedPurchases = cartCount, AttemptedPurchases = attemptedPurchases, CompletedPurchases = purchases.Count,
                PlannedFollowing = cartCount, AttemptedFollowing = attemptedFollowing, CompletedFollowing = following.Count,
                PlannedMoves = moveCount, AttemptedMoves = attemptedMoves, CompletedMoves = moveSamples.Length,
                Failed = failedPurchases + failedFollowing + failedMoves,
                FailedPurchases = failedPurchases, FailedFollowing = failedFollowing, FailedMoves = failedMoves,
                Unfinished = cartCount - purchases.Count + cartCount - following.Count + moveCount - moveSamples.Length,
                UnfinishedPurchases = cartCount - purchases.Count, UnfinishedFollowing = cartCount - following.Count,
                UnfinishedMoves = moveCount - moveSamples.Length,
                UnstartedPurchases = cartCount - attemptedPurchases, UnstartedMoves = moveCount - attemptedMoves,
                UnstartedFollowing = cartCount - attemptedFollowing,
                MoverStopped = moverStopped,
                Failure = failure is null ? null : new { Stage = stage, PurchaseIndex = purchaseIndex, MoveIndex = moveIndex,
                    Type = failure.GetType().Name, failure.Message, MoveFailure = moveFailure?.GetType().Name,
                    CleanupFailure = cleanupFailure?.GetType().Name },
                FirstPurchase = purchases.FirstOrDefault(), FirstMove = moveSamples.FirstOrDefault(),
                Purchase = Summarize(purchases.Skip(smoke ? 2 : 12).ToArray()),
                Following = Summarize(following.Skip(smoke ? 2 : 12).ToArray()),
                OtherAccount = Summarize(moveSamples.Skip(smoke ? 4 : 240).ToArray()),
                Purchases = purchases, FollowingRequests = following, OtherAccountRequests = moveSamples
            };
        }

        double Elapsed() => Stopwatch.GetElapsedTime(origin).TotalMilliseconds;
        async Task WaitFor(double planned)
        {
            double delay = planned - Elapsed();
            if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellation.Token);
        }
    }

    static object Summarize(PromotionLatency[] samples) => new
    {
        Count = samples.Length,
        Planned = Distribution(samples.Select(sample => sample.PlannedMilliseconds)),
        Actual = Distribution(samples.Select(sample => sample.ActualMilliseconds)),
        SendDebt = Distribution(samples.Select(sample => sample.DebtMilliseconds))
    };
    static object Distribution(IEnumerable<double> samples)
    {
        var rows = samples.ToArray();
        return new { P50 = rows.Length == 0 ? (double?)null : Percentile(rows, .5),
            P95 = rows.Length == 0 ? (double?)null : Percentile(rows, .95), P99 = rows.Length == 0 ? (double?)null : Percentile(rows, .99),
            Max = rows.Length == 0 ? (double?)null : rows.Max(), AtLeast100 = rows.Count(value => value >= 100), AtLeast250 = rows.Count(value => value >= 250),
            AtLeast1000 = rows.Count(value => value >= 1000) };
    }
    static double Percentile(IEnumerable<double> samples, double fraction)
    {
        var sorted = samples.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * fraction) - 1)];
    }
}
