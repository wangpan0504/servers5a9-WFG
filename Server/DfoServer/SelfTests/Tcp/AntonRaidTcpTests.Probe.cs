using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class AntonRaidTcpTests
{
    // 进程内真实监听：频道 19 入口、单人开战、实机 25 字节 CMD16，以及阶段 0 的 586/Reward。
    public static async Task RunProbeAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "anton-tcp-probe"), resources: resources);
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "raid-start.json"),
            """{"StartMinimumOverride":1,"AntonFormalEntryEnabled":true,"AntonPhaseProgressEnabled":true}""");
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        int discovered = AntonTcpProbe.ResolveGamePort("127.0.0.1", fixture.Options.ChannelPort, 19);
        That(discovered == endpoints[19], "频道握手选出的 19 号入口就是本进程的分类监听");
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire("antonprobe", () => { }))
        {
            var point = new Location(38, 0, 629, 196);
            var candidate = CharacterCreation.Prepare("AntProbe", resources.Characters.Get(0), point, point, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 85, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(85) }, resources.Learning);
            var created = repository.Create(owner, candidate with
            {
                Level = 85, AcceptedStoryLevel = 85, CompletedQuests = [12829, 3779], InitialLearning = learning
            });
            var items = resources.ItemResources;
            InventoryService.Commit(repository, owner, created,
                new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(created, [new(10096296, 2)]));
        }
        var main = SelfTestCatalogs.Dungeons(resources.Archive);
        var catalog = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive, main, resources.ItemResources.Items,
            antonMapPath: AntonRaidMapPaths.Bind(main.MapPath)).Find(0)!.Resources!.EntryCatalog!;
        That(catalog.Find(210) is not null && catalog.Ready && catalog.MissingMaps.IsEmpty, "探针使用与 Runtime 相同的已登记团本目录");
        using var peer = Open(fixture, "antonprobe", gamePort: endpoints[19], channelNumber: 19);
        peer.Select();
        Drain(peer);
        var report = AntonTcpProbe.Play(peer, catalog);
        That(report.Started && report.Entered210 && report.Reward && report.Opcode586 > 0 && report.Opcode596 == 0 && report.Opcode597 == 0,
            "假客户端跑完开战、210 和 Reward：" + report.Summary());
        That(fixture.Query("SELECT town, area FROM characters WHERE name='AntProbe'") is [[19L, 2L]] &&
            fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296") is [[1L]],
            "开战扣一张门票并留在 19/2，进图不再扣票");
        var events = fixture.Events();
        var started = events.Where(row => row.GetProperty("Kind").GetString() == "raid_start_request")
            .Select(row => row.GetProperty("Detail")).Where(row => row.GetProperty("Accepted").GetBoolean()).ToArray();
        That(started.Length == 1 && started[0].GetProperty("MissingFormalDungeons").GetInt32() == 14 &&
            started[0].GetProperty("MissingMaps").GetInt32() == 0 &&
            started[0].GetProperty("FormalDungeonSample").GetString() == "210,211,212,213,214,215,216,218",
            "主表缺的 14 个正式地下城含 210–216 与 218，不挡住已登记目录上的开战");
        That(events.Any(row => row.GetProperty("Kind").GetString() == "formal_dungeon_entry" &&
                row.GetProperty("Detail").GetProperty("Dungeon").GetInt32() == 210 &&
                row.GetProperty("Detail").GetProperty("Accepted").GetBoolean()) &&
            events.Any(row => row.GetProperty("Kind").GetString() == "anton_phase_progress" &&
                row.GetProperty("Detail").GetProperty("PhaseSuccess").GetInt32() == 1 &&
                row.GetProperty("Detail").GetProperty("State").GetString() == "Reward") &&
            events.Any(row => row.GetProperty("Kind").GetString() == "send" && row.GetProperty("Detail").GetProperty("Opcode").GetInt32() == 586) &&
            !events.Any(row => row.GetProperty("Kind").GetString() == "send" && row.GetProperty("Detail").GetProperty("Opcode").GetInt32() is 596 or 597),
            "server.jsonl 有进图 210、anton_phase_progress Reward 和 586，没有 596/597");
    }
}
