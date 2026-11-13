using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Events;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicRoad2019CatalogTests
{
    public static void RunResources(PvfArchive pvf)
    {
        int before = Count;
        var original = TownCatalog.Load(pvf);
        var towns = TownCatalog.Load(pvf, EpicRoad2019NpcRestoreTests.Geometry(pvf), epicRoad2019Enabled: true);
        That(towns.Areas.TryGetValue((123, 0), out var area),
            "活动启用时能从保留叶资源恢复123城镇，不依赖已删主登记");
        That(area!.Level == 95 && area.Quests.SequenceEqual([8712]) && area.DungeonWorldMap == 204 &&
            area.Default.Error is null && area.Default.Path == "map/gcontents/191210_tayberrs_road/tayberrs_road_town.map",
            "活动城镇保留等级、前置任务、选图及原地图路径");
        That(towns.DungeonMaps.TryGetValue(204, out var selection) && selection.Error is null &&
            selection.Options.Select(option => option.DungeonId).Order().SequenceEqual([400000097, 400000098]),
            "204选图沿原WDM装载免费和超星河两个副本");
        That(towns.NpcMoves.TryGetValue(400001102, out var enter) && enter.Contains(400001103u) &&
            towns.NpcExits.Contains(400001103),
            "亚瑟1沿原role传入亚瑟2，亚瑟2保留退出城镇角色");
        That(original.Areas.Count == towns.Areas.Count - (original.Areas.ContainsKey((123, 0)) ? 0 : 1) &&
            original.Room == towns.Room && original.InitialReturn == towns.InitialReturn,
            "活动目录只补所需城镇，保留原始房间与返城位置");

        var quests = QuestCatalog.Load(pvf, epicRoad2019Enabled: true);
        That(quests.TryGet(8712, out var quest) && quest.MinimumLevel == 95 && quest.Npc == 400001102 &&
            quest.CompleteNpc == -1 && quest.Path == "n_quest/gcontents/191210_tayberrs_road/tayberrs_road_enter.qst" &&
            quest.Definition is { } definition && definition.Diagnostics.Length == 0,
            "原8712真实任务能进入同一份仓储目录，保留等级与交接NPC");

        var dungeons = DungeonCatalog.Load(pvf, EpicRoadResourcePaths.Dungeons, EpicRoadResourcePaths.Maps);
        foreach (var option in selection!.Options)
            That(dungeons.Get(option.DungeonId).MinimumLevel == 95,
                "选图中每个活动副本都能解析到原95级DGN");
        foreach (var (root, id) in new[] { ("town", 123), ("npc", 400001102), ("worldmap", 204), ("n_quest", 8712) })
            Reject<InvalidDataException>(() => EpicRoad2019ResourceIndex.Restore(pvf, root,
                new Dictionary<int, string> { [id] = root + "/different-resource" }),
                "启用活动不能覆盖原目录中的同号异路径登记：" + root);
        Console.WriteLine($"PASS {Count - before} 星河活动城镇、NPC、任务与选图目录装配断言");
    }
}
