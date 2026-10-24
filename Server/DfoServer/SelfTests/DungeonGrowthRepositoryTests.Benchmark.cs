using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonGrowthRepositoryTests
{
    // 显式开启的仓储对照测量：不代表TCP响应、生产容量或稳定P99验收。
    static void RunReadBenchmark(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-growth-benchmark-" + Guid.NewGuid().ToString("N"));
        var learning = Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = Testing.SelfTestCatalogs.Growth(pvf);
        const int workers = 3, samples = 200;
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            var leases = Enumerable.Range(0, workers + 1).Select(i => owners.Acquire("growth-bench-" + i, () => { })).ToArray();
            try
            {
                var roles = leases.Select((owner, i) => repository.Create(owner,
                    SkillRepositoryTests.Fixture("ReadBench" + i, towns, characters, learning) with
                    {
                        Level = 95, InitialLearning = SkillRepositoryTests.Fixture("unused", towns, characters, learning).InitialLearning
                            with { Experience = growth.GetLevelStartExperience(95) },
                        ActiveMainQuests = [new(0,3145,1)], DungeonState = CharacterDungeonState.NewOrdinary,
                        Inventory = new(Enumerable.Range(0, 24).Select(at => new InventoryItem(0,
                            new(InventoryListType.Main, checked((short)(121 + at))), new(900001 + at, ItemKind.Material, 5))).ToImmutableArray())
                    })).ToArray();
                using var start = new Barrier(workers + 1);
                var tasks = Enumerable.Range(0, workers + 1).Select(worker => Task.Factory.StartNew(() =>
                {
                    var role = roles[worker];
                    var owner = leases[worker];
                    var times = new double[samples];
                    long allocations = 0;
                    start.SignalAndWait();
                    for (int at = 0; at < samples; at++)
                    {
                        // 每笔使用新实例，前置规划不计入仓储耗时，避免将fixture构造当成SQL成本。
                        var (run, authority) = CreateRun(role);
                        var pending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 20, 1)).Pending!;
                        var reward = new DungeonExperienceReward("benchmark-fixture", DungeonKillExperienceCatalog.ResourcePath,
                            pending.Monster.MonsterId, pending.Monster.CandidateLevel, 1);
                        long allocated = GC.GetAllocatedBytesForCurrentThread();
                        long began = Stopwatch.GetTimestamp();
                        if (worker == workers) role = repository.LoadCharacter(owner, role.Slot);
                        else role = repository.CommitDungeonGrowth(owner, role, run, pending, reward, growth).Character;
                        times[at] = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
                        allocations += GC.GetAllocatedBytesForCurrentThread() - allocated;
                    }
                    return (worker, role, times, allocations);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
                foreach (var task in tasks)
                {
                    var (worker, role, times, allocations) = task.Result;
                    That(JsonSerializer.Serialize(role) == JsonSerializer.Serialize(repository.LoadCharacter(leases[worker], role.Slot)),
                        "并发击杀与无关账号读取后返回快照和持久角色一致");
                    if (worker < workers) That(role.Revision == roles[worker].Revision + samples &&
                        role.DungeonState!.ProficiencyExperience == samples, "三账号竞争写入没有遗漏或重复成长");
                    var sorted = times.Skip(1).Order().ToArray();
                    double Percent(double fraction) => sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
                    Console.WriteLine("GROWTH_READ_BENCHMARK " + JsonSerializer.Serialize(new
                    {
                        Worker = worker, Action = worker == workers ? "unrelated-full-read" : "kill-commit", Samples = samples,
                        ColdMs = times[0], P50Ms = Percent(.50), P95Ms = Percent(.95), P99Ms = Percent(.99), MaxMs = times.Max(),
                        Over100Ms = times.Count(t => t >= 100), Over250Ms = times.Count(t => t >= 250), Over1000Ms = times.Count(t => t >= 1000),
                        AllocatedBytes = allocations, RetainedInventoryItems = role.Inventory.Items.Length,
                        Mode = "closed-loop repository-only; not network latency or capacity acceptance"
                    }));
                }
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
