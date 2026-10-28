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
            group.Peers[0].Send(16, Coop.Selection(dungeon, 0, 2)); group.Peers[0].Expect(0, 9); group.Peers[0].Closed();
            That(fixture.Snapshot() == before, "第二成员晚期SQL失败，全队异界或远古费用、次数和尝试号回滚");
            fixture.Sql("DROP TRIGGER reject_otherverse_party");
        }
        if (!ancientAvailable)
            Console.WriteLine("SKIP 组队远古7101：官方PVF未安装镇17 Area5与七远古DGN资源增量，仅覆盖现代异界与拒绝回滚");
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
            completed, 95, 2, rewards: [new(3332, 100)]);
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
    }
}
