using System.Diagnostics;

namespace DfoServer.Infrastructure;

// 仅供启动装配中的固定低频阶段使用；不采样后台线程、不强制GC、不记录资源内容。
internal static class StartupStage
{
    internal static T Run<T>(ServerLog log, string stage, Func<T> load)
    {
        var before = Snapshot();
        log.Write("startup", new { Stage = stage, State = "starting", Metrics = before });
        long started = Stopwatch.GetTimestamp();
        T result = load(); // 原异常直接传播；失败不能记录complete。
        var after = Snapshot();
        log.Write("startup", new { Stage = stage, State = "complete",
            ElapsedMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3), Metrics = after,
            CpuMs = after.CpuMs - before.CpuMs,
            GcPauseMs = after.GcPauseMs - before.GcPauseMs, AllocatedBytes = after.AllocatedBytes - before.AllocatedBytes,
            Gen0 = after.Gen0 - before.Gen0, Gen1 = after.Gen1 - before.Gen1, Gen2 = after.Gen2 - before.Gen2 });
        return result;
    }

    internal sealed record Metrics(double? CpuMs, long? WorkingSetBytes, long? PrivateBytes,
        int Gen0, int Gen1, int Gen2, double GcPauseMs, long AllocatedBytes, string? ProcessError);

    // 进程指标取不到时保留未知，不能让附加诊断改变原资源装配结果。
    internal static Metrics Snapshot()
    {
        double? cpu = null;
        long? working = null, privateBytes = null;
        string? error = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            cpu = process.TotalProcessorTime.TotalMilliseconds;
            working = process.WorkingSet64;
            privateBytes = process.PrivateMemorySize64;
        }
        catch (Exception e) { error = e.GetType().Name; }
        return new(cpu, working, privateBytes, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            GC.GetTotalPauseDuration().TotalMilliseconds, GC.GetTotalAllocatedBytes(false), error);
    }
}
