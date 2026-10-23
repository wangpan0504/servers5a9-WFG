using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SpecialNormalDungeonEntryTests
{
    internal static readonly int[] DungeonIds = [9595,9596,9597,9599,9600,9601,9602,9603,9604,9605,9606,9607,
        5101,5102,5103,5104,5105,5106,400000016];

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var quests = SelfTestCatalogs.Quests(pvf);
        var character = new Character(17,0,"NormalEntry",1,new Location(38,2,100,200),null) { Level = 95 };
        var failures = new List<string>();
        foreach (int id in DungeonIds)
        {
            var definition = catalog.Get(id);
            int choices = Math.Max(definition.Mazes.Count,definition.Mazes.SelectMany(maze => maze.MapSpecification ?? [])
                .Select(map => map.CandidateIds.Count).DefaultIfEmpty(1).Max());
            var sourceMaps = definition.Mazes.SelectMany(maze => maze.MapSpecification ?? []).ToArray();
            for (int choice = 0; choice < choices; choice++)
            {
                try
                {
                    That(DungeonEntryPlan.TryCreate(catalog,character,(uint)id,0,2,out var plan,out var reason,
                        draw: count => choice % count,quests: quests),$"普通副本{id}候选{choice}可规划：{reason}");
                    var entry = plan!;
                    That(entry is { IsOrdinary: true,QuestId: 0,RouteQuestId: 0,Difficulty: 2 } &&
                        entry.BaseBossMap.Position == entry.Maze.Bosses.Single() && entry.BaseBossMap.Kind == "boss" &&
                        sourceMaps.SequenceEqual(definition.Mazes.SelectMany(maze => maze.MapSpecification ?? [])),
                        "普通Boss坐标投影保留原资源、原地图和任务身份");
                    var planned = new List<DungeonMonsterPlan>();
                    uint next = 100;
                    foreach (var map in entry.Maze.MapSpecification!)
                    {
                        var room = DungeonMonsterPlan.Create(rooms,map,character.Id,checked((ushort)next),entry.MonsterBasisLevel,entry.PlayerTeam);
                        planned.Add(room);
                        if (room.Monsters.Count != 0 || room.SpecialObjects.Count != 0) next = room.NextObjectKey;
                    }
                    var route = new DungeonFixedRoute(planned,entry.Maze.StaticTopology.Links!
                        .Select(link => new DungeonRouteConnection(link.First,link.Second)).ToArray());
                    That(SinglePlayerDungeonRun.TryCreate(entry,1,100,route,new NativeDungeonCombatAuthority(),out _,out var runReason),
                        $"普通副本{id}完整原房间及怪物可创建正式Run：{runReason}");
                }
                catch (Exception error) { failures.Add($"{id}/{choice}: {error.Message}"); }
            }
            foreach (byte wrong in new byte[] { 0,1,3,4 })
                That(!definition.AllowsDifficulty(wrong,false),$"普通副本{id}不接受非指定难度{wrong}");
            That(!DungeonEntryPlan.TryCreate(catalog,character with { Level = (byte)(definition.MinimumLevel!.Value-1) },
                (uint)id,0,2,out _,out var levelReason,quests: quests) && levelReason == "dungeon-level-too-low",
                $"普通副本{id}保留最低等级限制");
        }
        That(failures.Count == 0,"普通副本全路线准入：" + string.Join("; ",failures));
        Console.WriteLine($"PASS {Count-checks} 安徒恩、卢克与超时空普通副本资源及正式Run断言（非客户端实机）");
    }
}
