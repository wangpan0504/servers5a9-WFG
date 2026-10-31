using System.Diagnostics;
using System.Text.Json;

namespace DfoServer.Testing;

// settings 专用低频测量：父阶段包含快照子阶段，不能相加或把余项解释为 CPU。
internal sealed class ClientSettingsTimings
{
    readonly Dictionary<string, (int Calls, long Ticks, long MinTicks, long MaxTicks)> values = new(StringComparer.Ordinal);
    public IDisposable Measure(string name) => new Sample(this, name);
    sealed class Sample(ClientSettingsTimings owner, string name) : IDisposable
    {
        readonly long start = Stopwatch.GetTimestamp();
        bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.values.TryGetValue(name, out var prior);
            long elapsed = Stopwatch.GetTimestamp() - start;
            owner.values[name] = (prior.Calls + 1, prior.Ticks + elapsed,
                prior.Calls == 0 ? elapsed : Math.Min(prior.MinTicks, elapsed), Math.Max(prior.MaxTicks, elapsed));
        }
    }
    public void Write(string root, bool completed)
    {
        try
        {
            var stages = values.Select(pair => new { Stage = pair.Key, pair.Value.Calls,
                ElapsedMs = pair.Value.Ticks * 1000.0 / Stopwatch.Frequency,
                MinMs = pair.Value.MinTicks * 1000.0 / Stopwatch.Frequency,
                MaxMs = pair.Value.MaxTicks * 1000.0 / Stopwatch.Frequency }).ToArray();
            string text = JsonSerializer.Serialize(new { SchemaVersion = 1, Group = "settings", Completed = completed,
                Semantics = "inclusive-wall-time; child stages and snapshots are included in caller phases; not CPU or cache eligibility", Stages = stages });
            File.WriteAllText(Path.Combine(root, "settings.phases.json"), text);
            Console.WriteLine("PERF settings " + text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("WARN settings timing write failed: " + error.GetType().Name); }
    }
}
