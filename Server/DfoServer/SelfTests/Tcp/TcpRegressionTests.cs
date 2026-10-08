using System.Text.Json;
using System.Diagnostics;

namespace DfoServer.Testing;

internal static class TcpRegressionTests
{
    static readonly (string Name, Func<ServerOptions, string, TcpTestResources, Task> Run)[] Groups =
    [
        ("commands", (options, root, _) => CommandLineTcpTests.RunAsync(options, root)),
        ("startup", (options, root, _) => StartupTcpTests.RunAsync(options, root)),
        ("packet-capture", (options, root, _) => PacketCaptureTcpTests.RunAsync(options, root)),
        ("town", (options, root, resources) => TownProtocolTcpTests.RunAsync(options, root, resources.Towns)),
        ("dungeon-probe", (options, root, _) => DungeonProbeTcpTests.RunAsync(options, root)),
        ("formal-dungeon", FormalDungeonTcpTests.RunAsync),
        ("dungeon-settlement", async (_, _, resources) =>
        {
            DungeonRoomCatalogTests.Run(resources.Archive);
            DungeonMonsterPlanTests.Run(resources.Archive);
            SinglePlayerDungeonRunTests.Run(resources.Archive);
            DungeonSettlementTests.RunResources(resources.Archive);
            DungeonSettlementSessionTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            await FormalDungeonSessionTests.RunAsync(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            await FormalDungeonPublicationTests.RunAsync(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
        }),
        ("jobs", (options, root, _) => TownProtocolTcpTests.JobsAsync(options, root)),
        ("inventory", InventoryTcpTests.RunAsync),
        ("inventory-persistence", (_, _, resources) =>
        {
            Inventory.InventoryCommitTests.Run(resources.Towns, resources.Characters, resources.ItemResources);
            DatabaseSchemaTests.Run();
            return Task.CompletedTask;
        }),
        ("skills", SkillProtocolTcpTests.RunAsync),
        ("special-containers", SpecialContainerTcpTests.RunAsync),
        ("guild-medals", GuildMedalTcpTests.RunAsync),
        ("currency", CurrencyTcpTests.RunAsync),
        ("cargo", CargoTcpTests.RunAsync),
        ("shop", ShopTcpTests.RunAsync),
        ("mailbox", MailboxTcpTests.RunAsync),
        ("gm-assets", DevelopmentAssetTcpTests.RunAsync),
        ("repair", RepairTcpTests.RunAsync),
        ("refinement", RefinementTcpTests.RunAsync),
        ("refinement-tickets", RefinementTicketTcpTests.RunAsync),
        ("disjoint", DisjointTcpTests.RunAsync),
        ("epic-book", EpicBookTcpTests.RunAsync),
        ("material-exchange", MaterialExchangeTcpTests.RunAsync),
        ("booster", BoosterTcpTests.RunAsync),
        ("booster-selection", BoosterSelectionTcpTests.RunAsync),
        ("booster-equipment", BoosterEquipmentTcpTests.RunAsync),
        ("authentication", (options, root, _) => AuthenticationTcpTests.RunAsync(options, root)),
        ("native-login", (options, root, _) => NativeLoginTcpTests.RunAsync(options, root)),
        ("settings", (options, root, _) => ClientSettingsTcpTests.RunAsync(options, root)),
        ("presence", (options, root, _) => TownPresenceTcpTests.RunAsync(options, root))
    ];

    public static IEnumerable<string> GroupNames => Groups.Select(group => group.Name);
    public static bool IsValidSelection(string selection)
    {
        string[] names = selection.Split(',');
        return names.Distinct(StringComparer.Ordinal).Count() == names.Length && names.All(GroupNames.Contains);
    }

    public static async Task RunAsync(ServerOptions options, string? selection = null)
    {
        if (selection is not null && !IsValidSelection(selection))
            throw new InvalidDataException("未知或重复的 TCP 测试组");
        string root = Path.GetFullPath(Path.Combine("build", "tcp-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        Console.WriteLine("C# TCP fixtures: " + root);
        using var resources = new TcpTestResources(options);
        string[] selected = selection?.Split(',') ?? GroupNames.ToArray();
        foreach (var group in Groups.Where(group => selected.Contains(group.Name, StringComparer.Ordinal)))
        {
            int before = TestCheck.Count;
            long allocated = GC.GetTotalAllocatedBytes();
            var watch = Stopwatch.StartNew();
            await group.Run(options, root, resources);
            using var process = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(root, group.Name + ".result.json"), JsonSerializer.Serialize(new
            {
                Passed = true, Assertions = TestCheck.Count - before, ElapsedMs = watch.ElapsedMilliseconds,
                DriverPrivateBytes = process.PrivateMemorySize64, DriverWorkingSetBytes = process.WorkingSet64,
                AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated, SharedResourcesLoaded = resources.IsLoaded
            }));
            Console.WriteLine($"PASS TCP {group.Name}: {TestCheck.Count - before} assertions");
        }
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { Passed = true, Assertions = TestCheck.Count, Groups = selected, Runtime = Environment.Version.ToString(), OS = Environment.OSVersion.ToString() }));
        Console.WriteLine($"PASS {TestCheck.Count} C# TCP assertions; {root}");
    }
}
