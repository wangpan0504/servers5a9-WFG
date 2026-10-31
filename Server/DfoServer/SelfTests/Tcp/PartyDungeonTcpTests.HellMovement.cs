using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    static void VerifyHellRoomMoveRecovery(TcpTestFixture fixture, TcpTestResources resources)
    {
        // 复现谜之觉悟的提前触门，并覆盖电站、寂静城、哈林及单人/共享实例。
        foreach (uint dungeon in new uint[] { 77, 101, 191, 291100308 })
        foreach (int count in new[] { 1, 2 })
        {
            var area = resources.Towns.Areas.Values.First(row => row.Level <= 95 && row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world] is { HellDungeon: true } map && map.Options.Any(option => option.DungeonId == dungeon));
            var worldMap = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value];
            var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(worldMap.HellQuests).ToHashSet();
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town, area.Area, (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2));
            using var group = new Coop(fixture, resources, $"exit{dungeon % 1000}{count}", position, done.ToArray(), 95, count, tickets: 90);
            group.Join(); group.Open();
            group.Peers[0].Send(16, Coop.Selection(dungeon, 0, 4, true));
            group.ReadEntry(group.All()); group.Load();
            var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get((int)dungeon).Mazes.Single(row => row.SourceOrdinal == group.Information[7]);
            var path = QuestStoryRouteTests.Path(maze.StaticTopology, new(group.Map[0], group.Map[1]), maze.HellParty!.Room);
            That(path.Length > 1, "深渊过图回归必须实际进入独立深渊房");
            foreach (var next in path.Skip(1)) { group.Clear(); group.Move((byte)next.X, (byte)next.Y); group.Load(); }
            var map = group.Map;
            var enemies = Enumerable.Range(0, map[18]).Select(i => 19 + 22 * i)
                .Where(at => map[at + 21] == 0 && map[at + 13] != 9).ToArray();
            var waves = enemies.Where(at => map[at + 14] == 1).ToArray();
            That(waves.Length > 0, "深渊过图回归包含尚未击杀的真实波次对象");
            foreach (int at in enemies.Except(waves)) Die(at);
            RejectMove();
            foreach (int at in waves.SkipLast(1)) Die(at);
            RejectMove();
            Die(waves[^1]);
            // 同一连接在收到失败回应、最后一个波次对象死亡后重试，不重新入场或改存档开门。
            var target = path[^2];
            var moved = group.Move((byte)target.X, (byte)target.Y);
            That(moved.All(rows => rows.Any(packet => packet is { Kind: 1, Opcode: 45 } && packet.Body.SequenceEqual(new byte[] { 1 }))),
                $"深渊{dungeon}/{count}人最后波次死亡后同连接可再次过图");
            group.Load();

            void Die(int at)
            {
                ushort key = U16(map, at + 6);
                group.Peers[0].Send(39, FormalDungeonTcpTests.Death(key, group.Peers[0].Id, map[at + 12]));
                That(group.All().All(rows => rows.Any(packet => packet is { Kind: 0, Opcode: 38 } && U16(packet.Body) == key)),
                    "深渊普通怪及波次死亡仍同步到原实例全部成员");
            }

            void RejectMove()
            {
                string before = fixture.Snapshot();
                var target = path[^2];
                var rejected = group.Move((byte)target.X, (byte)target.Y, expect: false);
                That(rejected[0].Count(packet => packet is { Kind: 1, Opcode: 45 } && packet.Body.SequenceEqual(new byte[] { 0, 19, 0 })) == 1 &&
                    rejected.Skip(1).All(rows => !rows.Any(packet => packet is { Kind: 1, Opcode: 45 })) &&
                    rejected.All(rows => !rows.Any(packet => packet is { Kind: 0, Opcode: 29 })) && fixture.Snapshot() == before,
                    $"深渊{dungeon}/{count}人未清波次只给请求者失败回应以释放等待，不切图、不写库");
            }
        }
    }
}
