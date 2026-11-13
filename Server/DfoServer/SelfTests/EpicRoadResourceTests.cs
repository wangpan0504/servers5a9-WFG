using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicRoadResourceTests
{
    public static void RunResources(PvfArchive pvf)
    {
        int before = Count;
        var native = Testing.SelfTestCatalogs.Dungeons(pvf);
        // 与活动启用入口使用同一份补充映射；默认关闭时仍使用原始目录。
        var catalog = DungeonCatalog.Load(pvf, EpicRoadResourcePaths.Dungeons, EpicRoadResourcePaths.Maps);
        That(EpicRoadResourcePaths.Dungeons.Count == 2 && EpicRoadResourcePaths.Maps.Count == 11,
            "星河裂缝补充名册仅含两图和十一张已交叉核对的地图");
        foreach (var map in EpicRoadResourcePaths.Maps)
            That(catalog.MapPath(map.Key) == map.Value && pvf.Contains(map.Value), "补充地图登记指向归档中的明确文件");
        var character = new Character(1, 0, "EpicRoad", 1, new(1, 0, 1, 1, 0), null) { Level = 95 };
        // 空合成任务目录只选择普通无任务分支，不读取或虚构活动任务。
        var noQuests = new QuestCatalog(new());
        foreach (int id in new[] { 400000097, 400000098 })
        {
            That(catalog.ContainsDungeon(id), "显式补充装配登记活动副本");
            var definition = catalog.Get(id);
            That(Enumerable.Range(0, 5).All(value => definition.AllowsDifficulty((byte)value, false) == (value == 2)),
                "两张活动图按真实客户端请求和DGN固定difficulty2准入，不沿缺省零难度");
            That(!(definition with { Id = 3 }).AllowsDifficulty(2, false) &&
                !(definition with { Path = "dungeon/other.dgn" }).AllowsDifficulty(2, false),
                "活动固定难度兼容同时核对编号和原资源路径");
            var difficulty = definition.RawSections.Single(section => section.Tag == "difficulty");
            foreach (var sections in new[]
            {
                definition.RawSections.Where(section => section.Tag != "difficulty").ToArray(),
                definition.RawSections.Append(difficulty).ToArray(),
                definition.RawSections.Select(section => section.Tag == "difficulty" ?
                    section with { Tokens = [new(0, 1)] } : section).ToArray()
            })
                That(!(definition with { RawSections = sections }).AllowsDifficulty(2, false),
                    "固定难度声明缺失、重复或不符时不能借活动编号放行");
            That(catalog.DungeonPath(id) == EpicRoadResourcePaths.Dungeons[id] &&
                (!native.ContainsDungeon(id) || native.DungeonPath(id) == catalog.DungeonPath(id)),
                "补充装配不覆盖原始副本登记");
            That(definition.Path.StartsWith("dungeon/gcontents/191210_tayberrs_road/", StringComparison.Ordinal) &&
                definition.MinimumLevel == 95 && definition.DisjointForbiddenDropRarities.SetEquals([4]),
                "星河裂缝两图真实资源冻结史诗禁止分解声明");
            That(definition.DisableClearReward &&
                definition.RawSections.Where(section => section.Tag == "direct eplp on clear dungeon").ToArray() is [{ Tokens.Count: 0 }] &&
                !definition.ForcedCreateDelilah,
                "星河裂缝两图保留叶声明disable clear reward与direct eplp on clear dungeon，未单独声明forced create delilah");
            var forcedDelilah = new DungeonRawSection("forced create delilah", [new(0, 1)]);
            That((definition with { RawSections = definition.RawSections.Append(forcedDelilah).ToArray() }).ForcedCreateDelilah,
                "显式单条forced create delilah=1可被识别");
            foreach (var sections in new[]
            {
                definition.RawSections.ToArray(),
                definition.RawSections.Append(forcedDelilah).Append(forcedDelilah).ToArray(),
                definition.RawSections.Append(forcedDelilah with { Tokens = [new(0, 0)] }).ToArray()
            })
                That(!(definition with { RawSections = sections }).ForcedCreateDelilah,
                    "强制德利拉声明缺失、重复或非1时不启用forced create delilah标记");
            That(definition.Mazes.Count > 1 &&
                !DungeonEntryPlan.TryCreate(definition, character, (uint)id, 0, 2, out _, out var ambiguous) &&
                ambiguous == "route-maze-ambiguous",
                "未提供任务目录的底层调用仍保留多路线歧义拒绝");
            foreach (var maze in definition.Mazes)
            {
            int draws = 0, bound = 0;
            That(DungeonEntryPlan.TryCreate(definition, character, (uint)id, 0, 2, out var selected, out var selectedReason,
                quests: noQuests, draw: size => { draws++; bound = size; return maze.SourceOrdinal; }),
                "带任务目录的普通分支能选择每条真实声明路线：" + selectedReason);
            That(draws == 1 && bound == definition.Mazes.Count && selected!.Maze.SourceOrdinal == maze.SourceOrdinal,
                "沿用声明条目单次抽样并冻结路线，保留重复而不宣称官方隐藏Boss概率");
            var singleRoute = definition with { Mazes = [maze] };
            That(DungeonEntryPlan.TryCreate(singleRoute, character, (uint)id, 0, 2, out var plan, out var reason),
                "真实星河脚本的逐条测试路线能生成95级入场计划：" + reason);
            That(plan!.DisjointForbiddenDropRarities.IsEmpty && plan.DisableClearReward && plan.DisableGiveupPenalty &&
                !plan.ForcedCreateDelilah,
                "本服活动两图入场解除史诗来源限制并保留无普通翻牌及虚弱惩罚");
            That(plan.ExperienceWeight == DungeonKillExperienceCatalog.GetDungeonWeight(catalog.Get(291100308)) &&
                plan.ExperienceWeight > 0 && plan.SuppressOrdinaryResultBoard,
                "两活动图击杀权重参考星空裂缝所在黑暗都市，仍抑制普通结算页");
            That(!DungeonEntryPlan.TryCreate(singleRoute, character with { Level = 94 }, (uint)id, 0, 2, out _, out _),
                "星河裂缝两图拒绝不足95级角色");
            if (id == 400000097)
                That(!plan.UsesFatigue && plan.EntryItem is null && plan.StartFatigue == 0 && definition.EnterWithoutFatigue,
                    "免费星河裂缝沿资源免票免疲劳，允许零疲劳准入");
            else
                That(plan.UsesFatigue && !plan.UsesRoomFatigue && plan.StartFatigue == 10 &&
                    plan.EntryItem is { ItemId: 490013728, Count: 1, Third: 1 },
                    "超星河裂缝沿既有入场一次性扣10疲劳和花瓣1张，不再逐房扣点");
            }
        }
        var incount = DungeonIncountCatalog.Load(pvf);
        var free = incount.AccountRows.Single(row => row.Dungeon == 400000097);
        That(free.Limit == 10 && incount.ResetInfo[free.ResetType] == -1 &&
            !incount.AccountRows.Any(row => row.Dungeon == 400000098),
            "免费本资源账号每日10次，收费本没有账号次数上限");

        // 官方 DGN 只声明两种路线形状；这些是资源事实，不是隐藏出现率或奖励概率。
        var freeMazes = catalog.Get(400000097).Mazes;
        var paidMazes = catalog.Get(400000098).Mazes;
        That(freeMazes.Count == 2 && paidMazes.Count == 13,
            "官方普通/超星河图分别保留2条与13条原始 maze 声明");
        That(freeMazes.Count(maze => maze.MapSpecification is { Count: 3 } maps && maps[2] is { Kind: "boss", Id: 400000459 }) == 1 &&
            freeMazes.Count(maze => maze.MapSpecification is { Count: 3 } maps && maps[2] is { Kind: "boss", Id: 400000462 }) == 1,
            "普通星河裂缝路线只指向459/462两种末房，保留原声明计数");
        That(paidMazes.Count(maze => maze.MapSpecification is { Count: 3 } maps && maps[2] is { Kind: "boss", Id: 400000619 }) == 12 &&
            paidMazes.Count(maze => maze.MapSpecification is { Count: 3 } maps && maps[2] is { Kind: "boss", Id: 400000465 }) == 1,
            "超星河裂缝路线只指向465/619两种末房，保留原声明计数");

        var specialDrops = DungeonSpecialDropCatalog.Load(pvf);
        var epicBosses = new[] { 407000590, 407000595, 407000596, 407000601 };
        That(epicBosses.All(monster => specialDrops.IndependentByMonster.TryGetValue(monster, out var rows) &&
            rows.Length > 0 && rows.All(row => row.ListFlag == 0 && row.ListValues.IsEmpty &&
                row.ItemId is 490013716 or 490013717)),
            "四个活动末房在官方 independent_drop 中仅声明花瓣/材料行，不冒称装备 producer");
        That(epicBosses.Where(monster => monster is 407000596 or 407000601)
                .All(monster => specialDrops.IndependentByMonster[monster].Count(row => row.ItemId == 490013717) == 2) &&
            epicBosses.Where(monster => monster is 407000590 or 407000595)
                .All(monster => specialDrops.IndependentByMonster[monster].All(row => row.ItemId == 490013716)),
            "官方四个末房的第二材料只出现在619/465对应领主，仍不连接area123装备池");

        var epicTables = DungeonHellEpicCatalog.Load(pvf).Areas[123];
        var expectedEpicTableShape = new (int List, int Count, int Weight)[] { (1, 122, 1055300), (2, 91, 1137500),
            (3, 37, 999999), (4, 74, 1000036), (5, 25, 1000000) };
        That(expectedEpicTableShape.All(expected => epicTables.TryGetValue(expected.List, out var rows) &&
            rows.Length == expected.Count && rows.Sum(row => row.Weight) == expected.Weight),
            "官方 area=123 五张史诗候选表的条数与权重总和可完整装载");
        Console.WriteLine($"PASS {Count - before} 星河裂缝真实资源准入、费用及来源限制断言");
    }
}
