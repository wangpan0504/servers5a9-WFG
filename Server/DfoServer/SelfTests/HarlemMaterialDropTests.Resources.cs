using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class HarlemMaterialDropTests
{
    public static void RunResources(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int before = Count;
        var dungeons = Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        var growth = Testing.SelfTestCatalogs.Growth(pvf, skills);
        var kills = Testing.SelfTestCatalogs.KillExperience(pvf);
        var drops = DungeonMonsterDropCatalog.Load(pvf);
        var harlem = towns.DungeonMaps.Values.Where(map => map.HellDungeon && map.HellQuests.Contains(6080))
            .SelectMany(map => map.Options).Select(option => checked((uint)option.DungeonId)).ToArray();
        var rules = Testing.SelfTestCatalogs.DropRules(pvf);
        var hell = new DungeonHellRewardPolicy(DungeonHellDropRules.TryLoad(pvf)!, items.DungeonDropItems);
        Verify(291100308, false, 50);
        Verify(291100308, true, 50);
        Verify(530, false, 0);
        Verify(530, false, 1000);
        Console.WriteLine($"PASS {Count - before} 哈林普通/星空及黎明材料正式死亡、拾取和回滚资源断言");

        void Verify(uint dungeonId, bool legend, int purgedPermille)
        {
            var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeonId));
            var done = new HashSet<int>(area.Quests) { 6030, 6080, 6081, 6082 };
            if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
                checked((ushort)(bounds.Y + bounds.Height / 2)));
            using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
                done.Order().ToArray(), position: position,
                items: [(3330, 60), (HellPartyEntryCost.LegendStone, 4), (10161610, 40)]);
            var materials = new HarlemMaterialDropPolicy(new() { DawnPurgedPermille = purgedPermille }, harlem, items.Items.Get);
            var resourcePolicy = new DungeonResourceDropPolicy(rules, items.DungeonDropItems, drops.Get,
                items.Items.Get, items.DungeonSpecialDrops, materials: materials);
            int drawCalls = 0;
            var receipt = Enter();
            That(receipt is not null, "材料回归沿真实准入进入副本: " + dungeonId + ":" + fixture.Session.LastFormalDungeonRejection);
            var target = receipt!.Entry.HellParty?.Room ?? receipt.Entry.BossMap.Position;
            var path = QuestStoryRouteTests.Path(receipt.Entry.Maze.StaticTopology, receipt.Entry.StartMap.Position, target);
            int materialDeaths = 0, collected = 0;
            bool faulted = false;
            uint oldGroundKey = 0;
            int expectedId = dungeonId == 530 && purgedPermille == 1000 ? HarlemMaterialDropPolicy.PurgedId : HarlemMaterialDropPolicy.FragmentId;
            int expectedCount = dungeonId == 530 ? purgedPermille == 1000 ? 120 : 200 : legend ? 15 : 9;
            foreach (var point in path)
            {
                receipt = fixture.Session.CompleteFormalDungeonLoading()!;
                That(receipt is not null, "真实材料路线加载房间");
                // 逆序击杀证明奖励归实际末杀，而不是最后一个静态key。
                foreach (var monster in receipt!.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                    .OrderByDescending(row => row.ObjectKey).ToArray())
                {
                    var observation = FormalDungeonSessionTests.Observation(receipt, monster);
                    if (dungeonId == 530 && monster.IsBoss)
                    {
                        var cached = resourcePolicy.PrepareEntry(receipt.Entry, hell);
                        That(!cached(monster with { IsFriendly = true }).ResourceSource.Contains(HarlemMaterialDropPolicy.Rule) &&
                            cached(monster).ResourceSource.Contains(HarlemMaterialDropPolicy.Rule) &&
                            !cached(monster with { Source = monster.Source! with { Modifiers = ["[dummy]"] } }).ResourceSource.Contains(HarlemMaterialDropPolicy.Rule),
                            "同模板同等级的友方、真Boss和脚本dummy不串用材料缓存");
                    }
                    if (dungeonId == 530 && purgedPermille == 1000 && monster.IsBoss && !faulted)
                    {
                        var saved = fixture.Read();
                        fixture.Sql("CREATE TRIGGER fail_harlem_material AFTER INSERT ON character_dungeon_drop_objects BEGIN SELECT RAISE(ABORT,'material death retry'); END");
                        Reject<SqliteException>(() => fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth),
                            "黎明Boss地面写入故障不能部分提交死亡、经验或材料");
                        That(QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "材料死亡事务失败保留完整原角色快照");
                        fixture.Sql("DROP TRIGGER fail_harlem_material");
                        int frozenDraws = drawCalls;
                        var resumed = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                        That(drawCalls == frozenDraws && resumed.Outcome == DungeonRunOutcome.Accepted,
                            "Boss死亡原事件重试复用冻结分支，不重新抽材料");
                        receipt = resumed.Receipt!;
                        Collect(resumed);
                        faulted = true;
                    }
                    else
                    {
                        var death = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                        That(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null,
                            "真实演员死亡沿既有提交入口: " + monster.MonsterId);
                        receipt = death.Receipt!;
                        Collect(death);
                    }
                    var duplicate = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    That(duplicate.Outcome == DungeonRunOutcome.Duplicate && duplicate.Drops.IsEmpty,
                        "重复死亡不重复发布材料地面对象");
                }
                if (point == target) break;
                var next = path[Array.IndexOf(path, point) + 1];
                receipt = fixture.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y)!;
                That(receipt is not null, "沿真实地图移动到材料奖励源");
            }
            That(materialDeaths == 1 && collected == expectedCount && Amount() == expectedCount,
                "每次真实挑战只由一个合法死亡源发放完整材料，拾取后写入库存");
            That(fixture.Session.ReturnFromFormalDungeon() is not null && Amount() == expectedCount,
                "回城保留已拾材料，不在退场追加发奖");
            var reentered = Enter();
            That(reentered is not null && Amount() == expectedCount &&
                fixture.Session.PickFormalDungeonItem(oldGroundKey, items.Items, items.EpicPieces, items.GoldLimits).Outcome == DungeonRunOutcome.Rejected,
                "新挑战不提前发材料，也不能消费旧实例的地面key");

            int Amount() => fixture.Read().Inventory.Items.Where(item => item.Core.ItemId == expectedId).Sum(item => item.Core.Count);
            FormalDungeonReceipt? Enter()
            {
                if (legend)
                {
                    That(fixture.Session.BeginDungeonSelection() is not null, "星空材料回归建立选图上下文");
                    That(fixture.Session.PrepareLegendDungeonSelection(dungeons, _ => 2) == dungeonId, "星空材料沿冻结目标准入");
                }
                return fixture.Session.EnterFormalDungeon(dungeonId, 0, dungeonId == 530 ? (byte)3 : (byte)0,
                    dungeons, rooms, drops, items.Items, bound => { drawCalls++; return bound - 1; }, items.EpicPieces,
                    prepareDrops: entry => resourcePolicy.PrepareEntry(entry, hell), hellParty: dungeonId != 530, hellLegend: legend);
            }
            void Collect(FormalDungeonDeathResult death)
            {
                var material = death.Drops.Where(row => !row.IsGold && row.Core.ItemId is HarlemMaterialDropPolicy.FragmentId or HarlemMaterialDropPolicy.PurgedId).ToArray();
                if (material.Length == 0) return;
                materialDeaths++;
                That(material.All(row => row.Core.ItemId == expectedId && row.ResourceIdentity.Contains(HarlemMaterialDropPolicy.Rule)) &&
                    material.Sum(row => row.Core.Count) == expectedCount,
                    "材料地面记录保留明确本服规则来源、物品及完整数量，黎明分支互斥");
                foreach (var ground in material)
                {
                    oldGroundKey = ground.Reservation.GroundKey;
                    if (dungeonId == 530 && purgedPermille == 1000 && !faulted)
                    {
                        var saved = fixture.Read();
                        fixture.Sql("CREATE TRIGGER fail_harlem_pickup AFTER INSERT ON character_dungeon_drop_pickups BEGIN SELECT RAISE(ABORT,'material pickup retry'); END");
                        FormalDungeonPickupResult? attempt = null;
                        bool failed = false;
                        try { attempt = fixture.Session.PickFormalDungeonItem(oldGroundKey, items.Items, items.EpicPieces, items.GoldLimits); }
                        catch (SqliteException) { failed = true; }
                        That(failed, $"材料拾取必须走到事务故障点，outcome={attempt?.Outcome},capacity={attempt?.CapacityRejected},kind={ground.Core.Kind},count={ground.Core.Count},phase={fixture.Session.Phase}");
                        That(QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "拾取故障保留库存原数量");
                        fixture.Sql("DROP TRIGGER fail_harlem_pickup");
                    }
                    var pickup = fixture.Session.PickFormalDungeonItem(oldGroundKey, items.Items, items.EpicPieces, items.GoldLimits);
                    That(pickup.Outcome == DungeonRunOutcome.Accepted && pickup.Commit is { Duplicate: false }, "原材料地面key实际拾取成功");
                    collected += ground.Core.Count;
                    That(fixture.Session.PickFormalDungeonItem(oldGroundKey, items.Items, items.EpicPieces, items.GoldLimits).Outcome == DungeonRunOutcome.Rejected,
                        "重复材料拾取不能再加数量");
                }
            }
        }
    }
}
