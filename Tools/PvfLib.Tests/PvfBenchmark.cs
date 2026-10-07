using System.Diagnostics;
using System.Text.Json;

namespace PvfLib.Tests;

internal static class PvfBenchmark
{
    public static void Run(string path, int count)
    {
        PvfArchive archive = null!;
        try
        {
            Measure("open", () => archive = PvfArchive.Open(path));
            Measure("indexes", () =>
            {
                foreach (string file in new[] { "character/character.lst", "equipment/equipment.lst", "stackable/stackable.lst" })
                    _ = PvfScript.List(archive.ReadText(file));
            });
            string[] paths = [];
            Measure("select_files", () => paths = archive.Files.Where(file => file.DataType == 1 &&
                (file.Path.StartsWith("equipment/", StringComparison.Ordinal) || file.Path.StartsWith("stackable/", StringComparison.Ordinal)))
                .Select(file => file.Path).Order(StringComparer.Ordinal).Take(count).ToArray());
            long tokens = 0, characters = 0;
            foreach (string phase in new[] { "first_read", "repeat_read" })
                Measure(phase, () =>
                {
                    foreach (string file in paths)
                    {
                        tokens += archive.ReadTokens(file).Length;
                        characters += archive.ReadText(file).Length;
                    }
                });
            Console.WriteLine(JsonSerializer.Serialize(new { FilesRead = paths.Length, Tokens = tokens, TextCharacters = characters,
                archive.CachedChunkBytes, archive.CachedTextBytes }));
        }
        finally
        {
            archive?.Dispose();
        }
    }

    private static void Measure(string phase, Action action)
    {
        long allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        long retained = GC.GetTotalMemory(true);
        using var process = Process.GetCurrentProcess();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Phase = phase, ElapsedMs = watch.Elapsed.TotalMilliseconds, AllocatedBytes = bytes,
            RetainedManagedBytes = retained, PrivateBytes = process.PrivateMemorySize64,
            PeakWorkingSetBytes = process.PeakWorkingSet64
        }));
    }
}
