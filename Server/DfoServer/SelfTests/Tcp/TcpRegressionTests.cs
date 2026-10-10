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
        ("town", TownProtocolTcpTests.RunAsync),
        ("quests", QuestLifecycleTcpTests.RunAsync),
        ("quest-continuation", QuestLifecycleTcpTests.RunContinuationAsync),
        ("story-digest", StoryDigestTcpTests.RunAsync),
        ("quest-manual-skip", (_, _, resources) => QuestManualSkipTests.RunAsync(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources, resources.Learning)),
        ("quest-ticket", (_, _, resources) => QuestTicketTests.RunAsync(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources,resources.Learning)),
        ("quest-specified-ticket", (_, _, resources) => QuestSpecifiedTicketTests.RunAsync(resources.Towns,resources.Archive,
            resources.Characters,resources.ItemResources,resources.Quests,resources.Learning)),
        ("quest-awakening-ticket", (_, _, resources) => QuestAwakeningTicketTests.RunAsync(resources.Towns,resources.Archive,
            resources.Characters,resources.ItemResources,resources.Quests,resources.Learning)),
        ("quest-play-only", (_, _, resources) => QuestPlayOnlyTests.RunAsync(resources.Towns,resources.Archive,
            resources.Characters,resources.ItemResources,resources.Quests,resources.Learning)),
        ("quest-auto-meet", (_, _, resources) => QuestAutoMeetTests.RunAsync(resources.Towns,resources.Archive,
            resources.Characters,resources.ItemResources,resources.Quests,resources.Learning)),
        ("quest-npc-mapping", (_, _, resources) =>
        {
            QuestNpcMappingTests.RunCore();
            QuestNpcMappingTests.RunResources(resources.Towns,resources.Quests);
            return Task.CompletedTask;
        }),
        ("quest-hidden", (_, _, resources) => QuestHiddenEntryTests.RunAsync(resources.Towns,resources.Archive,
            resources.Characters,resources.ItemResources,resources.Quests,resources.Learning)),
        ("quest-replay", (_, _, resources) => QuestReplayPublicationTests.RunAsync(resources.Archive,
            resources.Characters,resources.ItemResources,resources.Learning)),
        ("quest-initialization", (_, _, resources) =>
        {
            QuestDungeonClearProgressTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources);
            QuestDungeonClearProgressTests.RunInitialization(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources);
            return Task.CompletedTask;
        }),
        ("dungeon-story-events", (_, _, resources) => DungeonStoryEventPublicationTests.RunAsync(resources.Towns,resources.Archive,
            resources.Characters,resources.ItemResources,resources.Learning)),
        ("quest-core-rewards", async (_, _, resources) =>
        {
            QuestLifecycleTests.Run(resources.Towns, resources.ItemResources);
            QuestOrdinaryCompletionTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            QuestCharacterRewardTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            QuestTpRewardTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources, resources.Quests, resources.Learning);
            QuestThresholdRelationTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            await QuestCorePublicationTests.RunAsync(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources, resources.Quests, resources.Learning);
        }),
        ("dungeon-probe", DungeonProbeTcpTests.RunAsync),
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
        ("jobs", TownProtocolTcpTests.JobsAsync),
        ("inventory", InventoryTcpTests.RunAsync),
        ("inventory-persistence", (_, _, resources) =>
        {
            Inventory.InventoryCommitTests.Run(resources.Towns, resources.Characters, resources.ItemResources);
            DatabaseSchemaTests.Run();
            return Task.CompletedTask;
        }),
        ("resource-survey", (_, _, resources) =>
        {
            DungeonResourceSurveyTests.RunResources(resources.Archive);
            return Task.CompletedTask;
        }),
        ("skills", async (options, root, resources) =>
        {
            await SkillProtocolTcpTests.RunAsync(options, root, resources);
            await SkillExtendedTcpTests.RunAsync(options, root, resources);
        }),
        ("skill-slots", SkillQuickSlotsTcpTests.RunAsync),
        ("skill-material", SkillMaterialTcpTests.RunAsync),
        ("gm-progress", CharacterProgressTcpTests.RunAsync),
        ("special-containers", SpecialContainerTcpTests.RunAsync),
        ("guild-medals", GuildMedalTcpTests.RunAsync),
        ("currency", CurrencyTcpTests.RunAsync),
        ("cargo", CargoTcpTests.RunAsync),
        ("shop", ShopTcpTests.RunAsync),
        ("cash-shop", CashShopTcpTests.RunAsync),
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
        ("authentication", AuthenticationTcpTests.RunAsync),
        ("native-login", NativeLoginTcpTests.RunAsync),
        ("settings", ClientSettingsTcpTests.RunAsync),
        ("presence", TownPresenceTcpTests.RunAsync)
    ];

    public static IEnumerable<string> GroupNames => Groups.Select(group => group.Name);
    // 自测已有的定向入口和全量普查按需运行，完整 TCP 只执行网络集成。
    static IEnumerable<string> DefaultGroupNames => GroupNames.Where(name => name is not ("quest-continuation" or "quest-manual-skip" or "quest-ticket" or "quest-specified-ticket" or "quest-awakening-ticket" or "quest-play-only" or "quest-auto-meet" or "quest-npc-mapping" or "quest-hidden" or "quest-replay" or "quest-initialization" or "dungeon-story-events" or "quest-core-rewards" or "dungeon-settlement" or "inventory-persistence" or "resource-survey"));
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
        string[] selected = selection?.Split(',') ?? DefaultGroupNames.ToArray();
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
