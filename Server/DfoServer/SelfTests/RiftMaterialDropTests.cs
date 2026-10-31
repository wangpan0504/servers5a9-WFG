using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RiftMaterialDropTests
{
    internal static void RunResources(TcpTestResources resources)
    {
        int before = Count;
        var towns = resources.Towns;
        var items = resources.ItemResources;
        var pvf = resources.Archive;
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var growth = SelfTestCatalogs.Growth(pvf, resources.Learning);
        var kills = SelfTestCatalogs.KillExperience(pvf);
        var drops = DungeonMonsterDropCatalog.Load(pvf);
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 316));
        var done = new HashSet<int>(area.Quests) { 2900 };
        foreach (var option in towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.Where(row => row.DungeonId == 316 && !row.InProgress && row.QuestId > 0))
            done.Add(option.QuestId);
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        using var fixture = new QuestSceneTests.Fixture(towns, resources.Characters, resources.Learning, growth,
            resources.Quests, false, 95, done.Order().ToArray(), position: position,
            items: [(DungeonRiftEpicDropPolicy.RefinedTerraniumId, 20)]);
        var rifts = new DungeonRiftEpicDropPolicy(items.DungeonDropItems,
            DungeonHellEpicCatalog.Load(pvf).Pool(54, 2, items.Items.Get), items.EpicPieceDrops.Pool(85, 90),
            items.Items.Get(DungeonRiftEpicDropPolicy.RefinedTerraniumId), new() { RefinedTerraniumPermille = 1000 });
        var policy = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems, drops.Get,
            items.Items.Get, items.DungeonSpecialDrops, riftEpics: rifts);
        int draws = 0, picked = 0, rewardedDeaths = 0;
        var faultedTables = new HashSet<string>();
        var receipt = fixture.Session.EnterFormalDungeon(316, 0, 3, dungeons, rooms, drops, items.Items,
            bound => { draws++; return bound - 1; }, items.EpicPieces, prepareDrops: entry => policy.PrepareEntry(entry));
        That(receipt is not null, "魔界材料沿真实准入进入：" + fixture.Session.LastFormalDungeonRejection);
        int initialStone = Stones();
        long initialBook = fixture.Read().Inventory.EpicPieces.Counts.Sum(value => (long)value);
        var target = receipt!.Entry.BossMap.Position;
        var path = QuestStoryRouteTests.Path(receipt.Entry.Maze.StaticTopology, receipt.Entry.StartMap.Position, target);
        foreach (var point in path)
        {
            receipt = fixture.Session.CompleteFormalDungeonLoading()!;
            foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly).ToArray())
            {
                var observation = FormalDungeonSessionTests.Observation(receipt, monster);
                FormalDungeonDeathResult death;
                if (rifts.Matches(receipt.Entry, monster))
                {
                    var saved = fixture.Read();
                    fixture.Sql("CREATE TRIGGER fail_rift_death AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'rift death retry'); END");
                    Reject<SqliteException>(() => fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth), "裂缝材料死亡写库故障回滚");
                    That(QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "故障不部分提交死亡、材料或经验");
                    fixture.Sql("DROP TRIGGER fail_rift_death");
                    int frozen = draws;
                    death = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    That(draws == frozen, "死亡故障重试沿冻结奖励，不重新随机");
                }
                else death = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                That(death.Outcome == DungeonRunOutcome.Accepted, "真实魔界怪物死亡被接受");
                receipt = death.Receipt!;
                var materials = death.Drops.Where(row => !row.IsGold && row.ResourceIdentity.Contains(DungeonRiftEpicDropPolicy.MaterialRule) &&
                    (items.EpicPieces.ByPiece.ContainsKey(row.Core.ItemId) || row.Core.ItemId == DungeonRiftEpicDropPolicy.RefinedTerraniumId)).ToArray();
                if (materials.Length > 0)
                {
                    rewardedDeaths++;
                    That(materials.Length == 6, "只有一个真实终点领主发出完整六个材料对象");
                    foreach (var ground in materials)
                    {
                        var key = ground.Reservation.GroundKey;
                        string auditTable = items.EpicPieces.ByPiece.ContainsKey(ground.Core.ItemId) ? "epic_piece_audit_log" : "inventory_audit_log";
                        if (faultedTables.Add(auditTable))
                        {
                            var saved = fixture.Read();
                            fixture.Sql($"CREATE TRIGGER fail_rift_pickup AFTER INSERT ON {auditTable} BEGIN SELECT RAISE(ABORT,'rift pickup retry'); END");
                            Reject<SqliteException>(() => fixture.Session.PickFormalDungeonItem(key, items.Items, items.EpicPieces, items.GoldLimits), "裂缝材料拾取故障回滚");
                            That(QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "拾取失败不部分写入背包或图鉴");
                            fixture.Sql("DROP TRIGGER fail_rift_pickup");
                        }
                        That(fixture.Session.PickFormalDungeonItem(key, items.Items, items.EpicPieces, items.GoldLimits).Outcome == DungeonRunOutcome.Accepted,
                            "魔界材料沿原拾取事务写入图鉴或背包");
                        picked++;
                        That(fixture.Session.PickFormalDungeonItem(key, items.Items, items.EpicPieces, items.GoldLimits).Outcome == DungeonRunOutcome.Rejected,
                            "相同地面对象不能重复领取");
                    }
                }
                That(fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Duplicate,
                    "重复死亡不重发材料");
            }
            if (point == target) break;
            var next = path[Array.IndexOf(path, point) + 1];
            receipt = fixture.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y)!;
            That(receipt is not null, "沿真实魔界路线移动");
        }
        That(rewardedDeaths == 1 && picked == 6 && faultedTables.Count == 2 && Stones() == initialStone + 1 &&
            fixture.Read().Inventory.EpicPieces.Counts.Sum(value => (long)value) == initialBook + 5,
            "整条魔界路线仅一份材料，五片写入史诗图鉴且一颗精炼石入背包");
        Console.WriteLine($"PASS {Count - before} 魔界裂缝材料真实死亡、拾取及事务回滚断言");

        int Stones() => fixture.Read().Inventory.Items.Where(row => row.Core.ItemId == DungeonRiftEpicDropPolicy.RefinedTerraniumId)
            .Sum(row => row.Core.Count);
    }
}
