using System.Buffers.Binary;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Dungeons;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    public static async Task RunOtherverseAncientAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "party-otherverse-ancient"), gm: true, resources: resources);
        await fixture.StartAsync();
        That(DfoServer.Network.Management.DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _),
            "组队远古显式关闭无限疲劳");
        // 远古七图与镇17 Area5是独立资源增量；未安装时服务器按设计拒绝落点，组队只覆盖现代异界。
        bool ancientAvailable = resources.Towns.Areas.ContainsKey((17, 5)) &&
            SelfTestCatalogs.Dungeons(resources.Archive).ContainsDungeon(7101);
        bool[] modes = ancientAvailable ? new[] { false, true } : new[] { false };
        foreach (bool ancient in modes)
        {
            byte areaId = ancient ? (byte)5 : (byte)2;
            var area = resources.Towns.Areas[(17, areaId)];
            var done = area.Quests.Concat(area.AnyQuests).Concat(area.Default.DungeonGates.SelectMany(gate => gate.Quests)).Distinct().ToArray();
            var bounds = area.Map(done.ToHashSet()).DungeonGates.First(gate => gate.Conditions.Length == 0 && gate.Quests.All(done.Contains)).Bounds;
            var position = new DfoServer.GameWorld.Towns.Location(17, areaId, checked((ushort)(bounds.X + bounds.Width / 2)), checked((ushort)(bounds.Y + bounds.Height / 2)));
            using var group = new Coop(fixture, resources, ancient ? "anc" : "dim", position, done, 95, 2, rewards: [new(3037, 1000), new(3332, 100)]);
            var period = DungeonIncountPolicy.Period(DateTimeOffset.UtcNow);
            if (!ancient)
                fixture.Sql("INSERT INTO character_dungeon_incount(character_id,account,dungeon,day,daily_used,week,weekly_used) VALUES($p0,$p1,7100,$p2,2,$p3,2)",
                    group.Peers[1].Id, "dim1", period.Day, period.Week);
            group.Join(); group.Open();
            uint dungeon = ancient ? 7101u : 7167u;
            string before = fixture.Snapshot();
            fixture.Sql("CREATE TRIGGER reject_otherverse_party BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id=" +
                group.Peers[1].Id + " BEGIN SELECT RAISE(ABORT,'party entry rollback'); END");
            group.Peers[0].Send(16, Coop.Selection(dungeon, 0, 2)); group.Peers[0].ClosedAfterOptionalPartyRemoval();
            That(fixture.Snapshot() == before, "第二成员晚期SQL失败，全队异界或远古费用、次数和尝试号回滚");
            fixture.Sql("DROP TRIGGER reject_otherverse_party");
        }
        if (!ancientAvailable)
            TestCheck.Skip("ancient.party-resource-missing", "组队远古7101：官方PVF未安装镇17 Area5与七远古DGN资源增量，仅覆盖现代异界与拒绝回滚");
        else
        {
            var ancientArea = resources.Towns.Areas[(17, 5)];
            var ancientDone = ancientArea.Quests.Concat(ancientArea.AnyQuests).Concat(ancientArea.Default.DungeonGates.SelectMany(gate => gate.Quests)).Distinct().ToArray();
            var ancientGate = ancientArea.Map(ancientDone.ToHashSet()).DungeonGates.First(row => row.Conditions.Length == 0 && row.Quests.All(ancientDone.Contains)).Bounds;
            using (var ancient = new Coop(fixture, resources, "ancok", new(17, 5, (ushort)(ancientGate.X + ancientGate.Width / 2), (ushort)(ancientGate.Y + ancientGate.Height / 2)),
                ancientDone, 95, 2, rewards: [new(3037, 1000), new(3332, 100)]))
            {
                var fatigue = ancient.Peers.Select(peer => (long)fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", peer.Id)[0][0]).ToArray();
                ancient.Join(); ancient.Open(); ancient.Peers[0].Send(16, Coop.Selection(7101, 0, 2));
                var enteredAncient = ancient.All(); ancient.ReadEntry(enteredAncient);
                foreach (var rows in enteredAncient)
                    That(rows.Any(packet => packet is { Kind: 0, Opcode: 14 } &&
                        Enumerable.Range(0, U16(packet.Body, 1)).Any(index => U16(packet.Body, 3 + 123 * index) == 358 &&
                            U32(packet.Body, 9 + 123 * index) == 880)), "组队每人刷新各自账号无色余额");
                for (int index = 0; index < ancient.Peers.Length; index++)
                    That((long)fixture.Query("SELECT remaining FROM character_fatigue WHERE character_id=$p0", ancient.Peers[index].Id)[0][0] == fatigue[index] - 6,
                        "组队远古每人入场仅扣六疲劳");
                ancient.Load();
                var ancientMaze = SelfTestCatalogs.Dungeons(resources.Archive).Get(7101).Mazes.Single(row => row.SourceOrdinal == ancient.Information[7]);
                var route = QuestStoryRouteTests.Path(ancientMaze.StaticTopology, new(ancient.Map[0], ancient.Map[1]), new(ancient.Information[8], ancient.Information[9]));
                for (int index = 0; index < route.Length; index++)
                {
                    ancient.Clear();
                    if (index + 1 < route.Length) { ancient.Move((byte)route[index + 1].X, (byte)route[index + 1].Y); ancient.Load(); }
                }
                ancient.Settle();
                foreach (var peer in ancient.Peers)
                    That(fixture.Query("SELECT json_extract(j.value,'$.Used') FROM characters c,json_each(c.daily_booster_usage) j WHERE c.id=$p0 AND json_extract(j.value,'$.ItemId')=10159543", peer.Id) is [[1L]],
                        "两名成员真实完整通关、翻牌后各自能量只增加一次");
            }
        }
        var modern = resources.Towns.Areas[(17, 2)];
        var completed = modern.Quests.Concat(modern.AnyQuests).Concat(modern.Default.DungeonGates.SelectMany(gate => gate.Quests)).Distinct().ToArray();
        var gate = modern.Map(completed.ToHashSet()).DungeonGates.First(row => row.Conditions.Length == 0 && row.Quests.All(completed.Contains)).Bounds;
        using var success = new Coop(fixture, resources, "dimok", new(17, 2, (ushort)(gate.X + gate.Width / 2), (ushort)(gate.Y + gate.Height / 2)),
            completed, 95, 2, rewards: [new(3332, 100)], memberJobs: [0, 1]);
        var current = DungeonIncountPolicy.Period(DateTimeOffset.UtcNow);
        fixture.Sql("INSERT INTO character_dungeon_incount(character_id,account,dungeon,day,daily_used,week,weekly_used) VALUES($p0,$p1,7100,$p2,2,$p3,2)",
            success.Peers[1].Id, "dimok1", current.Day, current.Week);
        success.Join(); success.Open(); success.Peers[0].Send(16, Coop.Selection(7167, 0, 2));
        var entered = success.All(); success.ReadEntry(entered);
        var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(7167).Mazes.Single(row => row.SourceOrdinal == success.Information[7]);
        That(!maze.IsDimensionHiddenBoss, "队员隐藏周额已满时全队走普通物理路线，不随机拒绝整队");
        foreach (var peer in success.Peers)
            That(fixture.Query("SELECT daily_used FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=7165", peer.Id) is [[1L]],
                "组队现代异界每人只扣一次共用日额");
        That(entered.All(rows => !rows.Any(packet => packet.Opcode == 550 && U32(packet.Body, 0) is >= 7165 and <= 7179) && rows.Any(packet => packet is { Kind: 0, Opcode: 815, Body.Length: 9 })),
            "组队入场发布个人815，不伪造通用550");
        success.Load();
        var rewardCatalog = new DimensionRewardCatalog(resources.Archive, resources.ItemResources.Items);
        var chronicle = rewardCatalog.Professions.Values.SelectMany(r => r.Normal.Concat(r.Set)).ToHashSet();
        var namedIds = maze.MapSpecification!.SelectMany(m => SelfTestCatalogs.Rooms(resources.Archive).Get(m, requirePassiveDefinitions: false).Monsters)
            .Where(m => m.IsNamed && m.RankKind != "[boss]").Select(m => m.MonsterId).ToHashSet();
        int namedDeaths = 0, bossDeaths = 0;
        var path = QuestStoryRouteTests.Path(maze.StaticTopology, new(success.Map[0], success.Map[1]), new(success.Information[8], success.Information[9]));
        for (int room = 0; room < path.Length; room++)
        {
            var map = success.Map;
            foreach (int at in Enumerable.Range(0, map[18]).Select(i => 19 + 22 * i).Where(at => map[at + 21] == 0 && map[at + 13] != 9))
            {
                ushort key = U16(map, at + 6);
                bool boss = map[at + 13] == 3 && room == path.Length - 1;
                bool named = !boss && namedIds.Contains((int)U32(map, at + 8));
                var report = FormalDungeonTcpTests.Death(key, success.Peers[0].Id, map[at + 12]);
                success.Peers[0].Send(39, report);
                var deaths = success.All();
                for (int member = 0; member < success.Peers.Length; member++)
                {
                    var loot = deaths[member].Single(p => p is { Kind: 0, Opcode: 38 } && U16(p.Body) == key).Body;
                    var own = rewardCatalog.Professions[(member, 1)];
                    int count = 0;
                    for (int ordinal = 0; ordinal < U16(loot, 2); ordinal++)
                    {
                        int offset = 4 + ordinal * 131;
                        int id = (int)U32(loot, offset + 6);
                        if (!chronicle.Contains(id)) continue;
                        That(own.Normal.Contains(id) || own.Set.Contains(id), "R6 TCP不同职业成员只收到本人异界职业池");
                        count++;
                        uint ground = U32(loot, offset);
                        byte[] pickup = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(pickup, ground);
                        var peer = success.Peers[member]; peer.Send(43, pickup);
                        var received = success.All(peer);
                        That(received[member].Any(p => p is { Kind: 1, Opcode: 43, Body: [1] }) &&
                            received.Where((_, index) => index != member).All(rows => !rows.Any(p => p is { Kind: 0, Opcode: 39 } && U32(p.Body) == ground)),
                            "R6 TCP本人领取装备不清除另一成员地面");
                    }
                    if (named || boss) That(count >= (boss ? 2 : 1), "TCP勇士绿名一件、终点Boss两件个人装备");
                }
                if (named) namedDeaths++;
                if (boss) bossDeaths++;
                success.Peers[1].Send(39, report);
                That(success.All(success.Peers[1]).All(rows => !rows.Any(p => p is { Kind: 0, Opcode: 38 })), "重复死亡不重抽个人掉落");
            }
            if (room + 1 < path.Length) { success.Move((byte)path[room + 1].X, (byte)path[room + 1].Y); success.Load(); }
        }
        That(namedDeaths > 0 && bossDeaths == 1, "TCP实际经过真实绿名与终点Boss");
    }
}
