using System.Diagnostics;
using System.Text.Json;

namespace DfoServer.Testing;

// settings 专用低频测量：父阶段包含快照子阶段，不能相加或把余项解释为 CPU。
internal sealed class ClientSettingsTimings
{
    readonly Dictionary<string, (int Calls, long Ticks)> values = new(StringComparer.Ordinal);
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
            owner.values[name] = (prior.Calls + 1, prior.Ticks + Stopwatch.GetTimestamp() - start);
        }
    }
    public void Write(string root, bool completed)
    {
        try
        {
            var stages = values.Select(pair => new { Stage = pair.Key, pair.Value.Calls,
                ElapsedMs = pair.Value.Ticks * 1000.0 / Stopwatch.Frequency }).ToArray();
            string text = JsonSerializer.Serialize(new { SchemaVersion = 1, Group = "settings", Completed = completed,
                Semantics = "inclusive-wall-time; snapshots are included in caller phases; not CPU or cache eligibility", Stages = stages });
            File.WriteAllText(Path.Combine(root, "settings.phases.json"), text);
            Console.WriteLine("PERF settings " + text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("WARN settings timing write failed: " + error.GetType().Name); }
    }
}
