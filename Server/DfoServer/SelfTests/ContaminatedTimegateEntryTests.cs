using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ContaminatedTimegateEntryTests
{
    public static void RunResources(TownCatalog towns, PvfArchive pvf, QuestCatalog quests)
    {
        int checks = Count;
        var catalog = SelfTestCatalogs.Dungeons(pvf);
        var dungeon = catalog.Get(5100);
        var quest = quests.Get(4929);
        var acceptance = new ActiveMainQuest(5, 4929, 1) { AcceptanceId = Guid.NewGuid() };
        var character = new Character(1, 0, "Timegate", 0, new(12, 0, 1, 1), null)
        { Level = 95, GrowType = 0x11, CompletedQuests = [3622, 5146], ActiveMainQuests = [acceptance] };
        That(quest is { Condition: "[clear map]", SubType: -1, IntData: [95264] } &&
            quest.Definition is { JobChange: 3, GrowType: 1, Dungeons.Length: 0, Diagnostics.Length: 0 } &&
            quest.Definition.PrerequisiteGroups.Any(group => group.SequenceEqual([5146])) &&
            dungeon.Mazes.All(maze => maze.QuestConnectionTokens.Count == 0),
            "候选剑神6/6只有目标MAP，没有dungeon info或任务连接");
        That(QuestEligibility.Evaluate(character with { ActiveMainQuests = [] }, quest, quests).Eligible &&
            !QuestEligibility.Evaluate(character with { ActiveMainQuests = [], GrowType = 1 }, quest, quests).Eligible &&
            !QuestEligibility.Evaluate(character with { ActiveMainQuests = [], GrowType = 0x21 }, quest, quests).Eligible,
            "真实6/6仅由已一觉、尚未二觉剑魂接取，不能用未觉醒夹具伪造资格");
        That(towns.Areas[(12, 0)].DungeonWorldMap == 17 &&
            towns.DungeonMaps[17].Options.Any(row => row.DungeonId == 5100 && row.InProgress && row.QuestId == quest.Id),
            "5100在时空之门主选图按真实6/6活动任务开放");
        foreach (uint request in new uint[] { 0, 4929 })
        {
            That(DungeonEntryPlan.TryCreate(catalog, character, 5100, request, 2, out var entry, out var reason,
                quest: request == 0 ? null : quest, quests: quests), "真实时空之门显式及零请求可入：" + request + "/" + reason);
            That(entry is { StartMap.Id: 95261, BossMap.Id: 95264, Difficulty: 2 } && entry.QuestId == request &&
                entry.RouteQuestId == request && entry.ActiveQuestSlot == (request == 0 ? byte.MaxValue : acceptance.Slot) &&
                entry.QuestAcceptanceId == (request == 0 ? Guid.Empty : acceptance.AcceptanceId) &&
                entry.QuestAcceptances.SequenceEqual([acceptance]) &&
                entry.Maze.MapSpecification!.Select(map => map.Id).SequenceEqual([95261, 95262, 95263, 95264]),
                "两种请求保留自身实例任务身份、原活动接取和真实四房路线");
            foreach (byte difficulty in new byte[] { 0, 1, 3, 4 })
                That(!DungeonEntryPlan.TryCreate(catalog, character, 5100, request, difficulty, out _, out _,
                    quest: request == 0 ? null : quest, quests: quests), "固定难度2不被任务推导或借路放宽：" + request + "/" + difficulty);
        }
        var maze = dungeon.Mazes.Single();
        var maps = maze.MapSpecification!;
        var target = maps.Single(map => map.Id == quest.IntData[0]);
        foreach (var invalid in new[]
        {
            quest with { IntData = [int.MaxValue] },
            quest with { Definition = quest.Definition! with { Dungeons = [new(120, -1)] } },
            quest with { Definition = quest.Definition! with { Diagnostics = ["unsupported"] } }
        })
            That(!DungeonEntryPlan.TryCreate(dungeon, character, 5100, 4929, 2, out _, out _, quest: invalid, quests: quests),
                "缺失目标、显式其它DGN及未确认定义不能借用普通路线");
        That(!DungeonEntryPlan.TryCreate(catalog, character, 120, 4929, 2, out _, out _, quest: quest, quests: quests),
            "4929目标不在黄龙大会，不能用显式任务进入无关DGN");
        foreach (var invalid in new[]
        {
            character with { ActiveMainQuests = [] },
            character with { CompletedQuests = [3622, 5146, 4929] },
            character with { ActiveMainQuests = [acceptance with { ConditionState = 0 }] },
            character with { ActiveMainQuests = [acceptance with { Slot = 30 }] },
            character with { ActiveMainQuests = [acceptance, new(5, 4933, 1) { AcceptanceId = Guid.NewGuid() }] }
        })
            That(!DungeonEntryPlan.TryCreate(catalog, invalid, 5100, 4929, 2, out _, out _, quest: quest, quests: quests),
                "借路保留现有活动任务、未完成条件和唯一合法任务槽门禁");
        foreach (var invalid in new[]
        {
            maze with { MapSpecification = maps.Where(map => map != target).ToArray() },
            maze with { MapSpecification = maps.Select(map => map == target ? map with { Id = int.MaxValue } : map).ToArray() },
            maze with { QuestConnectionTokens = [new(0, 0), new(0, 4929), new(0, 0)] }
        })
            That(!DungeonEntryPlan.TryCreate(dungeon with { Mazes = [invalid] }, character, 5100, 4929, 2,
                out _, out _, quest: quest, quests: quests), "目标缺失或自身连接难度不符不能退回普通路线");
        That(!DungeonEntryPlan.TryCreate(dungeon with { Mazes = [maze, maze with { SourceOrdinal = maze.SourceOrdinal + 1 }] },
            character, 5100, 4929, 2, out _, out var ambiguous, quest: quest, quests: quests) && ambiguous == "quest-maze-ambiguous",
            "相同目标的多条无连接路线继续拒绝歧义");
        // 改换DGN、任务号和目标MAP验证资源归属规则，不按5100或4929白名单放行。
        var alternate = quest with { Id = 29988, IntData = [123456], Definition = quest.Definition! with { Dungeons = [] } };
        var alternateAcceptance = acceptance with { QuestId = 29988 };
        var alternateMaze = maze with { MapSpecification = maps.Select(map => map == target ? map with { Id = 123456 } : map).ToArray() };
        That(DungeonEntryPlan.TryCreate(dungeon with { Id = 29987, Mazes = [alternateMaze] }, character with { ActiveMainQuests = [alternateAcceptance] },
            29987, 29988, 2, out var borrowed, out _, quest: alternate) &&
            borrowed is { DungeonId: 29987, QuestId: 29988, RouteQuestId: 29988, BossMap.Id: 123456 } &&
            borrowed.QuestAcceptanceId == alternateAcceptance.AcceptanceId,
            "无dungeon info的合法清图按真实地图归属工作，不绑定特定DGN或QST编号");
        Console.WriteLine($"PASS {Count - checks} contaminated timegate entry resource/domain assertions");
    }
}
