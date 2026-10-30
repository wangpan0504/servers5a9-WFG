using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 仅显式诊断使用；每线程自有计数，PLINQ收束后读取，不创建采样线程或定时器。
internal sealed class ItemDropDiagnostics(Action<ItemDropDiagnostics.Report> report) : IDisposable
{
    internal sealed class Bucket
    {
        internal long Calls, ReadTicks, IndexTicks, ReadBytes, IndexBytes, ReadFailures, IndexFailures;
        internal void Add(PvfDocumentReadMetrics value)
        {
            Calls++;
            ReadTicks += value.ReadTicks; IndexTicks += value.IndexTicks;
            ReadBytes += value.ReadAllocatedBytes; IndexBytes += value.IndexAllocatedBytes;
            if (!value.ReadCompleted) ReadFailures++;
            else if (!value.IndexCompleted) IndexFailures++;
        }
    }
    internal sealed class Worker
    {
        internal readonly Bucket Root = new(), Import = new();
        internal long Processed, TemplateTicks, TemplateBytes;
        internal readonly Action<bool, PvfDocumentReadMetrics> Observe;
        internal Worker() => Observe = (imported, value) => (imported ? Import : Root).Add(value);
    }
    internal sealed record Phase(long Calls, double ReadMs, double IndexMs, long ReadAllocatedBytes,
        long IndexAllocatedBytes, long ReadFailures, long IndexFailures);
    internal sealed record Summary(int SchemaVersion, string Fields, string Encoding, string Algorithm, int Count, string Digest);
    internal sealed record Report(bool Completed, bool SnapshotReused, int Total, long Processed, int Workers,
        Phase Root, Phase Import, double TemplateResolutionMs, long TemplateResolutionAllocatedBytes,
        Summary? OrderedPositiveCandidates);
    readonly ThreadLocal<Worker> workers = new(() => new(), true);
    bool completed, disposed, reused;
    int total;
    Summary? summary;
    internal Worker Current => workers.Value!;
    internal void Start(int count) => total = count;
    internal void Complete() => completed = true;
    internal void Capture(IEnumerable<ItemTemplate> candidates, bool snapshotReused)
    {
        reused = snapshotReused;
        try { summary = Summarize(candidates); }
        catch (Exception) { summary = null; /* 摘要失败不能改变正式候选结果。 */ }
    }
    internal static Summary Summarize(IEnumerable<ItemTemplate> candidates)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("S5A9-positive-drop/v1\0"u8);
        Span<byte> row = stackalloc byte[20];
        int count = 0;
        foreach (var item in candidates)
        {
            BinaryPrimitives.WriteInt32LittleEndian(row, item.Id);
            BinaryPrimitives.WriteInt32LittleEndian(row[4..], (int)item.Kind);
            BinaryPrimitives.WriteInt32LittleEndian(row[8..], item.Grade);
            BinaryPrimitives.WriteInt32LittleEndian(row[12..], item.Rarity);
            BinaryPrimitives.WriteInt32LittleEndian(row[16..], item.CreationRate);
            hash.AppendData(row);
            count = checked(count + 1);
        }
        BinaryPrimitives.WriteInt32LittleEndian(row, count);
        hash.AppendData(row[..4]);
        return new(1, "Id,Kind,Grade,Rarity,CreationRate", "domain UTF-8; ordered int32 little-endian rows; trailing int32 count",
            "SHA-256", count, Convert.ToHexString(hash.GetHashAndReset()));
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            var values = workers.Values;
            Phase Sum(bool imported)
            {
                var parts = values.Select(worker => imported ? worker.Import : worker.Root).ToArray();
                return new(parts.Sum(p => p.Calls), Milliseconds(parts.Sum(p => p.ReadTicks)), Milliseconds(parts.Sum(p => p.IndexTicks)),
                    parts.Sum(p => p.ReadBytes), parts.Sum(p => p.IndexBytes), parts.Sum(p => p.ReadFailures), parts.Sum(p => p.IndexFailures));
            }
            report(new(completed, reused, total, values.Sum(w => w.Processed), values.Count, Sum(false), Sum(true),
                Milliseconds(values.Sum(w => w.TemplateTicks)), values.Sum(w => w.TemplateBytes), completed ? summary : null));
        }
        catch (Exception) { /* 汇总或日志失败不遮蔽原构建异常。 */ }
        finally { workers.Dispose(); }
    }
    static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
