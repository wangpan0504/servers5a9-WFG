using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PandemoniumRegionalDropTests
{
    public static void RunCore()
    {
        int before = Count;
        var map = new DungeonMap("boss", new(0, 0), 1, "map/地区材料.map");
        var maze = new DungeonMaze(0, 1, 1, "BB", [map.Position], [map.Position], [map], []);
        var definition = new DungeonDefinition(310, "dungeon/metrocenter/time_plaza/time_plaza.dgn", 87, 90, [maze], []);
        var entry = new DungeonEntryPlan(1, 0, 310, 0, 0, 0, maze, map, map);
        var monster = new DungeonPlannedMonster(0, 10, 42, 90, new(42, [], "[fixed]", "[normal]", "monster/地区.mob"));
        var seed = new ItemTemplate(10161610, ItemKind.Material, 0, 1, 2, 1000, 1, 2, 1, "[free]", ushort.MaxValue, 0, "诞生之芽");
        var radiance = seed with { Id = 10163136, Rarity = 6, Name = "次元光辉" };
        var card = seed with { Id = 99, Kind = ItemKind.Profession, IsMonsterCard = true };
        ItemTemplate Item(int id) => id switch
        {
            PandemoniumRegionalDropPolicy.BirthSeedId => seed,
            PandemoniumRegionalDropPolicy.DimensionalRadianceId => radiance,
            _ => card
        };
        var declaration = new DungeonIndependentDropRule(0, 42, card.Id, [0, 100, 1000, 2000, 1000000],
            [1, 1, 2, 1, 1], 0, 0, -1, 3, []);
        var special = DungeonSpecialDropCatalog.Create([], [], [declaration]);
        var policy = new PandemoniumRegionalDropPolicy(_ => definition, Item, special);
        var original = new DungeonDropGenerationPlan("原掉落", []);
        That(policy.Applies(entry) && policy.Applies(entry with { QuestId = 10, RouteQuestId = 10 }) &&
            policy.AppliesDimensionalRadiance(entry) && policy.AppliesDimensionalRadiance(entry with { QuestId = 10, RouteQuestId = 10 }),
            "普通和剧情沿同一资源地区身份，不因任务ID漏掉材料");
        var genericCatalog = DungeonItemDropCatalog.Create(new[] { seed, radiance }.Where(item => item.CreationRate > 0));
        That(seed.CreationRate == 0 && radiance.CreationRate == 0 &&
            genericCatalog.ReferencePool(false, 84, 92, 2).TotalWeight == 0 &&
            genericCatalog.ReferencePool(false, 84, 92, 6).TotalWeight == 0,
            "复现grade1且无creation rate的诞生之芽与次元光辉无法从通用等级池生成");
        var plan = policy.Extend(original, entry, monster);
        int seedSuccesses = 0, radianceSuccesses = 0;
        for (int roll = 0; roll < DungeonExplicitDropPolicy.TriggerDenominator; roll++)
        {
            var generated = plan.Generate(bound => bound == DungeonExplicitDropPolicy.TriggerDenominator ? roll : 0, DateTimeOffset.UnixEpoch);
            seedSuccesses += generated.Count(row => row is { Item.ItemId: PandemoniumRegionalDropPolicy.BirthSeedId });
            radianceSuccesses += generated.Count(row => row is { Item.ItemId: PandemoniumRegionalDropPolicy.DimensionalRadianceId });
            That(generated.All(row => row is { Item: { ItemId: PandemoniumRegionalDropPolicy.BirthSeedId or PandemoniumRegionalDropPolicy.DimensionalRadianceId,
                Count: 1 } }), "芽与次元光辉保持真实材料核心及单件数量");
        }
        That(seedSuccesses == DungeonExplicitDropPolicy.TriggerThreshold &&
            radianceSuccesses == PandemoniumRegionalDropPolicy.DimensionalRadianceTriggerThreshold && plan.MaximumObjects == 2,
            "地轨中心芽与次元光辉沿显式触发边界独立抽样，失败不保底、不截断、不伪造原版概率");
        var harlemDefinition = definition with { Id = 291100268, Path = "dungeon/harlem/borderquarantine/borderquarantine.dgn" };
        var harlemEntry = entry with { DungeonId = 291100268 };
        var harlemPolicy = new PandemoniumRegionalDropPolicy(_ => harlemDefinition, Item, special);
        That(harlemPolicy.Applies(harlemEntry) && !harlemPolicy.AppliesDimensionalRadiance(harlemEntry),
            "哈林地区适用诞生之芽与卡片，但不适用地轨中心次元光辉");
        var harlemPlan = harlemPolicy.Extend(original, harlemEntry, monster);
        That(harlemPlan.Trials.Length == 1 &&
            harlemPlan.Trials[0].Items?.Entries is [{ Template.Id: PandemoniumRegionalDropPolicy.BirthSeedId }],
            "哈林普通及剧情怪只追加诞生之芽，不混入次元光辉");
        Reject<InvalidDataException>(() => new PandemoniumRegionalDropPolicy(_ => definition,
            id => id == PandemoniumRegionalDropPolicy.DimensionalRadianceId ? radiance with { Rarity = 2 } : Item(id), special)
            .Extend(original, entry, monster));
        for (byte difficulty = 0; difficulty < 5; difficulty++)
        {
            var current = policy.Extend(original, entry with { Difficulty = difficulty }, monster);
            That(current.Trials.Length == (difficulty == 0 ? 2 : 3), "卡片零概率不补默认值");
            if (difficulty == 0) continue;
            var trial = current.Trials[2];
            That(trial.Threshold == declaration.Probabilities[difficulty] && trial.Attempts == declaration.Attempts[difficulty] &&
                trial.ProbabilityDenominator == 1000000 && trial.Category == DungeonDropCategory.MonsterItems,
                "卡片逐档保留原概率、次数与单卡来源");
            var cardPlan = new DungeonDropGenerationPlan("卡片边界", [trial]);
            That(cardPlan.Generate(bound => bound == 1000000 ? trial.Threshold - 1 : 0, DateTimeOffset.UnixEpoch).Length == trial.Attempts &&
                (trial.Threshold == 1000000 || cardPlan.Generate(bound => bound == 1000000 ? trial.Threshold : 0, DateTimeOffset.UnixEpoch).IsEmpty),
                "卡片真实抽样下界命中和排他上界正确");
        }
        foreach (var excluded in new[] { monster with { IsFriendly = true }, monster with { IsBlocking = false },
            monster with { IsDummyMapBoss = true }, monster with { SpawnType = 4 },
            monster with { Source = monster.Source! with { ScriptedRetirement = true } },
            monster with { Source = monster.Source! with { RankKind = "[boss]", Modifiers = ["[dummy]"] } },
            monster with { HellWave = new(2, 216, 42, 90, 0, 10, 0) } })
            That(ReferenceEquals(policy.Extend(original, entry, excluded), original), "友方、退场、假Boss和深渊演员不能借地区材料规则领奖");
        That(!policy.Applies(entry with { IsPractice = true }) && !policy.Applies(entry with { IsQuestReplay = true }),
            "练习及已完成任务回放不生成地区奖励");
        foreach (var other in new[] { definition with { Path = "dungeon/harlem_assault/a.dgn" },
            definition with { Path = "dungeon/levelacc/harlem/a.dgn" }, definition with { Path = "dungeon/other/a.dgn" },
            definition with { DungeonType = "particular hell" }, definition with { DungeonType = "disaster dungeon" } })
            That(!new PandemoniumRegionalDropPolicy(_ => other, Item, special).Applies(entry), "特殊模式不按名称或编号借普通地区规则");
        var preserved = new DungeonDropGenerationPlan("已有物品", [new(1, new DungeonItemDropPool([new DungeonWeightedDropItem(card, 1)]), 1, 1,
            ProbabilityDenominator: 1)]) { OrdinaryLukeDailySource = 123 };
        var extended = policy.Extend(preserved, entry, monster);
        That(extended.Trials[0] == preserved.Trials[0] && extended.OrdinaryLukeDailySource == 123 && original.Trials.IsEmpty,
            "地区追加不覆盖通用掉落、不可变原计划或原奖励来源");
        Console.WriteLine($"PASS {Count - before} 魔界哈林地区材料与卡片规则断言");
    }

    public static void RunResources(PvfArchive pvf, DungeonCatalog dungeons, DungeonRoomCatalog rooms, ItemResources items)
    {
        int before = Count;
        var special = items.DungeonSpecialDrops;
        var regional = new PandemoniumRegionalDropPolicy(dungeons.Get, items.Items.Get, special);
        var monsterDrops = DungeonMonsterDropCatalog.Load(pvf);
        var resource = new DungeonResourceDropPolicy(Testing.SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            monsterDrops.Get, items.Items.Get, special, regional: regional);
        var directory = DungeonCatalog.ParseDirectory(pvf.ReadTokens("dungeon/dungeon.lst"), pvf.ResolveString, "dungeon");
        var selected = directory.Where(pair => pair.Value.StartsWith("dungeon/metrocenter/", StringComparison.Ordinal) ||
            pair.Value.StartsWith("dungeon/harlem/", StringComparison.Ordinal))
            // 灾难入口5340是专用占位资源，不要求普通目录为其构造maze。
            .Where(pair => !new PvfDocument(pvf.ReadText(pair.Value)).Has("dungeon type"))
            .Select(pair => dungeons.Get(pair.Key)).ToArray();
        int regionCount = 0, metroCenterCount = 0, mapCount = 0;
        var monsterIds = new HashSet<int>();
        var cardIds = new HashSet<int>();
        foreach (var dungeon in selected)
        {
            var first = dungeon.Mazes.FirstOrDefault(maze => maze.MapSpecification is { Count: > 0 });
            if (first is null) continue;
            var initial = first.MapSpecification![0];
            var entry = new DungeonEntryPlan(1, 0, checked((uint)dungeon.Id), 0, 0, 0, first, initial, initial)
                { BasisLevel = dungeon.BasisLevel };
            if (!regional.Applies(entry)) continue;
            bool metroCenter = dungeon.Path.StartsWith("dungeon/metrocenter/", StringComparison.OrdinalIgnoreCase);
            That(regional.AppliesDimensionalRadiance(entry) == metroCenter,
                "地轨中心与哈林的次元光辉区域边界与DGN路径一致");
            regionCount++;
            if (metroCenter) metroCenterCount++;
            var prepare = Enumerable.Range(0, 5).Select(difficulty =>
                resource.PrepareEntry(entry with { Difficulty = checked((byte)difficulty) })).ToArray();
            var dungeonCards = new HashSet<int>();
            var maps = dungeon.Mazes.SelectMany(maze => maze.MapSpecification ?? []).DistinctBy(map => map.Id).ToArray();
            foreach (var map in maps)
            {
                var room = rooms.Get(map, requirePassiveDefinitions: false);
                mapCount++;
                foreach (var source in room.Monsters)
                {
                    monsterIds.Add(source.MonsterId);
                    var monster = new DungeonPlannedMonster(0, 10, source.MonsterId, dungeon.BasisLevel ?? 95, source)
                        { SourceMap = map, IsFriendly = source.Team is >= 0 and < 100,
                            SpawnType = source.RankKind == "[boss]" ? (byte)3 : source.IsNamed ? (byte)2 : (byte)0 };
                    if (!PandemoniumRegionalDropPolicy.CanReward(monster)) continue;
                    for (byte difficulty = 0; difficulty < 5; difficulty++)
                    {
                        var plan = prepare[difficulty](monster);
                        That(plan.Trials.Any(trial => trial.Items?.Entries.Any(item => item.Template.Id == 10161610) == true),
                            "地区真实普通及剧情MAP怪物接入芽生成计划");
                        That(plan.Trials.Any(trial => trial.Threshold == PandemoniumRegionalDropPolicy.DimensionalRadianceTriggerThreshold &&
                            trial.ProbabilityDenominator == DungeonExplicitDropPolicy.TriggerDenominator &&
                            trial.Category == DungeonDropCategory.Materials &&
                            trial.Items?.Entries is [{ Template.Id: PandemoniumRegionalDropPolicy.DimensionalRadianceId }]) == metroCenter,
                            "次元光辉仅接入地轨中心真实普通及剧情MAP合法怪物，不扩散到哈林");
                        if (!special.CardsByMonster.TryGetValue(source.MonsterId, out var cards)) continue;
                        foreach (var card in cards.Where(card => card.ListFlag == 3 && card.Probabilities[difficulty] > 0 && card.Attempts[difficulty] > 0))
                        {
                            That(plan.Trials.Any(trial => trial.ProbabilityDenominator == 1000000 && trial.Threshold == card.Probabilities[difficulty] &&
                                trial.Items?.Entries.Any(item => item.Template.Id == card.ItemId) == true), "地区真实卡片声明进入生产计划");
                            cardIds.Add(card.ItemId);
                            dungeonCards.Add(card.ItemId);
                        }
                    }
                }
            }
            Console.WriteLine($"REGIONAL dungeon={dungeon.Id} path={dungeon.Path} maps={maps.Length} cards={string.Join(',', dungeonCards.Order())}");
        }
        That(metroCenterCount == 7 && regionCount == 16 && monsterIds.Count > 0 && cardIds.Count > 0,
            "地轨中心7个与哈林9个普通/剧情副本及非空卡片来源已核对");
        That(items.Items.Get(10161610) is { Kind: ItemKind.Material, Grade: 1, CreationRate: 0 } &&
            items.Items.Get(10163136) is { Kind: ItemKind.Material, Grade: 1, Rarity: 6, CreationRate: 0 },
            "真实诞生之芽与次元光辉身份复现通用候选缺口");
        Console.WriteLine($"PASS {Count - before} regional drops: dungeons={regionCount} maps={mapCount} monsters={monsterIds.Count} cards={cardIds.Count}");
    }
}
