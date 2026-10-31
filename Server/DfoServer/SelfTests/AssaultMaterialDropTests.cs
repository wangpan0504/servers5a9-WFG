using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AssaultMaterialDropTests
{
    public static void Run(PvfArchive pvf, ItemCatalog items)
    {
        int checks = Count;
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var catalog = AssaultModeCatalog.Load(pvf, dungeons);
        var policy = new AssaultMaterialDropPolicy(catalog, items.Get);
        var empty = new DungeonDropGenerationPlan("test", []);
        foreach (int id in catalog.Missions.Values.SelectMany(rows => rows))
        {
            var definition = dungeons.Get(id);
            var maze = definition.Mazes[0];
            var monster = maze.MapSpecification!.SelectMany(map =>
                DungeonMonsterPlan.FromRoom(rooms.Get(map, requirePassiveDefinitions: false), 1, 100,
                    definition.BasisLevel ?? 95).Monsters).First(row => row.CanReportDeath && !row.IsFriendly &&
                        !row.IsDummyMapBoss && !row.IsScriptedBoss && row.AiCharacter is null);
            var map = monster.SourceMap!;
            var entry = new DungeonEntryPlan(1, 0, checked((uint)id), 0, byte.MaxValue, 0, maze, map, map);
            var generated = policy.Extend(empty, entry, monster);
            var now = DateTimeOffset.UnixEpoch;
            var hit = generated.Generate(bound => bound == 10000 ? 999 : 0, now);
            That(hit is [{ Item.ItemId: AssaultMaterialDropPolicy.MaterialId, Item.Count: 1 }] &&
                generated.Generate(bound => bound == 10000 ? 1000 : 0, now).Length == 0,
                $"强袭{id}真实来源气息掉落概率边界及单次数量");
            foreach (var excluded in new[] { entry with { DungeonId = 7300 }, entry with { DungeonId = 7327 },
                entry with { DungeonId = 3 }, entry with { IsPractice = true }, entry with { RouteQuestId = 1 } })
                That(ReferenceEquals(policy.Extend(empty, excluded, monster), empty), "主选图/普通图/练习/剧情不借用强袭材料");
            foreach (var excluded in new[] { monster with { IsFriendly = true }, monster with { IsDummyMapBoss = true },
                monster with { Source = null }, monster with { SourceMap = null }, monster with { IsBlocking = false } })
                That(ReferenceEquals(policy.Extend(empty, entry, excluded), empty), "友方/假Boss/缺来源/非死亡演员不产气息");
        }
        Console.WriteLine($"PASS {Count - checks} 强袭普通及隐藏任务副本材料掉落与排除边界");
    }
}
