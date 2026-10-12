using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 主线任务接取与进图使用当前 PVF。不代表 Windows 客户端实机验收。
internal static class MainlineQuestBatchTests
{
    public static void Run(PvfArchive pvf, ItemResources items)
    {
        int checks = Count;
        var quests = QuestCatalog.Load(pvf);
        var dungeons = DungeonCatalog.Load(pvf);
        var rooms = DungeonRoomCatalog.Load(pvf);
        var forest = quests.Get(3292);
        var watch = quests.Get(3272);
        var guards = quests.Get(3265);
        That(QuestClearEligibility.IsSingleMap(forest) && forest.IntData[0] == 92046,
            "3292 林中徘徊的孩子把末尾难度 -1 视为同一张清图");
        That(quests.All.Count(quest => quest.Condition == "[clear map]" && quest.SubType == -1 && quest.IntData is [> 0, -1]) == 1,
            "清图末尾 -1 目前只有 3292");
        That(Accepts(forest), "3292 在前置完成且等级足够时可以接取");
        That(Entry(forest, 46, out var forestPlan) && forestPlan!.BossMap.Id == 92046 && forestPlan.BaseBossMap.Kind == "boss",
            "3292 进入雪林迷宫，终点就是 92046");
        That(Plans(forestPlan!, dungeons.Get(46)), "3292 路线上的房间可以生成演员");

        That(QuestClientSceneProgress.DungeonCinematic(watch) && watch.CinematicMapId == 70147,
            "3272 帕丽丝呢是王的遗迹 Boss 图上的副本演出");
        That(Accepts(watch), "3272 没有城镇引导也可以接取");
        That(Entry(watch, 33, out var watchPlan) && watchPlan!.BossMap.Id == 70147,
            "3272 接取后可以进入演出所在的 Boss 图");

        That(QuestClearEligibility.IsSingleMap(guards) && guards.IntData[0] == 70174, "3265 的清图目标是叠层 70174");
        That(Entry(guards, 154, out var guardPlan) && guardPlan!.BossMap.Id == 70174 && guardPlan.BossMap.Kind == "layered" &&
            guardPlan.BaseBossMap.Kind == "boss" && guardPlan.BaseBossMap.Id == 70138,
            "3265 把 Boss 坐标上的普通基础图升成基础 Boss 图，终点仍是任务层");
        That(guardPlan!.Maze.StaticTopology.Supported, "3265 升格后的静态路线仍然连通: " + guardPlan.Maze.StaticTopology.UnsupportedReason);
        That(Plans(guardPlan, dungeons.Get(154)), "3265 含叠层在内的房间可以生成演员");

        int layered = 0;
        foreach (var quest in quests.All.Where(QuestClearEligibility.IsSingleMap))
        {
            if (quest.Definition is not { Dungeons: [{ DungeonId: > 0 } dungeon, ..] }) continue;
            DungeonDefinition definition;
            try { definition = dungeons.Get(dungeon.DungeonId); }
            catch (InvalidDataException) { continue; }
            var routes = definition.Mazes.Where(row => row.ResourceError is null && row.ConnectsQuest((uint)quest.Id)).ToArray();
            if (routes.Length == 0) continue;
            // 相同连接和布局在入场里合并成一条路线。真正不同的路线仍是既有歧义，不在这次升格范围内。
            var distinct = new List<DungeonMaze>();
            foreach (var row in routes)
                if (!distinct.Any(existing => existing.QuestConnectionTokens.SequenceEqual(row.QuestConnectionTokens) && existing.SameLayout(row)))
                    distinct.Add(row);
            if (distinct.Count != 1 || distinct[0].MapSpecification is not { } maps || distinct[0].Bosses.Count != 1) continue;
            var maze = distinct[0];
            var boss = maze.Bosses[0];
            var bases = maps.Where(map => map.Position == boss).ToArray();
            if (bases.Length != 1 || bases[0].Kind != "map" ||
                !maze.LayeredMaps.Any(map => map.Position == boss && (map.Id == quest.IntData[0] || map.CandidateIds.Contains(quest.IntData[0])))) continue;
            layered++;
            That(Entry(quest, checked((uint)dungeon.DungeonId), out var plan) && plan!.BossMap.Id == quest.IntData[0] && plan.BaseBossMap.Kind == "boss",
                "同类叠层清图可以进到任务地图: " + quest.Id);
        }
        That(layered >= 1, "至少覆盖 3265 这一条 Boss 格叠层清图");
        Console.WriteLine($"PASS {Count - checks} 主线清图接取、副本演出接取和叠层进图");

        bool Accepts(StoryQuest quest)
        {
            var character = Ready(quest);
            var eligibility = QuestEligibility.Evaluate(character, quest, quests);
            That(eligibility.Eligible, "接取资格: " + quest.Id + " " + eligibility.Reason);
            var plan = QuestLifecyclePlan.Accept(character, quest, quests, items.Items, items.GoldLimits, items.EpicPieces, DateTimeOffset.UnixEpoch);
            return plan.ActiveAfter.Single(row => row.QuestId == quest.Id).ConditionState == 1;
        }

        bool Entry(StoryQuest quest, uint dungeonId, out DungeonEntryPlan? plan)
        {
            var definition = dungeons.Get(checked((int)dungeonId));
            var active = Ready(quest, definition.MinimumLevel) with
            {
                ActiveMainQuests = [new ActiveMainQuest(0, checked((ushort)quest.Id), 1) { AcceptanceId = Guid.NewGuid() }]
            };
            if (!DungeonEntryPlan.TryCreate(dungeons, active, dungeonId, checked((uint)quest.Id), 0, out plan, out var reason, quest: quest, quests: quests))
                throw new InvalidOperationException("进图失败 " + quest.Id + ": " + reason);
            return true;
        }

        bool Plans(DungeonEntryPlan plan, DungeonDefinition definition)
        {
            foreach (var map in plan.Maze.MapSpecification!.Concat(plan.Maze.LayeredMaps).Concat(plan.Maze.ConditionalMaps))
                _ = DungeonMonsterPlan.FromRoom(rooms.Get(map, requirePassiveDefinitions: false), plan.CharacterId, 1, definition.BasisLevel);
            return true;
        }

        static Character Ready(StoryQuest quest, int? dungeonLevel = null)
        {
            int level = quest.MinimumLevel is >= 1 and <= 95 ? quest.MinimumLevel : 1;
            if (dungeonLevel is >= 1 and <= 95 && level < dungeonLevel) level = dungeonLevel.Value;
            if (quest.MaximumLevel is >= 1 and <= 95 && level > quest.MaximumLevel) level = quest.MaximumLevel;
            return new(17, 0, "Mainline", 1, new Location(1, 1, 100, 100, 0), null)
            {
                Level = checked((byte)level),
                CompletedQuests = quest.Prerequisites.Where(id => id is > 0 and < 30000).Distinct().ToArray()
            };
        }
    }
}
