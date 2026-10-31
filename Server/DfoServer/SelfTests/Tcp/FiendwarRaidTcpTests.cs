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

internal static partial class FiendwarRaidTcpTests
{
    internal static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new LukeRaidClockTests.Clock();
        DatabaseSchemaTests.FiendwarUpgrade(Path.Combine(root, "fiendwar-schema"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "fiendwar-flow"), resources: resources, clock: clock);
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
        peer.Send(664, Build(writer => { writer.Write((byte)2); writer.Write(5u); writer.Write("fiend"u8); writer.Write(new byte[4]); }));
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
        AntonTcpProbe.Enter(peer, raid.EntryCatalog!, raid.Fiendwar!.Areas[15].DungeonId, report, clear: true);
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
