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
        var cards = new DungeonCardRewardPolicy(null!, null!, items, null!, null!);
        var card = cards.PrepareAssault(123, DateTimeOffset.UnixEpoch);
        That(card.Gold == 123 && card.Item is { ItemId: AssaultMaterialDropPolicy.CertificateId, Count: 55 },
            "强袭免费牌固定55个黑夜穿行之证并保留原金币");

        foreach (int id in catalog.Missions.Values.SelectMany(rows => rows))
        {
            var definition = dungeons.Get(id);
            var maze = definition.Mazes[0];
            var monsters = maze.MapSpecification!.SelectMany(map =>
                DungeonMonsterPlan.FromRoom(rooms.Get(map, requirePassiveDefinitions: false), 1, 100,
                    definition.BasisLevel ?? 95).Monsters).ToArray();
            var boss = monsters.First(row => row.IsBoss);
            var small = monsters.First(row => row.CanReportDeath && !row.IsFriendly && !row.IsBoss &&
                !row.IsDummyMapBoss && !row.IsScriptedBoss && row.AiCharacter is null);
            var map = boss.SourceMap!;
            var entry = new DungeonEntryPlan(1, 0, checked((uint)id), 0, byte.MaxValue, 0, maze, map, map);
            var questEntry = entry with { QuestId = 6084 };
            That(!policy.Extend(empty, questEntry, boss).Trials.IsEmpty,
                $"强袭{id}携带6084猎杀任务时仍保留领主固定材料与英雄之证掉落");
            var extended = policy.Extend(empty, entry, boss);
            var generated = extended.Generate(_ => 0, DateTimeOffset.UnixEpoch);
            bool hidden = catalog.Missions[catalog.HiddenBaseDungeon].Contains(id);
            var expected = hidden
                ? new[] { (AssaultMaterialDropPolicy.CertificateId, 15, 6, 90), (AssaultMaterialDropPolicy.MaterialId, 50, 4, 200) }
                : new[] { (AssaultMaterialDropPolicy.CertificateId, 10, 2, 20), (AssaultMaterialDropPolicy.MaterialId, 25, 2, 50) };
            That(extended.MaximumObjects == (hidden ? 11 : 4),
                $"强袭{(hidden ? "隐藏" : "普通")}{id}领主掉落预留完整容量");
            if (hidden)
            {
                var heroProof = extended.Trials.Single(row => row.Category == DungeonDropCategory.Equipment);
                That(heroProof.Threshold == 3000 &&
                    heroProof.ProbabilityDenominator == DungeonDropRatePlan.ProbabilityDenominator &&
                    heroProof.Items!.Entries.Select(row => row.Template.Id).Order()
                        .SequenceEqual(AssaultMaterialDropPolicy.HeroProofItemIds.Order()) &&
                    generated.Count(row => row.Item is { } item && AssaultMaterialDropPolicy.HeroProofItemIds.Contains(item.ItemId)) == 1,
                    $"强袭隐藏{id}英雄之证独立掉落为30%");
            }
            else
            {
                That(extended.Trials.All(row => row.Category != DungeonDropCategory.Equipment) &&
                    generated.All(row => row.Item is null || !AssaultMaterialDropPolicy.HeroProofItemIds.Contains(row.Item.ItemId)),
                    $"强袭普通{id}不追加英雄之证装备掉落");
            }
            foreach (var (itemId, stack, stacks, total) in expected)
            {
                var rows = generated.Where(row => row.Item?.ItemId == itemId).Select(row => row.Item!.Count).ToArray();
                That(rows.Length == stacks && rows.All(count => count == stack) && rows.Sum() == total,
                    $"强袭{(hidden ? "隐藏" : "普通")}{id}领主固定{itemId}：{stack}×{stacks}={total}");
            }
            That(ReferenceEquals(policy.Extend(empty, entry, small), empty),
                $"强袭{id}小怪不再掉落污浊的不祥气息");
            foreach (var excluded in new[] { entry with { DungeonId = 7300 }, entry with { DungeonId = 7327 },
                entry with { DungeonId = 3 }, entry with { IsPractice = true }, entry with { RouteQuestId = 1 } })
                That(ReferenceEquals(policy.Extend(empty, excluded, boss), empty), "主选图/普通图/练习/剧情不借用强袭材料");
            foreach (var excluded in new[] { boss with { IsFriendly = true }, boss with { IsDummyMapBoss = true },
                boss with { Source = null }, boss with { SourceMap = null }, boss with { IsBlocking = false },
                boss with { Source = boss.Source! with { RankKind = "[normal]" } } })
                That(ReferenceEquals(policy.Extend(empty, entry, excluded), empty), "友方/假Boss/缺来源/小怪不产领主材料");
        }
        Console.WriteLine($"PASS {Count - checks} 强袭普通与隐藏领主固定材料、免费牌及排除边界");
    }
}