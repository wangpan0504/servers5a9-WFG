using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FiendwarRaidTcpTests
{
    // 轮转真实迷宫，确保每种人数都经过光、暗及核心原资源；不依赖随机碰巧抽到目标房。
    sealed class MazeCoverageRandom : Random
    {
        int next;
        public override int Next(int maxValue) => maxValue > 0 ? next++ % maxValue : base.Next(maxValue);
    }
    internal static void VerifyConsumable(TcpTestFixture fixture, TcpPeer peer, bool accepted, uint check,
        int itemId = 10312385)
    {
        var source = fixture.Query("SELECT s.slot_index,i.item_uid,i.item_core FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) WHERE s.character_id=$p0 AND s.list_type=0 AND json_extract(i.item_core,'$.ItemId')=$p1", peer.Id, itemId).Single();
        ushort slot = Convert.ToUInt16(source[0]);
        long uid = Convert.ToInt64(source[1]);
        var before = System.Text.Json.JsonSerializer.Deserialize<ItemCore>((string)source[2])!;
        long audits = Convert.ToInt64(fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE character_id=$p0", peer.Id)[0][0]);
        long revision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id)[0][0]);
        peer.Send(44, Build(writer => { writer.Write(slot); writer.Write((byte)0); writer.Write(check); writer.Write(itemId); writer.Write(0); }));
        var rows = AntonTcpProbe.Barrier(peer, "fiendwar consumable");
        Bytes(rows.Single(row => row is { Kind: 1, Opcode: 44 }).Body,
            Build(writer => { writer.Write(accepted ? (byte)1 : (byte)0); writer.Write(accepted ? slot : (ushort)123);
                writer.Write((byte)0); writer.Write(check); writer.Write(itemId); }),
            "真实44完整回应物品使用，保留源槽和核验值");
        That(!rows.Any(row => row is { Kind: 0, Opcode: 14 }), "药品成功由ACK自扣，拒绝也不重复发送源14");
        var after = System.Text.Json.JsonSerializer.Deserialize<ItemCore>((string)fixture.Query(
            "SELECT item_core FROM character_inventory_items WHERE item_uid=$p0", uid)[0][0])!;
        That(after == (accepted ? before with { Value = before.Value - 1 } : before) &&
            Convert.ToInt64(fixture.Query("SELECT count(*) FROM inventory_audit_log WHERE character_id=$p0", peer.Id)[0][0]) == audits + (accepted ? 1 : 0),
            "药品只在合法场景实际扣原UID一瓶并提交一条审计，拒绝保留完整Core和审计");
        That(accepted || Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0", peer.Id)[0][0]) == revision,
            "使用拒绝不修改角色持久revision");
    }

    internal static void VerifyBlockedLegacyConsumable(TcpTestFixture fixture, TcpPeer peer)
    {
        var source = fixture.Query("SELECT s.slot_index,i.item_uid,i.item_core FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) WHERE s.character_id=$p0 AND s.list_type=0 AND json_extract(i.item_core,'$.ItemId')=7180", peer.Id).Single();
        string before = System.Text.Json.JsonSerializer.Serialize(fixture.Query(
            "SELECT revision,revive_coins,(SELECT count(*) FROM inventory_audit_log WHERE character_id=$p0) FROM characters WHERE id=$p0", peer.Id));
        peer.Send(30, BitConverter.GetBytes(Convert.ToUInt16(source[0])));
        var rows = AntonTcpProbe.Barrier(peer, "fiendwar legacy consumable");
        Bytes(rows.Single(row => row is { Kind: 1, Opcode: 30 }).Body, [0, 19, 0],
            "漩涡正式实例拒绝非44币券入口，不能绕过专用消耗品门禁");
        That(!rows.Any(row => row is { Kind: 0, Opcode: 14 }) &&
            Equals(fixture.Query("SELECT item_core FROM character_inventory_items WHERE item_uid=$p0", source[1])[0][0], source[2]) &&
            System.Text.Json.JsonSerializer.Serialize(fixture.Query(
                "SELECT revision,revive_coins,(SELECT count(*) FROM inventory_audit_log WHERE character_id=$p0) FROM characters WHERE id=$p0", peer.Id)) == before,
            "拒绝币券保留原UID/Core、复活币、revision和审计，不先发钱包或源物品通知");
    }

    internal static async Task RunBattlePassTeamDeathAsync(ServerOptions source, string root, TcpTestResources resources)
        => await RunBattlePassTeamFixtureAsync(source, root, resources, 0);

    internal static async Task RunBattlePassTeamBoundaryAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await RunBattlePassTeamFixtureAsync(source, Path.Combine(root, "rollback"), resources, 1);
        await RunBattlePassTeamFixtureAsync(source, Path.Combine(root, "writer"), resources, 2);
    }

    static async Task RunBattlePassTeamFixtureAsync(ServerOptions source, string root, TcpTestResources resources, int probeCase)
    {
        var start = DateTimeOffset.Parse("2026-10-29T06:00:00+08:00");
        var policy = new BattlePassMissionPolicy("fiendwar-team-death-fixture", start, BattlePassCatalog.Load(resources.Archive));
        var now = Enumerable.Range(0, policy.Days).Select(day => start.AddDays(day).AddHours(2))
            .First(at => policy.Active(at).Any(row => row.Key.Category == 0 && row.Key.Id == 1));
        var clock = new LukeRaidClockTests.Clock(now);
        source = source with { BattlePassSeasonKey = policy.SeasonKey, BattlePassSeasonStart = start };
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "fiendwar-team-death"), resources: resources,
            clock: clock, fiendwarMazeRandom: new MazeCoverageRandom());
        await fixture.StartAsync();
        var raid = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items).Find(2)!.Resources!;
        VerifyParties(fixture, resources, raid, new CharacterRepository(fixture.Database), clock, 4, policy, probeCase);
    }

    internal static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new LukeRaidClockTests.Clock();
        DatabaseSchemaTests.FiendwarUpgrade(Path.Combine(root, "fiendwar-schema"));
        DatabaseSchemaTests.RaidGuildContributionUpgrade(Path.Combine(root, "fiendwar-schema"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "fiendwar-flow"), resources: resources, clock: clock,
            fiendwarMazeRandom: new MazeCoverageRandom());
        await fixture.StartAsync();
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire("fiendflow", () => { }))
        {
            var point = new Location(38, 0, 629, 196);
            var candidate = CharacterCreation.Prepare("FiendFlow", resources.Characters.Get(0), point, point, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
            var role = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95,
                CompletedQuests = [8672], InitialLearning = learning });
            var items = resources.ItemResources;
            var funded = InventoryService.Commit(repository, owner, role,
                new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(role, [new(10306760, 2)]));
            repository.CommitInventory(owner, funded, new(funded.Inventory, funded.Inventory with { Gold = 1_000_000 }, "fixture_fiendwar_shop_gold"));
        }
        var raid = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items).Find(2)!.Resources!;
        using var peer = Open(fixture, "fiendflow", gamePort: ChannelTcpTests.Endpoints(fixture)[39], channelNumber: 39);
        peer.Select();
        Read("selected");
        BuyMerchant(false, "not started");
        peer.Send(1426, []);
        That(Read("recruitment").Any(packet => packet is { Kind: 0, Opcode: 23 } && packet.Body[2] == 57 && packet.Body[3] == 1),
            "真实1426进入57/1招募街区，不进入普通115");
        var waiting = resources.Towns.Entrance(new(57, 1, 0, 0), new(57, 2, 0, 0), new HashSet<int> { 8672 })!;
        That(waiting is not null, "原候战门存在真实到达坐标");
        peer.Send(36, Area(57, 2, waiting!.X, waiting.Y, 57, 1));
        Read("waiting without raid");
        That(fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[57L, 1L]], "未建团不能进入候战区");
        peer.Send(36, Area(115, 0, 1301, 428, 57, 1));
        Read("normal cross route");
        That(fixture.Query("SELECT town,area FROM characters WHERE id=$p0", peer.Id) is [[57L, 1L]], "漩涡频道伪造36不能串入普通超时空之战");
        peer.Send(664, Build(writer => { writer.Write((byte)2); writer.Write(5u); writer.Write("fiend"u8); writer.Write(new byte[] { 0, 0, 0, 7 }); }));
        That(Read("created").Any(packet => packet is { Kind: 1, Opcode: 664, Body: [1, ..] }), "无公会真实创建漩涡攻坚队");
        peer.Send(36, Area(57, 2, waiting.X, waiting.Y, 57, 1));
        That(Read("waiting with raid").Any(packet => packet is { Kind: 0, Opcode: 23 } && packet.Body[2] == 57 && packet.Body[3] == 2) &&
            fixture.Query("SELECT count(*) FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[0L]],
            "已建团未开战可进57/2候战，未提前扣票或创建战斗账本");
        peer.Send(666);
        var started = Read("started");
        That(started.Any(packet => packet is { Kind: 1, Opcode: 666, Body: [1] }), "单人不编固定分队即可正式开战");
        That(started.Any(packet => packet is { Kind: 0, Opcode: 1522, Body: [] }), "漩涡开战使用原生空体1522");
        That(started.Any(packet => packet is { Kind: 0, Opcode: 1525 }), "开战发布真实战场成员位置");
        var initialSymbols = started.Last(packet => packet is { Kind: 0, Opcode: 1530 }).Body;
        That(initialSymbols.Length == 1 + initialSymbols[0] * 8 &&
            Enumerable.Range(0, initialSymbols[0]).All(index => U32(initialSymbols, 1 + index * 8) != 102),
            "漩涡开战保留原生1530状态行，省略击杀全员的102零执行事件");
        That(started.Any(packet => packet is { Kind: 0, Opcode: 1528 } && packet.Body[0] > 0),
            "正式开战即发布原生1528兵线与设施时钟");
        That(fixture.Query("SELECT raid_type,state FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[2L, 0L]],
            "漩涡费用与类型2生命周期共同提交");
        peer.Send(666);
        That(Read("start replay").Any(packet => packet is { Kind: 1, Opcode: 666, Body: [1] }), "重复开战回放原成功");
        That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[1L]], "重复开战不新增账本");

        int area = 2;
        var merchant = started.Last(packet => packet is { Kind: 0, Opcode: 1529 }).Body;
        That(merchant.Length == 5 && raid.Fiendwar!.MerchantPositions.Any(row => row.Area == merchant[0]),
            "商人区域来自原资源并提供完整停留秒数");
        Travel(merchant[0]);
        BuyMerchant(true, "at merchant");
        Travel(raid.Fiendwar!.Areas[area].Neighbours.First(next => next is not (30 or 31)));
        BuyMerchant(false, "left merchant");
        Travel(2);
        Travel(15);
        var report = new AntonTcpProbe.AntonProbeReport();
        int eventsBeforeRadar = fixture.Events().Length;
        int radarDungeon = raid.Fiendwar!.Areas[15].DungeonId;
        // 漩涡清图同步原生战况；不用安徒恩586或legacy117确认作为成功条件。
        AntonTcpProbe.Enter(peer, raid.EntryCatalog!, radarDungeon, report, clear: true, requireClearNotice: false);
        var radarEvents = fixture.Events().Skip(eventsBeforeRadar).ToArray();
        var radarClears = radarEvents.Where(row => row.GetProperty("Kind").GetString() == "formal_dungeon_death")
            .Select(row => row.GetProperty("Detail"))
            .Where(detail => detail.TryGetProperty("ClearFact", out var fact) &&
                fact.ValueKind == System.Text.Json.JsonValueKind.Object &&
                fact.GetProperty("DungeonId").GetUInt32() == (uint)radarDungeon).ToArray();
        That(radarClears.Length == 1 && radarClears[0].GetProperty("Outcome").GetInt32() == 0 &&
            radarClears[0].GetProperty("ClearFact").GetProperty("ConfirmedDeathEvents").GetArrayLength() > 0,
            "雷达真实39被接受并生成一次正式ClearFact，117不重复产生通关事实");
        var clearIdentity = radarClears.Single().GetProperty("ClearFact").GetProperty("Identity");
        string? radarRun = clearIdentity.GetProperty("InstanceId").GetString();
        That(radarEvents.Where(row => row.GetProperty("Kind").GetString() == "raid_run_clear")
            .Select(row => row.GetProperty("Detail")).Any(detail =>
                detail.GetProperty("Run").GetString() == radarRun &&
                detail.GetProperty("Dungeon").GetUInt32() == (uint)radarDungeon &&
                detail.GetProperty("AfterRevision").GetInt64() > detail.GetProperty("BeforeRevision").GetInt64() &&
                detail.GetProperty("State").GetString() == "Fighting"),
            "同一正式Run清图推进漩涡战况revision，保持整团Fighting");
        That(radarEvents.Where(row => row.GetProperty("Kind").GetString() == "send")
            .Select(row => row.GetProperty("Detail")).Any(detail =>
                detail.GetProperty("Opcode").GetInt32() == 1528 &&
                detail.GetProperty("Body").GetString() == "010F0C0000000000000000"),
            "雷达通关实发区域15种类12完整计时清除行，End与Duration均为0");
        That(fixture.Query("SELECT state FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[0L]],
            "雷达清图与正常退场不冒充整团胜利");
        foreach (int count in new[] { 1, 4, 8 }) VerifyParties(fixture, resources, raid, repository, clock, count);

        void BuyMerchant(bool expected, string action)
        {
            var before = fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id);
            peer.Send(21, Build(writer => { writer.Write(10312384); writer.Write(1); writer.Write(305); writer.Write(626); }));
            var ack = Read("merchant " + action).Single(row => row is { Kind: 1, Opcode: 21 });
            That(ack.Body[0] == (expected ? 1 : 0), "随机商人真实购买资格：" + action);
            var after = fixture.Query("SELECT gold FROM characters WHERE id=$p0", peer.Id);
            That(expected ? Convert.ToInt64(after[0][0]) < Convert.ToInt64(before[0][0]) : Equals(after[0][0], before[0][0]),
                "商人实际购买扣费，拒绝不扣费：" + action);
        }
        void Travel(int target)
        {
        var queue = new Queue<int>(); queue.Enqueue(area);
        var previous = new Dictionary<int, int> { [area] = -1 };
        while (queue.TryDequeue(out int current) && !previous.ContainsKey(target))
            foreach (int next in raid.Fiendwar!.Areas[current].Neighbours.Where(next => next is not (30 or 31)))
                if (previous.TryAdd(next, current)) queue.Enqueue(next);
        var path = new List<int>();
        for (int step = target; step != area; step = previous[step]) path.Add(step);
        path.Reverse();
        foreach (int next in path)
        {
            var destination = new Location(57, checked((byte)next), 0, 0);
            var arrival = resources.Towns.Entrance(new(57, checked((byte)area), 0, 0), destination, new HashSet<int> { 8672 });
            That(arrival is not null, "资源有真实相邻区域落点：" + area + "->" + next);
            peer.Send(36, Area(57, checked((byte)next), checked((ushort)arrival!.X), checked((ushort)arrival.Y), 57, checked((ushort)area)));
            var moved = Read("move " + next);
            That(moved.Any(packet => packet is { Kind: 0, Opcode: 23 } && packet.Body[2] == 57 && packet.Body[3] == next),
                "真实36沿三路战场移动：" + area + "->" + next);
            area = next;
        }
        }

        List<WirePacket> Read(string action) => AntonTcpProbe.Barrier(peer, "fiendwar " + action);
    }
}
