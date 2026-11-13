using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LegacyHellDropTests
{
    internal static void RunCore(DungeonDropRuleCatalog rules, DungeonEntryPlan basis,
        EpicPieceDropCatalog fragments, DungeonHellRewardPolicy hell)
    {
        var maps = new Dictionary<int, TownDungeonMap>
        {
            [39] = new(39, "worldmap/metrocenter.wdm", [new(310, 0, false), new(7539, 0, false)]) { HellDungeon = true }
        };
        var towns = new TownCatalog([], new(1, 0, 0, 0), new(1, 0, 0, 0), new(0, [], []), [], new Dictionary<uint, uint[]>(), maps);
        ItemTemplate Item(int id) => new(id, ItemKind.Material, 0, 1, 0, 1000, 1, 0, 1, "[free]", ushort.MaxValue, 0, "合成旧深渊奖励");
        var legacy = new LegacyHellDropPolicy(towns, Item, new());
        var resource = new DungeonResourceDropPolicy(rules, DungeonItemDropCatalog.Create([]),
            id => new(id, "monster/合成.mob", null, null), Item, legacyHell: legacy);
        var entry = basis with { DungeonId = 310, BasisLevel = 90, HellParty = new(1, new(0, 0)), HellPartyMode = 1 };
        var wave = new DungeonPlannedMonster(0, 10, 42, 90, null)
            { SpawnType = 5, HellRewardRolls = 8, HellWave = new(2, 219, 42, 90, 5, 10, 0) };
        var ordinary = resource.PrepareEntry(entry, hell)(wave);
        That(ordinary.Trials.Length == 5 && ordinary.Trials[^1].Items!.Entries.Single().Template.Id == 10159141 &&
            ordinary.Trials[^3] is { Threshold: 2, ProbabilityDenominator: 3, MaximumCount: 2, IndividualObjects: true } &&
            ordinary.Trials[^2] is { Threshold: 10, ProbabilityDenominator: 1000 } && ordinary.Trials[^1].Threshold == 1,
            "旧普通深渊仅追加本服票、魂及当前地区宝珠，不借哈林晶石");
        That(ordinary.Trials[0].ExcludeLegacyHellLowEquipment && ordinary.Trials.Skip(1).All(trial => !trial.ExcludeLegacyHellLowEquipment),
            "只有旧地区普通密门深渊的末波装备额外骰携带白蓝魔封过滤，碎片票魂珠不携带");
        foreach (bool special in new[] { false, true })
        {
            var rift = entry with { DungeonId = 7539, HellParty = null, IsInfernoParty = true,
                Maze = entry.Maze with { IsInfernoSpecial = special } };
            for (byte difficulty = 0; difficulty < 5; difficulty++)
            {
                var result = resource.PrepareEntry(rift with { Difficulty = difficulty }, hell)(wave);
                int expected = special ? 13 : 11;
                That(result.Trials.Length == 2 && result.Trials[^1].Attempts == expected &&
                    result.Trials.All(trial => !trial.ExcludeLegacyHellLowEquipment) &&
                    result.Generate(bound => bound == 1001 ? 1000 : 0, DateTimeOffset.UnixEpoch).Length == expected,
                    "时空裂缝普通11/特殊13片，实际生成数量不随难度改变且不混入票魂宝珠");
            }
        }
        var disabled = new DungeonHellRewardPolicy(DungeonHellDropRules.Parse("""
            [dungeon difficulty drop prob] 90 90 1000 1000 1000 1000 1000
            [basis of rarity dicision] 1 0 0 0 0 1000000 1000001 1000002
            [item drop ref table] 90 6 3
            """), DungeonItemDropCatalog.Create([]), fragments, 0);
        That(resource.PrepareEntry(entry with { DungeonId = 7539, HellParty = null, IsInfernoParty = true }, disabled)(wave).Trials.Length == 1,
            "时空碎片仍遵守显式0关闭，不因固定数量绕过配置");
        foreach (var invalid in new[] { entry with { HellParty = null }, entry with { IsPractice = true },
            entry with { RouteQuestId = 10 }, entry with { IsHellLegend = true }, entry with { IsCosmo = true },
            entry with { DungeonId = 291100308 } })
            That(!resource.PrepareEntry(invalid, hell)(wave).ResourceSource.Contains(LegacyHellDropPolicy.Rule) &&
                resource.PrepareEntry(invalid, hell)(wave).Trials.All(trial => !trial.ExcludeLegacyHellLowEquipment),
                "普通、任务、练习、星空及哈林不借旧地区补全奖励");
        That(!resource.PrepareEntry(entry, hell)(wave with { HellRewardRolls = 0 }).ResourceSource.Contains(LegacyHellDropPolicy.Rule) &&
            !resource.PrepareEntry(entry, hell)(wave with { IsFriendly = true }).ResourceSource.Contains(LegacyHellDropPolicy.Rule),
            "非末波及友方演员不产生补全奖励");
        foreach (var ineligible in new[] { wave with { HellRewardRolls = 0 }, wave with { IsFriendly = true },
            wave with { HellWave = null }, wave with { IsBlocking = false },
            wave with { Source = new DungeonRoomMonster(42, [], "[fixed]", "[boss]", "monster/合成.mob") { Modifiers = ["[dummy]"] } } })
            That(resource.PrepareEntry(entry, hell)(ineligible).Trials.All(trial => !trial.ExcludeLegacyHellLowEquipment),
                "非有效末波末杀不安装额外装备过滤");
        var lowLevelHell = new DungeonHellRewardPolicy(DungeonHellDropRules.Parse("""
            [dungeon difficulty drop prob] 1 1 1000 1000 1000 1000 1000
            [basis of rarity dicision] 1 0 599000 993000 993000 1000000 1000001 1000002
            [item drop ref table] 1 0 2
            """), DungeonItemDropCatalog.Create([]));
        var ordinaryMonster = resource.PrepareEntry(entry with { BasisLevel = 1 }, lowLevelHell)(wave with { SpawnType = 0, CandidateLevel = 1 });
        That(ordinaryMonster.Trials.Count(trial => trial.ExcludeLegacyHellLowEquipment) == 1 &&
            ordinaryMonster.Trials.Where(trial => trial.ExcludeLegacyHellLowEquipment).Single().RarityBonusGroup == DungeonRarityBonusGroup.HellEquipment &&
            ordinaryMonster.Trials.Any(trial => trial.RarityBonusGroup == DungeonRarityBonusGroup.OrdinaryEquipment && !trial.ExcludeLegacyHellLowEquipment),
            "同怪普通装备通道不继承末波额外装备骰的过滤");
    }

    internal static void RunResources(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        int before = Count;
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var legacy = new LegacyHellDropPolicy(towns, items.Items.Get, new(), dungeons.InfernoDungeons);
        var hell = new DungeonHellRewardPolicy(DungeonHellDropRules.TryLoad(pvf)!, items.DungeonDropItems, items.EpicPieceDrops);
        var resource = new DungeonResourceDropPolicy(DungeonDropRuleCatalog.Load(pvf), items.DungeonDropItems,
            id => new(id, "monster/未使用APC普通声明.mob", null, null), items.Items.Get, legacyHell: legacy);
        int checkedDungeons = 0, specialMazes = 0;
        foreach (int id in towns.DungeonMaps.Values.Where(map => map.Id is 15 or 17 or 19 or 32 or 39)
            .SelectMany(map => map.Options).Select(option => option.DungeonId)
            .Concat(dungeons.InfernoDungeons.Select(dungeon => dungeon.Id)).Distinct())
        {
            if (!dungeons.ContainsDungeon(id)) continue;
            var dungeon = dungeons.Get(id);
            var mazes = dungeon.Mazes.Where(maze => maze.QuestConnectionTokens.Count == 0 &&
                (maze.HellParty is not null || dungeon.IsInfernoDungeon)).ToArray();
            if (mazes.Length == 0) continue;
            var first = mazes[0];
            var map = first.MapSpecification!.First();
            var basis = new DungeonEntryPlan(1, 0, (uint)id, 0, 0, 0, first, map, map)
            { BasisLevel = dungeon.BasisLevel, HellPartyMode = 1, HellParty = first.HellParty, IsInfernoParty = dungeon.IsInfernoDungeon };
            if (legacy.EquipmentRange(basis) is not { } range) continue;
            var expectedRange = id switch
            {
                86 or 87 or 92 or 93 or 7542 => (70, 75),
                70 or 71 or 72 or 74 or 75 or 76 or 77 or 7527 or 7528 or 7529 => (75, 80),
                101 => (75, 85),
                102 or 103 or 104 or 190 or 191 or 192 or 193 or 194 or 7530 or 7531 or 7532 or 7533 or 7534 or 7535 => (80, 85),
                310 or 311 or 312 or 313 or 314 or 9611 or 9612 or 9613 or 7539 or 7540 or 7541 => (85, 90),
                _ => throw new InvalidOperationException("未列入旧深渊审计名册: " + id)
            };
            That(range == expectedRange, id + "独立地区等级预期与正式策略相符");
            checkedDungeons++;
            foreach (var maze in mazes)
            {
                var entry = basis with { Maze = maze, HellParty = maze.HellParty };
                var wave = new DungeonPlannedMonster(0, 10, 42, dungeon.BasisLevel!.Value, null)
                { SpawnType = 5, HellRewardRolls = 8, HellWave = dungeon.IsInfernoDungeon ? null : new(2, 219, 42, 90, 5, 10, 0) };
                var plan = resource.PrepareEntry(entry, hell)(wave);
                var equipment = plan.Trials[0].RarityPools[4].Items.Entries;
                That(equipment.Length > 0 && equipment.All(row => row.Template.MinimumLevel >= range.Minimum && row.Template.MinimumLevel <= range.Maximum) &&
                    equipment.Select(row => row.Template.MinimumLevel).Distinct().Order().SequenceEqual(
                        Enumerable.Range(range.Minimum, range.Maximum - range.Minimum + 1).Where(level => level % 5 == 0).Select(level => (byte)level)),
                    id + "每条真实迷宫的史诗池包含完整地区等级，不混入邻区装备");
                var boosted = plan.WithEpicDropBonus(100).Trials[0];
                That(boosted.RarityPools[4].Threshold - boosted.RarityPools[3].Threshold == 14000,
                    id + "真实奖励七档分布接受史诗药剂");
                if (dungeon.IsInfernoDungeon)
                {
                    if (maze.IsInfernoSpecial) specialMazes++;
                    That(plan.Trials.Length == 2 && plan.Trials[1].Attempts == (maze.IsInfernoSpecial ? 13 : 11),
                        id + "真实时空普通/特殊迷宫按标志固定碎片，不混入灵魂和宝珠");
                }
                else
                {
                    var ids = plan.Trials.Skip(2).SelectMany(trial => trial.Items!.Entries).Select(row => row.Template.Id).ToArray();
                    That(ids.Contains(3330) && ids.Contains(10158124) && !ids.Contains(10305940) && !ids.Contains(10088691),
                        id + "真实普通深渊补票魂且不套哈林/先锋战区晶石");
                    int crystal = id switch
                    {
                        86 or 87 or 92 or 93 => 0,
                        70 or 71 or 72 or 74 or 75 or 76 or 77 => 10088689,
                        101 or 102 or 103 or 104 => 10088690,
                        190 or 191 or 192 or 193 or 194 => 10146492,
                        _ => 10159141
                    };
                    That(ids.SequenceEqual(crystal == 0 ? new[] { 3330, 10158124 } : new[] { 3330, 10158124, crystal }),
                        id + "地区宝珠身份与独立审计名册逐一对应");
                }
            }
        }
        Console.WriteLine($"HELLDROP audit dungeons={checkedDungeons} special-mazes={specialMazes}");
        That(checkedDungeons == 41 && specialMazes == 10, "完整覆盖28个旧地区普通深渊和13个时空裂缝子变体、10条特殊迷宫");
        foreach (var (id, count) in new[] { (10088689, 400), (10088690, 450), (10146492, 500), (10159141, 500) })
            That(items.Boosters.Get(id).Pools.Single().Rewards.Single() is { ItemId: 3330 } reward && reward.Count == count,
                "地区宝珠逐一核对本地可开启内容: " + id);
        Console.WriteLine($"PASS {Count - before} 旧地区深渊全路线奖励审计断言");
    }
}
