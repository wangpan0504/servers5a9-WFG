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
        // 定向入口也核对真实安徒恩资源合同；只复用本轮目录，不调用整套核心自测。
        var raids = RaidCatalog.Load(resources.Archive);
        AntonRaidResourceTests.RunResources(resources.Archive, raids, resources.ItemResources.Items);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "anton-tcp-probe"), resources: resources);
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "raid-start.json"),
            """{"StartMinimumOverride":1,"AntonFormalEntryEnabled":true,"AntonPhaseProgressEnabled":true}""");
        await fixture.StartAsync();
        string usage = await fixture.CliAsync(null, "--anton-probe");
        That(usage.Contains("不连接", StringComparison.Ordinal) && usage.Contains("--steps", StringComparison.Ordinal),
            "真实CLI默认只打印说明，不连接，列出步骤参数");
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
        var catalog = raids.PrepareResources(resources.Archive, main, resources.ItemResources.Items,
            antonMapPath: AntonRaidMapPaths.Bind(main.MapPath), antonMapPoolPaths: AntonRaidMapPaths.Paths).Find(0)!.Resources!.EntryCatalog!;
        That(catalog.Find(210) is not null && catalog.Ready && catalog.MissingMaps.IsEmpty, "探针使用与 Runtime 相同的已登记团本目录");
        using var peer = Open(fixture, "antonprobe", gamePort: endpoints[19], channelNumber: 19);
        peer.Select();
        Drain(peer);
        string assets = fixture.Snapshot("SELECT * FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')<>10096296 ORDER BY 1,2");
        string gold = fixture.Snapshot("SELECT id,gold FROM characters ORDER BY id");
        var report = AntonTcpProbe.Play(peer, catalog, AntonTcpProbe.StopAfterForSteps("start,enter,phase,reward,card"));
        That(report.Steps.SequenceEqual(new[] { "start", "enter", "phase", "reward", "card" }) && !report.CardUnavailable,
            "探针跑完五个步骤，正式资源奖励已提交并推进阶段1");
        That(report.RejectedCards == 2 && report.Opcode600 > 0 && report.Opcode601 == 1,
            "正式翻牌拒绝无配置kind/card；600展示冻结产物且先于ACK，601仅首开");
        That(assets != fixture.Snapshot("SELECT * FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')<>10096296 ORDER BY 1,2") &&
            gold == fixture.Snapshot("SELECT id,gold FROM characters ORDER BY id") &&
            fixture.Query("SELECT count(*),count(DISTINCT kind) FROM character_raid_reward_receipts") is [[2L, 2L]] &&
            fixture.Query("SELECT count(DISTINCT phase) FROM character_raid_reward_receipts") is [[1L]],
            "真实Reward实际发放两种独立资源奖励，重复请求不增回执，保留开战扣票");
        That(report.Started && report.Entered210 && report.Reward && report.Opcode586 > 0 &&
                report.Opcode596 > 0 && report.Opcode597 > 0 && report.Opcode601 == 1,
            "假客户端领取后进入休息并收到原596/597：" + report.Summary());
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
            events.Any(row => row.GetProperty("Kind").GetString() == "send" && row.GetProperty("Detail").GetProperty("Opcode").GetInt32() == 596) &&
            events.Any(row => row.GetProperty("Kind").GetString() == "send" && row.GetProperty("Detail").GetProperty("Opcode").GetInt32() == 597),
            "server.jsonl 有进图210、Reward和586，完整领取后有休息596/597");
        // 两种真实奖励均领取后进入休息态；计时按剩余值校验，不固定真实时钟秒数或重复投影次数。
        var stateAndTimers = events.Where(row => row.GetProperty("Kind").GetString() == "send" &&
            row.GetProperty("Detail").GetProperty("Kind").GetInt32() == 0 &&
            row.GetProperty("Detail").GetProperty("Opcode").GetInt32() is 587 or 596 or 597).ToArray();
        uint waitLimit = checked((uint)(catalog.Script.Headers.Single(row => row.Tag == "[phase break time]").Values.Single().Number - 5));
        string? state = null;
        byte[]? timer = null;
        uint previousSeconds = waitLimit;
        int pairs = 0;
        long previousSendAt = 0;
        foreach (var row in stateAndTimers)
        {
            var detail = row.GetProperty("Detail");
            int opcode = detail.GetProperty("Opcode").GetInt32();
            byte[] body = Convert.FromHexString(detail.GetProperty("Body").GetString()!);
            long sentAt = DateTimeOffset.Parse(row.GetProperty("Time").GetString()!).ToUnixTimeSeconds();
            if (opcode == 587)
            {
                That(timer is null, "休息计时596之后须先有597，不能跨状态配对");
                state = detail.GetProperty("Body").GetString();
            }
            else if (opcode == 596)
            {
                That(state == "0500" && timer is null && detail.GetProperty("Action").GetString() == "anton_wait_timer" &&
                    body.Length == 13 && body.AsSpan(0, 5).SequenceEqual(new byte[5]) &&
                    U32(body, 9) > 0 && U32(body, 9) <= previousSeconds,
                    "596只在587休息态/phase0后发送13字节待命计时，保留字段为零且不延长剩余时间");
                long preparedAt = (long)U32(body, 5) - U32(body, 9);
                That(preparedAt >= previousSendAt && preparedAt <= sentAt,
                    "596结束Unix秒减剩余秒落在前帧写后与本帧写后之间，不限制合法写入耗时");
                timer = body;
            }
            else
            {
                That(timer is not null && state == "0500" && detail.GetProperty("Action").GetString() == "anton_wait_remaining" &&
                    body.Length == 5 && body[0] == 1 && U32(body, 1) > 0 && U32(body, 1) <= U32(timer!, 9),
                    "597跟在本次596后，精确5字节、休息slot1，剩余秒不增大");
                previousSeconds = U32(body, 1);
                timer = null;
                pairs++;
            }
            previousSendAt = sentAt;
        }
        That(timer is null && pairs > 0 && pairs == report.Opcode596 && pairs == report.Opcode597,
            "真实收包与日志均有完整596/597待命计时对，不允许孤立计时或漏收");
        peer.Dispose();
        await fixture.StopAsync();
        await fixture.StartAsync("reopened");
        using var reopened = Open(fixture, "antonprobe", gamePort: ChannelTcpTests.Endpoints(fixture)[19], channelNumber: 19);
        reopened.Select();
        Drain(reopened);
        That(fixture.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296") is [[1L]] &&
            fixture.Query("SELECT count(*) FROM character_raid_reward_receipts") is [[2L]],
            "重建Host保留一张票和两份阶段0奖励回执，不将Reward当最终通关；不是跨进程战况恢复证明");
        var restarted = AntonTcpProbe.Play(reopened, catalog, "start");
        That(restarted.Started && fixture.Query("SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10096296") is [[0L]],
            "同角色重建Host后能重新开战，只扣保存的最后一张票");
    }
}
