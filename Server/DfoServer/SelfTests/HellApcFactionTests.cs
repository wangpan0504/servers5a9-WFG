using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class HellApcFactionTests
{
    internal static void RunCore()
    {
        var map = new DungeonMap("map", new(0, 0), 1, "map/fixture.map");
        var room = new DungeonRoomDefinition(map, [], [], [])
        {
            AiCharacters = [new(20900, [20900,0,0,0,0,0], "[character]", "[normal]", "aicharacter/friend.aic", 20)]
        };
        var original = DungeonMonsterPlan.FromRoom(room, 99, 1, 20, companionRows: [20900]);
        var waves = Enumerable.Range(5, 4).Select(type => new DungeonHellWaveMonster(2, 169, 25625 + type - 5, 85, (byte)type, 0, 0)).ToArray();
        var plan = original.WithHellWaves(waves, 99);
        That(plan.HellWaves.Select(row => row.PacketIndex).SequenceEqual([64,65,66,67]) &&
            plan.Monsters.Where(row => row.HellWave is not null).All(row => !row.IsFriendly && row.IsBlocking),
            "深渊全部APC品级进入敌对索引区间，不继承已有友军或被动对象序号");
        That(plan.Monsters[0] == original.Monsters[0] && original.Monsters[0].IsFriendly,
            "地图伴侣友军原行、阵营和对象身份不变");
        var maze = new DungeonMaze(0, 1, 1, "", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(99, 0, 1, 192, 0, 0, maze, map, map);
        var body = DfoServer.Network.Builders.DungeonPackets.StartMap(entry, plan, 1);
        for (int i = 0; i < waves.Length; i++)
        {
            int at = 19 + (original.Monsters.Count + i) * 22;
            That(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 2)) == 64 + i && body[at + 13] == 5 + i &&
                body[at + 14] == 1 && body[at + 15] == 255 && body[at + 21] == 0,
                "实际29隐藏行携带独立APC索引，保留波次模板标志与敌对清房语义");
        }
        var maximum = original.WithHellWaves(Enumerable.Repeat(waves[0], 64).ToArray(), 99);
        That(maximum.HellWaves[^1].PacketIndex == 127, "原码索引上界127可以使用");
        Reject<InvalidDataException>(() => original.WithHellWaves(Enumerable.Repeat(waves[0], 65).ToArray(), 99),
            "超出客户端可生成的APC索引明确拒绝，不回绕到友军表");
    }

    internal static void RunResources(TcpTestResources resources)
    {
        int before = Count, roomsChecked = 0, apcsChecked = 0;
        RunCore();
        var catalog = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var seen = new HashSet<int>();
        foreach (int id in new[] {101,102,103,104,190,191,192,193,194,195})
        {
            var definition = catalog.Get(id);
            var mazes = definition.Mazes.Where(maze => maze.HellParty is not null).ToArray();
            // 王的书库原资源未声明深渊；仍普查并明确断言，不能补造入口。
            That(id == 195 ? mazes.Length == 0 : mazes.Length > 0,
                $"电站/寂静城{id}按实际资源保留深渊声明");
            foreach (var maze in mazes)
            {
                var resolved = catalog.ResolveOrdinaryMaze(definition, maze, _ => 0, hellParty: true);
                var map = resolved.MapSpecification!.Single(row => row.Position == maze.HellParty!.Room) with
                    { Id = maze.HellParty!.MapId, Path = maze.HellPartyPath!, CandidateIds = [] };
                var room = rooms.Get(map, requirePassiveDefinitions: false);
                var tables = room.SpecialObjects.SelectMany(parent => parent.Children).Where(child => child.Kind == "[hellparty]");
                foreach (char difficulty in new[] {'A','B'})
                foreach (int group in tables.SelectMany(child => child.RawValues.Where((_, i) => i % 3 == 0)).Distinct())
                {
                    var entries = rooms.HellParty.Entries(group, difficulty).Where(row => row.EntityType == 1).ToArray();
                    if (entries.Length == 0) continue;
                    var waves = entries.Select(row => new DungeonHellWaveMonster(2, group, row.Code, 85, 5, 0, 0)).ToArray();
                    var original = DungeonMonsterPlan.FromRoom(room, 99, 1, definition.BasisLevel);
                    var plan = original.WithHellWaves(waves, 99);
                    That(plan.HellWaves.All(row => row.PacketIndex is >= 64 and < 128) &&
                        plan.Monsters.Take(original.Monsters.Count).SequenceEqual(original.Monsters),
                        $"{id}/{map.Id}/组{group}/{difficulty}的全部APC走独立敌对区间，保留原MAP演员");
                    foreach (var entry in entries) seen.Add(entry.Code);
                    apcsChecked += entries.Length;
                }
                roomsChecked++;
            }
        }
        That(seen.Contains(25625) && seen.Contains(25626), "十图普查真实覆盖加了特效的兔子和傻白甜小师妹");
        Console.WriteLine($"PASS hell APC faction: {Count - before} checks, rooms={roomsChecked}, apcs={apcsChecked}, unique={seen.Count}");
    }
}
