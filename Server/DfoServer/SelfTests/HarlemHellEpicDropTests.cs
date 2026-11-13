using System.Text.RegularExpressions;
using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class HarlemHellEpicDropTests
{
    public static void RunCore(DungeonDropRuleCatalog dropRules, DungeonEntryPlan basis)
    {
        int before = Count;
        const string source = """
            [drop area] 54
            [drop epic item list] 1 9000 2 9500 3 [/drop epic item list]
            [drop epic item list] 2 9500 7 9501 11 [/drop epic item list]
            [drop epic item list] 3 9600 13 [/drop epic item list]
            [/drop area]
            """;
        static DungeonHellEpicCatalog Parse(string text)
        {
            var tags = new Dictionary<int, string>();
            var tokens = new List<PvfToken>();
            foreach (Match match in Regex.Matches(text, @"\[[^\]]+\]|-?\d+"))
            {
                if (match.Value[0] != '[') tokens.Add(new(0, int.Parse(match.Value)));
                else { int key = tags.Count; tags.Add(key, match.Value); tokens.Add(new(3, key)); }
            }
            return DungeonHellEpicCatalog.Parse(tokens, key => tags[key]);
        }
        ItemTemplate Gear(int id, byte level, int grade, int creation) => new(id, ItemKind.Equipment, 0,
            level, 37, 1, 1, 4, grade, "[free]", ushort.MaxValue, 0, "合成深渊史诗") { CreationRate = creation };
        var old = Gear(8500, 85, 1, 5);
        var normal = Gear(9000, 90, 97, 1);
        var first = Gear(9500, 95, 101, 0);
        var second = Gear(9501, 95, 101, 0);
        var templates = new[] { old, normal, first, second }.ToDictionary(item => item.Id);
        var catalog = Parse(source);
        var ordinaryPool = catalog.Pool(54, 1, id => templates[id]);
        var legendPool = catalog.Pool(54, 2, id => templates[id]);
        That(catalog.Areas[54][3].Single() == new DungeonSpecialDropItem(9600, 13) &&
            ordinaryPool.TotalWeight == 5 && ordinaryPool.Select(1).Id == normal.Id && ordinaryPool.Select(2).Id == first.Id &&
            legendPool.Select(6).Id == first.Id && legendPool.Select(7).Id == second.Id,
            "专表保留未消费列表；正权重区间严格按2/3与7/11选择，不用creation rate或grade删候选");
        foreach (string invalid in new[] { "", source + source, source.Replace("54", "0"), source.Replace("9500 3", "9000 3"),
            source.Replace("9500 3", "9500 0"), source.Replace("9500 3", "9500 -1"),
            source.Replace("9000 2", "9000 2147483647"), source.Replace("9500 3", "9500"),
            source.Replace("[/drop area]", ""), source.Replace("[/drop epic item list]", ""),
            source.Replace("3 9600", "2 9600"), source.Replace("[drop area]", "[unknown]"),
            source + "[/drop area]", source.Replace("9000 2", "[unknown] 9000 2") })
            Reject<InvalidDataException>(() => Parse(invalid), "专表拒绝空、重复、残缺、未知结构及非法权重，不回退旧池");
        Reject<InvalidDataException>(() => catalog.Pool(123, 1, id => templates[id]), "缺少映射区域明确拒绝");
        Reject<InvalidDataException>(() => catalog.Pool(54, 4, id => templates[id]), "缺少映射列表明确拒绝");
        Reject<KeyNotFoundException>(() => catalog.Pool(54, 3, id => templates[id]), "不存在的物品不能被悄悄跳过");
        foreach (var invalid in new[] { first with { Id = 9999 }, first with { Kind = ItemKind.Material },
            first with { Rarity = 3 }, first with { StackLimit = 2 } })
            Reject<InvalidDataException>(() => catalog.Pool(54, 2, id => id == first.Id ? invalid : templates[id]),
                "显式史诗列表不能混入身份错误、非装备、非史诗或堆叠候选");
        var local = new HarlemHellEpicDropPolicy([1], ordinaryPool);
        Reject<InvalidDataException>(() => new HarlemHellEpicDropPolicy([1], ordinaryPool, cosmoPool: ordinaryPool), "超星空不接受90级候选");
        Reject<InvalidDataException>(() => new HarlemHellEpicDropPolicy([1], new DungeonItemDropPool(new[] { old })),
            "本服哈林普通规则不接受80/85级史诗");
        var natural = DungeonItemDropCatalog.Create([old, normal with { Id = 8000, MinimumLevel = 1, Rarity = 0, Grade = 1, CreationRate = 1 }]);
        var resource = new DungeonResourceDropPolicy(dropRules, natural, id => new(id, "monster/合成.mob", null, null),
            id => templates[id], harlemEpics: local);
        var hellRules = DungeonHellDropRules.Parse("""
            [dungeon difficulty drop prob] 1 2 199 199 199 199 199
            [basis of rarity dicision] 1 100000 200000 300000 400000 800000 900000 1000000
            [item drop ref table] 1 0 2
            """);
        var hell = new DungeonHellRewardPolicy(hellRules, natural);
        var entry = basis with { HellParty = new(1, basis.StartMap.Position) };
        var monster = new DungeonPlannedMonster(0, 10, 42, 1, null) { SpawnType = 5, HellRewardRolls = 2 };
        var prepare = resource.PrepareEntry(entry, hell);
        var plan = prepare(monster);
        var star = resource.PrepareEntry(entry with { IsHellLegend = true }, hell)(monster);
        var legacy = new DungeonResourceDropPolicy(dropRules, natural, id => new(id, "monster/合成.mob", null, null),
            id => templates[id]).PrepareEntry(entry, hell)(monster);
        That(ReferenceEquals(plan.Trials.Single().RarityPools[4].Items, ordinaryPool) &&
            ReferenceEquals(star.Trials.Single().RarityPools[4].Items, ordinaryPool) &&
            ReferenceEquals(plan, prepare(monster with { ObjectKey = 11 })) &&
            plan.ResourceSource.Contains(HarlemHellEpicDropPolicy.Rule),
            "入图一次准备普通/星空专表候选，重复演员复用不可变计划并保留本服来源");
        var trial = plan.Trials.Single();
        var previous = legacy.Trials.Single();
        That(trial.Threshold == 200 && trial.ProbabilityDenominator == 1001 && trial.Attempts == 2 &&
            trial.RarityPools.Select(row => row.Threshold).SequenceEqual(previous.RarityPools.Select(row => row.Threshold)) &&
            trial.RarityPools.Where((_, i) => i != 4).All(row => row.Items.Entries.IsEmpty) &&
            previous.RarityPools[0].Items.TotalWeight > 0,
            "保留命中200/1001、两次骰与所有稀有度阈值，同时排除非史诗装备及白装回退候选");
        That(plan.Generate(_ => 0, DateTimeOffset.UnixEpoch).IsEmpty &&
            star.Generate(_ => 0, DateTimeOffset.UnixEpoch).IsEmpty &&
            legacy.Generate(_ => 0, DateTimeOffset.UnixEpoch).Length == 2,
            "普通与星空末波命中非史诗稀有度时不掉装备，不重抽成史诗；未映射旧模式保留原掉落");
        int calls = 0;
        var generated = plan.Generate(bound => { calls++; return bound switch { 1001 => 199, 1000000 => 400000, 5 => 2, _ => 0 }; },
            DateTimeOffset.FromUnixTimeSeconds(1800000000));
        That(generated.Length == 2 && generated.All(row => row.Item!.ItemId == first.Id) && calls == 8,
            "概率及稀有度精确边界命中后，grade101/creation0装备沿原创建通道生成，两次骰不变");
        int misses = 0;
        That(plan.Generate(bound => { misses++; return 200; }, DateTimeOffset.UtcNow).IsEmpty && misses == 2,
            "命中阈值处不掉落，只保留每骰一次概率抽样");
        foreach (var outside in new[] { entry with { DungeonId = 2 }, entry with { IsPractice = true },
            entry with { HellParty = null }, entry with { IsTutorial = true }, entry with { RouteQuestId = 10 } })
            That(!resource.PrepareEntry(outside, hell)(monster).ResourceSource.Contains(HarlemHellEpicDropPolicy.Rule),
                "其它区域、练习、普通无深渊图、教程及任务路线不接本服史诗映射");
        That(prepare(monster with { HellRewardRolls = 0 }).Trials.IsEmpty &&
            !natural.ReferencePool(true, 1, 101, 4).Entries.Any(row => row.Template.Id == first.Id),
            "前序波次不额外奖励，专表creation0候选不会被加入全局自然池");
        VerifyBranches(dropRules, basis, normal, first, second);
        Console.WriteLine($"PASS {Count - before} 哈林末波史诗专表与候选策略断言");
    }

    static void VerifyBranches(DungeonDropRuleCatalog dropRules, DungeonEntryPlan basis, ItemTemplate old, ItemTemplate harlem, ItemTemplate tayberrs)
    {
        var rules = HarlemHellEpicRules.Parse("{}");
        That(rules.NormalWeaponPermille == 10 && rules.LegendRareEpicPermille == 0 && rules.CosmoTayberrsPermille == 100 && rules.CosmoSuperPermille == 100,
            "普通及星空共用1%苍穹条件分支；星空稀有额外奖励默认关闭，超星空使用80/10/10条件分支");
        Reject<InvalidDataException>(() => HarlemHellEpicRules.Parse("null"), "史诗规则不接受空配置");
        Reject<InvalidDataException>(() => HarlemHellEpicRules.Parse("{\"NormalWeaponPermille\":1001}"), "分支概率不截断");
        Reject<JsonException>(() => HarlemHellEpicRules.Parse("{\"LegendTayberrsPercent\":5}"), "配置字段拼错明确拒绝");
        Reject<JsonException>(() => HarlemHellEpicRules.Parse("{\"LegendTayberrsPermille\":50}"), "旧星空条件概率字段明确拒绝，不能默认为每只稀有怪的绝对概率");
        Reject<InvalidDataException>(() => HarlemHellEpicRules.Parse("{\"LegendRareEpicPermille\":-1}"), "稀有怪额外奖励不接受负概率");
        Reject<InvalidDataException>(() => HarlemHellEpicRules.Parse("{\"LegendRareEpicPermille\":1001}"), "稀有怪额外奖励不接受越界概率");
        Reject<InvalidDataException>(() => HarlemHellEpicRules.Parse("{\"CosmoSuperPermille\":-1}"), "超界分支拒绝负概率");
        Reject<InvalidDataException>(() => HarlemHellEpicRules.Parse("{\"CosmoSuperPermille\":901}"), "超界与泰波尔斯条件分支之和不得超过1000");
        var normal = new DungeonItemDropPool(new[] { new DungeonWeightedDropItem(old, 2), new(harlem, 3) });
        var level95 = new DungeonItemDropPool(new[] { new DungeonWeightedDropItem(harlem, 7) });
        var alternate = new DungeonItemDropPool(new[] { new DungeonWeightedDropItem(tayberrs, 11) });
        foreach (var invalid in new[] { new DungeonDropRarityPool(1000000, level95, AlternatePermille: 1),
            new DungeonDropRarityPool(1000000, level95, alternate, 1001),
            new DungeonDropRarityPool(1000000, level95, SecondaryPermille: 1),
            new DungeonDropRarityPool(1000000, level95, alternate, 600, alternate, 401),
            new DungeonDropRarityPool(1000000, level95, new DungeonItemDropPool(Array.Empty<ItemTemplate>()), 50) })
            Reject<InvalidDataException>(() => new DungeonDropGenerationPlan("合成非法条件分支",
                [new(1000, null, 1, 1, RarityPools: [invalid])]), "条件分支拒绝缺候选、空候选或范围外概率");
        var superItem = tayberrs with { Id = 8201 };
        var superPool = new DungeonItemDropPool(new[] { new DungeonWeightedDropItem(superItem, 13) });
        var map = basis.BossMap with { Id = 92690, Path = "map/合成超星空终点.map", Position = new(2, 0) };
        var source = new DungeonRoomMonster(42, [42, 1, 0, 100, 100, 0, 1, 1], "[fixed]", "[boss]", "monster/合成超星空领主.mob");
        var cosmo = new DungeonCosmoRewardCatalog(2, 4, [new(map, [source], [], [])]);
        var local = new HarlemHellEpicDropPolicy([1], normal, alternate, alternate, level95, rules, cosmo, superPool);
        var candidates = DungeonItemDropCatalog.Create([old with { CreationRate = 1 }]);
        var resource = new DungeonResourceDropPolicy(dropRules, candidates, id => new(id, "monster/合成.mob", null, null),
            id => id == harlem.Id ? harlem : tayberrs, harlemEpics: local);
        var hell = new DungeonHellRewardPolicy(DungeonHellDropRules.Parse("""
            [dungeon difficulty drop prob] 1 2 199 199 199 199 199
            [basis of rarity dicision] 1 0 0 0 0 1000000 1000000 1000000
            [item drop ref table] 1 0 2
            """), candidates);
        WhiteCosmoDropTests.RunCore(basis, hell, normal, alternate, level95, superPool);
        var wave = new DungeonPlannedMonster(0, 10, 42, 1, null) { SpawnType = 5, HellRewardRolls = 2 };
        var normalEntry = basis with { DungeonId = 1, BasisLevel = 1, HellParty = new(1, basis.StartMap.Position), HellPartyMode = 1 };
        var ultraEntry = normalEntry with { DungeonId = 7191, Difficulty = 4, BossMap = map, HellParty = null };
        var boss = wave with { Source = source, SpawnType = 3, HellRewardRolls = 0 };
        var starEntry = normalEntry with { IsHellLegend = true };
        var rare = wave with { MonsterId = 107000276, SpawnType = 0,
            Source = new(107000276, [107000276, 0, 1, 0, 0, 0, 0, 0], "[fixed]", "[normal]", ""),
            HellWave = new(2, 219, 107000276, 1, 0, wave.ObjectKey, 0) };
        That(local.IsLegendRare(starEntry, rare) && !local.IsLegendRare(normalEntry, rare) &&
            !local.ExtendLegendRare(new("合成空计划", []), starEntry, rare).Trials.Any(),
            "稀有怪来源可识别，但默认关闭时不追加奖励或消耗额外随机数");
        var extraPolicy = new HarlemHellEpicDropPolicy([1], normal, alternate, alternate, level95,
            rules with { LegendRareEpicPermille = 50 }, cosmo, superPool);
        var extraResource = new DungeonResourceDropPolicy(dropRules, candidates, id => new(id, "monster/合成.mob", null, null),
            id => tayberrs, harlemEpics: extraPolicy);
        var cachedRare = extraResource.PrepareEntry(starEntry, hell);
        foreach (var invalid in new[] { rare with { HellWave = null }, rare with { IsFriendly = true },
            rare with { IsBlocking = false }, rare with { IsDummyMapBoss = true }, rare with { Source = source },
            rare with { HellWave = rare.HellWave! with { GroupId = 222 } },
            rare with { HellWave = rare.HellWave! with { Order = 1 } },
            rare with { HellWave = rare.HellWave! with { ObjectKey = 11 } },
            rare with { MonsterId = 107013803 } })
        {
            That(!extraPolicy.IsLegendRare(starEntry, invalid) &&
                !cachedRare(invalid).ResourceSource.Contains(HarlemHellEpicDropPolicy.RareRule),
                "相同模板的其它来源、跨组、非敌对及超星空变体不借稀有奖励，缓存区分合法来源");
        }
        foreach (int group in Enumerable.Range(219, 6))
        {
            int id = group <= 221 ? 107000276 : 107000277;
            var actor = rare with { MonsterId = id, Source = rare.Source! with { MonsterId = id },
                HellWave = rare.HellWave! with { GroupId = group, MonsterId = id } };
            That(extraPolicy.IsLegendRare(starEntry, actor), "六个真实星空候选组分别识别对应稀有怪");
        }
        foreach (int rolls in new[] { 0, 2 })
        {
            var plan = cachedRare(rare with { HellRewardRolls = rolls });
            var extra = plan.Trials.Single(trial => ReferenceEquals(trial.Items, alternate));
            That(extra.Attempts == 1 && extra.Threshold == 50 && extra.ProbabilityDenominator == 1000 &&
                extra.MinimumCount == 1 && extra.MaximumCount == 1 && extra.Category == DungeonDropCategory.Equipment,
                "提前击杀与实际末杀都保留一份独立稀有奖励，不随末杀骰数复制");
            foreach (decimal factor in new[] { 0m, 1m, 2m })
            {
                var adjusted = DungeonDropControlSnapshot.Apply(new(plan.ResourceSource, [extra]), new(Equipment: factor, Special: 100));
                That(adjusted.Trials[0].Threshold == 50 * factor &&
                    adjusted.Generate(bound => bound == 1000 ? 99 : 0, DateTimeOffset.UnixEpoch).Length == (factor == 2 ? 1 : 0),
                    "星空稀有怪装备接入Equipment，Special不再替代或叠乘，零关闭");
            }
            var isolated = new DungeonDropGenerationPlan(plan.ResourceSource, [extra]);
            for (int draw = 0; draw < 1000; draw++)
                That(isolated.Generate(bound => bound == 1000 ? draw : 0, DateTimeOffset.UnixEpoch).Length == (draw < 50 ? 1 : 0),
                    "测试配置5%按每只稀有怪绝对概率独立判定，不再依赖装备或稀有度命中");
        }
        foreach (var (entry, monster, threshold) in new[] { (normalEntry, wave, 10),
            (normalEntry with { IsHellLegend = true }, wave, 10), (ultraEntry, boss, 100) })
        {
            var plan = resource.PrepareEntry(entry, hell)(monster);
            var trial = plan.Trials.Last();
            That(trial.Threshold == 200 && trial.Attempts == 2 && trial.ProbabilityDenominator == 1001 &&
                trial.RarityPools[4].AlternatePermille == threshold && ReferenceEquals(trial.RarityPools[4].AlternateItems, alternate),
                "三种模式保留装备概率与骰数；只改变史诗命中后的专表分支");
            var isolated = new DungeonDropGenerationPlan(plan.ResourceSource, [trial]);
            for (int draw = 0; draw < 1000; draw++)
            {
                var results = isolated.Generate(bound => bound == 1000 ? draw : 0, DateTimeOffset.UnixEpoch);
                That(results.Length == 2 && results.All(row => row.Item!.ItemId ==
                    (draw < threshold ? tayberrs.Id : entry.DungeonId == 7191 ? (draw < 200 ? superItem.Id : harlem.Id) : old.Id)),
                    "普通及星空1%、超星空80/10/10条件分支每个边界精确覆盖，超星空不出90史诗");
            }
        }
        var ordinary = Enumerable.Range(0, 7).Where(rarity => rarity != 4)
            .Select(rarity => old with { Id = 8000 + rarity, Rarity = rarity, Grade = 1, CreationRate = 1 }).ToArray();
        var superRules = rules with { CosmoSuperPermille = 200 };
        Reject<InvalidDataException>(() => new HarlemHellEpicDropPolicy([1], normal, alternate, alternate, level95,
            superRules, cosmo), "非零超界概率不能静默忽略缺失候选池");
        Reject<InvalidDataException>(() => new HarlemHellEpicDropPolicy([1], normal, alternate, null, level95,
            rules, cosmo, superPool), "非零泰波尔斯概率不能静默忽略缺失候选池");
        var superPolicy = new HarlemHellEpicDropPolicy([1], normal, alternate, alternate, level95, superRules, cosmo, superPool);
        var superPlan = superPolicy.Extend(new("合成超界条件分支", []), ultraEntry, boss, hell);
        var superTrial = superPlan.Trials.Last();
        That(superTrial.Attempts == 2 && superTrial.Threshold == 200 && superTrial.ProbabilityDenominator == 1001,
            "启用超界不增加装备骰数或修改装备命中概率");
        var superIsolated = new DungeonDropGenerationPlan(superPlan.ResourceSource, [superTrial with { Attempts = 1 }]);
        for (int draw = 0; draw < 1000; draw++)
        {
            var result = superIsolated.Generate(bound => bound == 1000 ? draw : 0, DateTimeOffset.UnixEpoch);
            That(result is [{ Item: { } item }] && item.ItemId == (draw < 100 ? tayberrs.Id : draw < 300 ? superItem.Id : harlem.Id),
                "三分支同一千分比抽样互斥选择，超界占比从哈林划出且各池权重不拼接");
        }
        var materialItem = harlem with { Id = 8100, Kind = ItemKind.Material, Rarity = 0, Grade = 1, CreationRate = 1 };
        var mixedItems = ordinary.Concat([old with { Grade = 1, CreationRate = 1 }, harlem with { Grade = 1, CreationRate = 1 }, materialItem])
            .ToDictionary(item => item.Id);
        var mixedResource = new DungeonResourceDropPolicy(dropRules, DungeonItemDropCatalog.Create(mixedItems.Values),
            id => new(id, "monster/合成混合掉落.mob", mixedItems.Keys.Select(item => new DungeonMonsterDropEntry(item, 1)).ToArray(), null),
            id => mixedItems[id], harlemEpics: local);
        foreach (var entry in new[] { normalEntry, normalEntry with { IsHellLegend = true }, ultraEntry })
        {
            var filtered = mixedResource.PrepareEntry(entry, hell)(wave with { SpawnType = 0, HellRewardRolls = 0 });
            var direct = filtered.Trials.Last().Items!;
            That(direct.Entries.Select(row => row.Template.Id).Order().SequenceEqual(
                    (entry.DungeonId == 7191 ? new[] { harlem.Id, materialItem.Id } : new[] { old.Id, harlem.Id, materialItem.Id }).Order()) &&
                filtered.Trials.SelectMany(trial => trial.RarityPools.IsDefaultOrEmpty ? [] : trial.RarityPools)
                    .SelectMany(row => row.Items.Entries).All(row => row.Template.Kind != ItemKind.Equipment ||
                        row.Template.Rarity == 4 && (entry.DungeonId != 7191 || row.Template.MinimumLevel == 95)),
                "三种深渊普通怪的通用与专项掉落均排除非史诗装备，保留材料及模式允许的90/95史诗");
            var generated = new DungeonDropGenerationPlan(filtered.ResourceSource, [filtered.Trials.Last()])
                .Generate(_ => 0, DateTimeOffset.UnixEpoch);
            That(generated.Single().Item!.ItemId == (entry.DungeonId == 7191 ? harlem.Id : old.Id),
                "过滤后的混合专项池仍沿原生成流程掉落史诗，超星空不出90级史诗");
        }
        var ordinaryEntry = mixedResource.PrepareEntry(normalEntry with { HellParty = null }, hell)
            (wave with { SpawnType = 0, HellRewardRolls = 0 });
        That(ordinaryEntry.Trials.Last().Items!.Entries.Length == mixedItems.Count,
            "未选择深渊的普通副本继续保留通用及专项普通装备掉落");
        foreach (int permille in new[] { 0, 1000 })
        {
            var configured = new HarlemHellEpicDropPolicy([1], normal, alternate, alternate, level95,
                rules with { NormalWeaponPermille = permille }, cosmo, superPool);
            var plan = configured.Extend(hell.Extend(new("合成装备", []), 1, 1, 1, suppressEpicFragments: true, epicEquipment: normal),
                normalEntry, wave, hell);
            var result = plan.Generate(_ => 0, DateTimeOffset.UnixEpoch);
            That(result.Single().Item!.ItemId == (permille == 0 ? old.Id : tayberrs.Id), "配置0/1000准确关闭普通分支或基础池");
        }
        foreach (var invalid in new[] { ultraEntry with { IsPractice = true }, ultraEntry with { DungeonId = 530 },
            ultraEntry with { Difficulty = 0 }, ultraEntry with { RouteQuestId = 7 }, ultraEntry with { BossMap = map with { Id = 1 } } })
            That(!local.IsCosmoBoss(invalid, boss), "超星空只认固定难度正式路线及真实终点，不能借其它模式发奖");
        That(!local.IsCosmoBoss(ultraEntry, boss with { IsFriendly = true }) &&
            !local.IsCosmoBoss(ultraEntry, boss with { IsDummyMapBoss = true }) &&
            !local.IsCosmoBoss(ultraEntry, boss with { Source = source with { RawValues = [42] } }),
            "同模板友方、dummy和不同资源行不借超星空终点领奖");
        var cached = resource.PrepareEntry(ultraEntry, hell);
        That(cached(boss).Trials.Length == cached(boss with { Source = source with { RawValues = [42] } }).Trials.Length + 1 &&
            ReferenceEquals(cached(boss), cached(boss with { ObjectKey = 11 })),
            "超星空缓存区分真实终点来源，同时让相同合法演员复用不可变计划");
        var material = new HarlemMaterialDropPolicy(new(), [1], id => new(id, ItemKind.Material, 0, 1, 0, 100, 1, 0, 1,
            "[free]", ushort.MaxValue, 0, "合成材料"), local);
        var materials = material.Extend(new("合成无掉落", []), ultraEntry, boss).Generate(_ => 0, DateTimeOffset.UnixEpoch);
        That(materials.Where(row => row.Item!.ItemId == HarlemMaterialDropPolicy.FragmentId).ToArray() is { Length: 30 } cosmoFragments &&
            cosmoFragments.All(row => row.Item!.Count == 1) &&
            materials.Where(row => row.Item!.ItemId == HarlemMaterialDropPolicy.SkyWishId).ToArray() is [{ Item.Count: 30 }],
            "超星空固定难度下逐个生成碎片30、单堆生成天空之愿30，材料翻倍不增加装备骰数");
    }

    public static HarlemHellEpicDropPolicy RunResources(PvfArchive pvf, ItemResources items, uint[] harlem)
    {
        var catalog = DungeonHellEpicCatalog.Load(pvf);
        var normal = catalog.Pool(54, 1, items.Items.Get);
        var legend = catalog.Pool(54, 2, items.Items.Get);
        That(catalog.Areas.Keys.Order().SequenceEqual([54, 123]) && catalog.Areas.Values.All(area =>
            area.OrderBy(row => row.Key).Select(row => row.Value.Length).SequenceEqual([122, 91, 37, 74, 25])),
            "真实PVF两个区域的五份专表完整保留，不把额外奖励表合并到普通末波");
        That(normal.Entries.Length == 122 && normal.Entries.Count(row => row.Template.MinimumLevel == 90) == 31 &&
            normal.Entries.Count(row => row.Template is { MinimumLevel: 95, Grade: 101 }) == 91 &&
            legend.Entries.Length == 91 && legend.Entries.All(row => row.Template is { MinimumLevel: 95, Grade: 101 }) &&
            normal.TotalWeight == 31 * 19297 + 91 * 5023 && legend.TotalWeight == 91 * 12500,
            "真实普通池为31件90套及91件95哈林，星空为91件95哈林，累计权重来自专表");
        foreach (var area in catalog.Areas)
        foreach (var list in area.Value)
        {
            var pool = catalog.Pool(area.Key, list.Key, items.Items.Get);
            That(pool.Entries.Length == list.Value.Length, "真实PVF全部显式候选可解析为史诗装备，包含creation0的保留列表");
        }
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        var dungeons = Testing.SelfTestCatalogs.Dungeons(pvf);
        foreach (int group in Enumerable.Range(219, 6))
        {
            int id = group <= 221 ? 107000276 : 107000277;
            That(rooms.HellParty.Entries(group, 'A').Count(row => row.Code == id && row.EntityType == 0) == 1,
                "当前PVF六个星空末波组各自包含唯一绑定稀有怪，资源变化须重新核对");
        }
        var cosmo = DungeonCosmoRewardCatalog.Load(pvf, dungeons, rooms);
        var local = new HarlemHellEpicDropPolicy(harlem, normal, catalog.Pool(54, 3, items.Items.Get),
            catalog.Pool(54, 4, items.Items.Get), legend, cosmo: cosmo, superPool: catalog.Pool(54, 5, items.Items.Get));
        uint[] targets = [291100268, 291100293, 291100308, 291100309, 291100317, 291100319];
        That(targets.All(harlem.Contains), "6080区域候选包含全部六张哈林深渊，实际范围还须核对深渊房间");
        var resource = new DungeonResourceDropPolicy(Testing.SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            _ => throw new InvalidOperationException("APC不应查询普通怪掉落"), items.Items.Get, harlemEpics: local);
        var hell = new DungeonHellRewardPolicy(DungeonHellDropRules.TryLoad(pvf)!, items.DungeonDropItems);
        foreach (uint id in targets)
        foreach (bool star in new[] { false, true })
        {
            var definition = dungeons.Get(checked((int)id));
            var maze = definition.Mazes.First(row => row.HellParty is not null);
            var map = new DungeonMap("normal", maze.HellParty!.Room, maze.HellParty.MapId, "map/合成计划.map");
            var entry = new DungeonEntryPlan(1, 0, id, 0, 0, 0, maze, map, map)
            { BasisLevel = definition.BasisLevel, HellParty = maze.HellParty, HellPartyMode = 1, IsHellLegend = star };
            var monster = new DungeonPlannedMonster(0, 10, 42, definition.BasisLevel!.Value, null)
            { SpawnType = 5, HellRewardRolls = 8 };
            var pool = resource.PrepareEntry(entry, hell)(monster).Trials.Single().RarityPools[4].Items;
            That(ReferenceEquals(pool, normal) && pool.Entries.Any(row => row.Template.MinimumLevel == 95) &&
                pool.Entries.Any(row => row.Template.MinimumLevel == 90),
                "六图真实基础等级93/94/95均不再以旧grade区间排除95末波候选: " + id + ":" + star);
        }
        That(cosmo.BossRooms.Keys.Order().SequenceEqual([92689, 92690, 92697]) && cosmo.Difficulty == 4 && cosmo.RewardRolls == 8 &&
            cosmo.BossRooms.Values.SelectMany(room => room.Monsters).Select(row => row.MonsterId).Order()
                .SequenceEqual(new[] { 65587, 107013805, 109007097 }.Order()),
            "超星空终点从当前PVF解析三个不同领主，骰数来自hellparty档位，不混用星空稀有怪");
        foreach (var room in cosmo.BossRooms.Values)
        {
            var definition = dungeons.Get(7191);
            var entry = new DungeonEntryPlan(1, 0, 7191, 0, 0, 4, definition.Mazes.First(), room.Map, room.Map)
            { BasisLevel = definition.BasisLevel };
            var boss = new DungeonPlannedMonster(0, 10, room.Monsters.Single().MonsterId, 97, room.Monsters.Single()) { SpawnType = 3 };
            var policy = new DungeonResourceDropPolicy(Testing.SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
                id => new(id, "monster/奖励边界.mob", null, null), items.Items.Get, harlemEpics: local);
            var plan = policy.PrepareEntry(entry, hell)(boss);
            var trial = plan.Trials.Last();
            bool white = boss.MonsterId == HarlemHellEpicDropPolicy.WhiteBossId;
            That(trial.Attempts == 8 && trial.RarityPools[4].AlternatePermille == (white ? 1000 : 100) &&
                trial.RarityPools[4].Items.Entries.Length == 91 && trial.RarityPools[4].AlternateItems!.Entries.Length == 74 &&
                trial.RarityPools[4].SecondaryPermille == (white ? 0 : 100) &&
                (white ? trial.RarityPools[4].SecondaryItems is null : trial.RarityPools[4].SecondaryItems!.Entries.Length == 25) &&
                plan.ResourceSource.Contains(HarlemHellEpicDropPolicy.Rule),
                "真实白斯只取泰波尔斯，其余两终点保持80/10/10条件分支");
            var superOnly = new HarlemHellEpicDropPolicy(harlem, normal, local.Weapons, local.Tayberrs, legend,
                new() { CosmoTayberrsPermille = 0, CosmoSuperPermille = 1000 }, cosmo, local.Super);
            var superPlan = superOnly.Extend(new("真实超界候选配置", []), entry, boss, hell);
            var superTrial = superPlan.Trials.Last() with { Attempts = 1 };
            var selectedPool = white ? local.Tayberrs! : local.Super!;
            That(white ? superTrial.RarityPools[4].AlternatePermille == 1000 && superTrial.RarityPools[4].SecondaryItems is null :
                superTrial.RarityPools[4].SecondaryItems!.Entries.Length == 25,
                "白斯专属策略不被其它领主的超界配置覆盖");
            WhiteCosmoDropTests.RunResources(items, local, entry, boss);
            int cursor = 0;
            foreach (var candidate in selectedPool.Entries)
            {
                var isolated = new DungeonDropGenerationPlan(superPlan.ResourceSource, [superTrial]);
                var draws = new Queue<(int Bound,int Value)>([(superTrial.ProbabilityDenominator,0),
                    (DungeonDropRatePlan.RarityDenominator,superTrial.RarityPools[3].Threshold),
                    (1000,0),(selectedPool.TotalWeight,cursor)]);
                var generated = isolated.Generate(bound =>
                {
                    if(draws.Count==0) return 0;
                    var next=draws.Dequeue();
                    That(bound==next.Bound,"史诗稀有度与百万池权重抽样按顺序区分，不能按相同上界混用");
                    return next.Value;
                },DateTimeOffset.Parse("2026-10-23T00:00:00Z"));
                That(draws.Count==0,"实际生成消费全部确定性奖励抽样");
                That(generated is [{ Item: { } item }] && item.ItemId == candidate.Template.Id,
                    "真实领主策略逐件生成白斯泰波尔斯或其它领主超界，不仅检查池中存在：" + candidate.Template.Id);
                cursor = candidate.CumulativeWeight;
            }
        }
        foreach (uint id in new uint[] { 530, 7191 })
        {
            var maze = dungeons.Get(checked((int)id)).Mazes.First();
            var map = new DungeonMap("normal", new(0, 0), 1, "map/合成范围.map");
            var entry = new DungeonEntryPlan(1, 0, id, 0, 0, 0, maze, map, map) { HellParty = maze.HellParty };
            That(maze.HellParty is null && local.Pool(entry) is null,
                "同一区域的黎明与超星空没有普通深渊房间，不接入哈林末波史诗: " + id);
        }
        return local;
    }
}
