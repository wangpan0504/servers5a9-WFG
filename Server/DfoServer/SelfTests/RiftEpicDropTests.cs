using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RiftEpicDropTests
{
    internal static void RunCore(DungeonDropRuleCatalog rules, DungeonEntryPlan basis)
    {
        ItemTemplate Gear(int id, byte level, int grade, int weight) =>
            new(id, ItemKind.Equipment, 0, level, 37, 1, 1, 4, grade, "[free]", ushort.MaxValue, 0, "合成裂缝史诗") { CreationRate = weight };
        var old = Gear(8500, 85, 93, 2);
        var high = Gear(9000, 90, 100, 3);
        var harlem = Gear(9500, 95, 101, 0);
        var white = old with { Id = 1, Rarity = 0, MinimumLevel = 1, Grade = 1, CreationRate = 1 };
        var candidates = DungeonItemDropCatalog.Create([white, old, high]);
        var fragment = white with { Id = 2, Kind = ItemKind.Material, StackLimit = 100 };
        var refined = fragment with { Id = DungeonRiftEpicDropPolicy.RefinedTerraniumId };
        var fragmentPool = new DungeonItemDropPool([new DungeonWeightedDropItem(fragment, 1)]);
        var dawnPool = new DungeonItemDropPool([new DungeonWeightedDropItem(harlem, 11)]);
        var materialRules = PandemoniumRiftMaterialRules.Parse("{}");
        var rifts = new DungeonRiftEpicDropPolicy(candidates, dawnPool, fragmentPool, refined, materialRules);
        Reject<InvalidDataException>(() => PandemoniumRiftMaterialRules.Parse("null"), "空裂缝材料规则拒绝");
        Reject<System.Text.Json.JsonException>(() => PandemoniumRiftMaterialRules.Parse("{\"Fragments\":5}"), "未知配置名拒绝");
        Reject<InvalidDataException>(() => PandemoniumRiftMaterialRules.Parse("{\"FragmentRolls\":101}"), "碎片次数有界");
        Reject<InvalidDataException>(() => PandemoniumRiftMaterialRules.Parse("{\"RefinedTerraniumPermille\":1001}"), "精炼石概率越界拒绝");
        var normal = new DungeonResourceDropPolicy(rules, candidates, id => new(id, "monster/合成裂缝.mob", null, null), _ => white);
        var policy = new DungeonResourceDropPolicy(rules, candidates, id => new(id, "monster/合成裂缝.mob", null, null), _ => white, riftEpics: rifts);
        foreach (var (dungeonId, mapId, monsterId) in new (uint, int, int)[] { (316, 44158, 63804), (316, 44159, 107001626), (530, 41207, 71030) })
        {
            var map = basis.BossMap with { Id = mapId, Kind = "boss" };
            var entry = basis with { DungeonId = dungeonId, BossMap = map, HellParty = null };
            var source = new DungeonRoomMonster(monsterId, [monsterId, 1, 0, 100, 100, 0, 1, 1], "[fixed]", "[boss]", "monster/合成裂缝.mob");
            var monster = new DungeonPlannedMonster(0, 10, monsterId, 1, source) { SpawnType = 3, SourceMap = map };
            var before = normal.PrepareEntry(entry)(monster);
            var after = policy.PrepareEntry(entry)(monster);
            That(before.Trials[2].RarityPools[4].Items.Entries.All(row => row.Template.Rarity == 0) &&
                after.Trials[2].RarityPools[4].Items.Entries.Select(row => row.Template.Id)
                    .SequenceEqual(dungeonId == 316 ? new[] { 8500, 9000 } : new[] { 9500 }),
                $"裂缝领主不把史诗空池回退成普通装备：dungeon={dungeonId},eligible={rifts.Matches(entry, monster)}," +
                $"before={string.Join(',', before.Trials[2].RarityPools[4].Items.Entries.Select(row => row.Template.Id))}," +
                $"after={string.Join(',', after.Trials[2].RarityPools[4].Items.Entries.Select(row => row.Template.Id))}");
            That(after.Trials.Length == before.Trials.Length + (dungeonId == 316 ? 2 : 0) && after.Trials.Zip(before.Trials).All(pair =>
                pair.First with { RarityPools = pair.Second.RarityPools } == pair.Second),
                "独立准备的裂缝计划保持原触发率、数量、次数与其它掉落通道");
            var frozen = rifts.Extend(before, entry, monster);
            That(Enumerable.Range(0, 7).Where(index => index != 4).All(index =>
                ReferenceEquals(before.Trials[2].RarityPools[index].Items, frozen.Trials[2].RarityPools[index].Items)),
                "变换只替换史诗档，保留其它六档原候选对象");
            var empty = new DungeonDropGenerationPlan("无通用奖励", []);
            var bonusPlan = rifts.Extend(empty, entry, monster);
            var bonuses = bonusPlan.Generate(_ => 0, DateTimeOffset.UnixEpoch);
            That(dungeonId == 316 ? bonuses.Count(row => row.Item!.ItemId == fragment.Id) == 5 &&
                bonuses.Count(row => row.Item!.ItemId == refined.Id) == 1 && bonuses.All(row => row.Item!.Count == 1)
                : bonuses.IsEmpty, "魔界独立补全五片与一次精炼石机会，黎明不借材料");
            if (dungeonId == 316)
            {
                That(bonusPlan.Generate(bound => bound - 1, DateTimeOffset.UnixEpoch).Length == 5,
                    "默认精炼石未命中边界不影响五份碎片");
                var disabled = new DungeonRiftEpicDropPolicy(candidates, dawnPool, fragmentPool, refined,
                    new() { FragmentPermille = 0, RefinedTerraniumPermille = 0 });
                That(disabled.Extend(empty, entry, monster).Trials.IsEmpty, "两个材料概率为0时显式关闭，不关闭史诗修正");
                var guaranteed = new DungeonRiftEpicDropPolicy(candidates, dawnPool, fragmentPool, refined,
                    new() { FragmentRolls = 0, RefinedTerraniumPermille = 1000 });
                That(guaranteed.Extend(empty, entry, monster).Generate(bound => bound - 1, DateTimeOffset.UnixEpoch)
                    is [{ Item.ItemId: DungeonRiftEpicDropPolicy.RefinedTerraniumId }], "零碎片次数和精炼石必出边界独立生效");
            }
            var isolated = new DungeonDropGenerationPlan("裂缝史诗实际生成", [after.Trials[2]]);
            var generated = isolated.Generate(bound => bound == 1000000 ? 800000 : 0, DateTimeOffset.UnixEpoch);
            That(generated.Length == 1 && generated[0].Item!.ItemId == (dungeonId == 316 ? old.Id : harlem.Id),
                "裂缝史诗在原触发及稀有度边界真实创建");
            foreach (var invalid in new[] { entry with { IsPractice = true }, entry with { RouteQuestId = 10 },
                entry with { DungeonId = 7191 }, entry with { IsInfernoParty = true } })
                That(ReferenceEquals(rifts.Extend(before, invalid, monster), before), "其它路线不借裂缝领主史诗池");
            foreach (var invalid in new[] { monster with { IsFriendly = true }, monster with { SourceMap = map with { Id = 1 } },
                monster with { MonsterId = 1 }, monster with { IsDummyMapBoss = true } })
                That(ReferenceEquals(rifts.Extend(before, entry, invalid), before), "友方、错误领主、非终点与dummy不借专用候选");
            foreach (bool eligibleFirst in new[] { true, false })
            {
                var cached = policy.PrepareEntry(entry);
                var outside = monster with { SourceMap = map with { Id = 1 } };
                _ = cached(eligibleFirst ? monster : outside);
                That(cached(monster).ResourceSource.Contains(DungeonRiftEpicDropPolicy.Rule) &&
                    !cached(outside).ResourceSource.Contains(DungeonRiftEpicDropPolicy.Rule),
                    "同模板在终点及非终点的资格参与缓存身份，两种准备顺序均不串池");
            }
        }
    }

    internal static void RunResources(PvfArchive pvf, ItemResources items)
    {
        int before = Count;
        var dungeons = Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        var dawn = DungeonHellEpicCatalog.Load(pvf).Pool(54, 2, items.Items.Get);
        var rifts = new DungeonRiftEpicDropPolicy(items.DungeonDropItems, dawn, items.EpicPieceDrops.Pool(85, 90),
            items.Items.Get(DungeonRiftEpicDropPolicy.RefinedTerraniumId), new());
        var policy = new DungeonResourceDropPolicy(Testing.SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            DungeonMonsterDropCatalog.Load(pvf).Get, items.Items.Get, items.DungeonSpecialDrops, riftEpics: rifts);
        foreach (int id in new[] { 316, 530 })
        {
            var dungeon = dungeons.Get(id);
            foreach (var maze in dungeon.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0))
            foreach (var map in maze.MapSpecification!.Where(map => map.Kind == "boss"))
            {
                var source = rooms.Get(map, requirePassiveDefinitions: false).Monsters.Single(row => row.RankKind == "[boss]");
                var entry = new DungeonEntryPlan(1, 0, (uint)id, 0, 0, 3, maze, map, map) { BasisLevel = dungeon.BasisLevel };
                var monster = new DungeonPlannedMonster(0, 10, source.MonsterId, dungeon.BasisLevel!.Value, source)
                    { SpawnType = 3, SourceMap = map };
                var plan = policy.PrepareEntry(entry)(monster);
                var pool = plan.Trials.Single(trial => trial.Category == DungeonDropCategory.Equipment).RarityPools[4].Items;
                That(plan.ResourceSource.Contains(DungeonRiftEpicDropPolicy.Rule) && pool.Entries.All(row => row.Template.Rarity == 4) &&
                    pool.Entries.Select(row => row.Template.MinimumLevel).Distinct().Order().SequenceEqual(id == 316 ? new byte[] { 85, 90 } : new byte[] { 95 }),
                    id + "真实迷宫领主沿合法终点身份使用正确史诗等级");
                if (id == 530) That(pool.Entries.SequenceEqual(dawn.Entries), "黎明95哈林名册及专表权重完整保留");
                if (id == 316)
                {
                    var generated = rifts.Extend(new("魔界材料实际创建", []), entry, monster).Generate(_ => 0, DateTimeOffset.UnixEpoch);
                    That(generated.Length == 6 && generated.Count(row => items.EpicPieces.ByPiece.ContainsKey(row.Item!.ItemId)) == 5 &&
                        generated.Count(row => row.Item!.ItemId == DungeonRiftEpicDropPolicy.RefinedTerraniumId) == 1,
                        "两个真实领主可新建五份可入史诗图鉴碎片及精炼石，不借自然grade池");
                }
            }
        }
        var metro = dungeons.Get(316);
        foreach (var maze in metro.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0))
        foreach (var map in maze.MapSpecification!.Where(map => map.Id is 44153 or 44156))
        {
            int id = map.Id == 44153 ? 59538 : 59537;
            var source = rooms.Get(map, requirePassiveDefinitions: false).Monsters.Single(row => row.MonsterId == id);
            var boss = maze.MapSpecification!.Single(row => row.Kind == "boss");
            var entry = new DungeonEntryPlan(1, 0, 316, 0, 0, 3, maze, map, boss) { BasisLevel = metro.BasisLevel };
            var monster = new DungeonPlannedMonster(0, 10, id, metro.BasisLevel!.Value, source) { SourceMap = map };
            var plan = policy.PrepareEntry(entry)(monster);
            That(plan.Trials.Single(row => row.Category == DungeonDropCategory.Equipment).RarityPools[4].Items.Entries
                .Select(row => row.Template.MinimumLevel).Distinct().Order().SequenceEqual(new byte[] { 85, 90 }) &&
                !plan.ResourceSource.Contains(DungeonRiftEpicDropPolicy.MaterialRule),
                "两只normal源行的真实绿名也使用85/90史诗池，不借领主材料次数");
            var outside = monster with { SourceMap = map with { Id = 1 } };
            var cached = policy.PrepareEntry(entry);
            _ = cached(outside);
            That(cached(monster).ResourceSource.Contains(DungeonRiftEpicDropPolicy.Rule) &&
                !cached(outside).ResourceSource.Contains(DungeonRiftEpicDropPolicy.Rule), "绿名资格亦参与缓存，不向其它地图串池");
        }
        Console.WriteLine($"PASS {Count - before} 魔界裂缝与黎明裂缝真实领主史诗池断言");
    }
}
