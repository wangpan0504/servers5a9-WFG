using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SleepyHollowMaterialDropTests
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
        var policy = new SleepyHollowMaterialDropPolicy(SleepyHollowRewardCatalog.Load(dungeons, rooms), items.Items.Get);
        var resourcePolicy = new DungeonResourceDropPolicy(Testing.SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            drops.Get, items.Items.Get, items.DungeonSpecialDrops, sleepyHollow: policy);
        That(items.Items.Get(SleepyHollowMaterialDropPolicy.UsefulJunkId).Kind == ItemKind.Quest &&
            items.Items.Get(SleepyHollowMaterialDropPolicy.BirthSeedId).Kind == ItemKind.Material,
            "真实资源杂物为Quest、芽为Material，不误用成长胶囊或污浊气息");
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 7322));
        var done = new HashSet<int>(area.Quests) { 6030, 6082 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        var seen = new HashSet<int>();
        for (int mazeChoice = 0; mazeChoice < 5; mazeChoice++) Verify(mazeChoice);
        That(seen.SetEquals([311401, 311408, 311415, 311422, 311429]), "正式入场覆盖断头谷全部五条Boss路线");
        Console.WriteLine($"PASS {Count - before} 断头谷五Boss、死亡回滚、拾取及重放资源断言");

        void Verify(int choice)
        {
            using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
                done.ToArray(), position: position);
            int draws = 0;
            FormalDungeonReceipt? Enter() => fixture.Session.EnterFormalDungeon(7322, 0, 0, dungeons, rooms, drops, items.Items,
                bound => { draws++; return bound == 5 ? choice : bound - 1; }, items.EpicPieces,
                prepareDrops: entry => resourcePolicy.PrepareEntry(entry));
            var receipt = Enter();
            That(receipt is not null, "真实断头谷准入成功：" + fixture.Session.LastFormalDungeonRejection);
            seen.Add(receipt!.Entry.BossMap.Id);
            That(receipt.Entry.StartFatigue == 8 && receipt.Entry.StartMap.Id == 311393, "入场疲劳与原起点不变");
            var path = QuestStoryRouteTests.Path(receipt.Entry.Maze.StaticTopology, receipt.Entry.StartMap.Position, receipt.Entry.BossMap.Position);
            int rewarded = 0, junk = 0, seeds = 0;
            uint oldKey = 0;
            bool pickupFault = false;
            foreach (var point in path)
            {
                if (point == receipt!.Entry.BossMap.Position)
                {
                    var pendingBoss = receipt.Monsters.Monsters.Single(row => row.IsBoss);
                    int pendingDraws = draws;
                    var saved = fixture.Read();
                    var premature = fixture.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, pendingBoss), kills, growth);
                    That(premature.Outcome == DungeonRunOutcome.Rejected && premature.Drops.IsEmpty && draws == pendingDraws &&
                        QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "Boss房间尚未完成加载的报告不能提前生成材料或抽数量");
                }
                receipt = fixture.Session.CompleteFormalDungeonLoading()!;
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
                {
                    var observation = FormalDungeonSessionTests.Observation(receipt, monster);
                    bool eligible = policy.IsRewardBoss(receipt.Entry, monster);
                    if (eligible)
                    {
                        That(monster.MonsterId == 107010048 + choice, "五条真实Boss模板与当前路线一一匹配");
                        var cached = resourcePolicy.PrepareEntry(receipt.Entry);
                        var foreign = monster with { Source = monster.Source! with { Path = "monster/同模板外部来源.mob" } };
                        That(!cached(foreign).ResourceSource.Contains(SleepyHollowMaterialDropPolicy.Rule) &&
                            !cached(monster with { SourceMap = monster.SourceMap! with { Id = 1 } }).ResourceSource.Contains(SleepyHollowMaterialDropPolicy.Rule) &&
                            cached(monster).ResourceSource.Contains(SleepyHollowMaterialDropPolicy.Rule) &&
                            !cached(monster with { ObjectKey = receipt.Entry.CharacterId }).ResourceSource.Contains(SleepyHollowMaterialDropPolicy.Rule) &&
                            !cached(monster with { IsFriendly = true }).ResourceSource.Contains(SleepyHollowMaterialDropPolicy.Rule),
                            "同模板Boss不同来源或key资格不会串缓存");
                        var saved = fixture.Read();
                        int rejectedDraws = draws;
                        var invalid = fixture.Session.ObserveFormalDungeonDeath(observation with { ChecksumMatches = false }, kills, growth);
                        That(invalid.Outcome == DungeonRunOutcome.Rejected && invalid.Drops.IsEmpty && draws == rejectedDraws &&
                            QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "未经合法死亡核验的报告不抽材料或发奖励");
                        fixture.Sql("CREATE TRIGGER fail_sleepy_death AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'sleepy death retry'); END");
                        Reject<SqliteException>(() => fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth), "死亡保存故障必须整体回滚");
                        That(QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "失败不提交经验或材料资产");
                        fixture.Sql("DROP TRIGGER fail_sleepy_death");
                    }
                    int frozenDraws = draws;
                    var death = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    That(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null, "合法怪物沿原死亡事务提交");
                    if (eligible) That(draws == frozenDraws, "死亡失败重试沿冻结数量，不重新抽10至14芽");
                    receipt = death.Receipt!;
                    var materials = death.Drops.Where(row => !row.IsGold &&
                        row.Core.ItemId is SleepyHollowMaterialDropPolicy.UsefulJunkId or SleepyHollowMaterialDropPolicy.BirthSeedId).ToArray();
                    That(eligible ? materials.Length > 0 : materials.Length == 0, "只有当前真实Boss死亡产生补全材料");
                    if (eligible)
                    {
                        rewarded++;
                        That(materials.Where(row => row.Core.ItemId == SleepyHollowMaterialDropPolicy.UsefulJunkId).Sum(row => row.Core.Count) == 9 &&
                            materials.Where(row => row.Core.ItemId == SleepyHollowMaterialDropPolicy.BirthSeedId).Sum(row => row.Core.Count) == 10 + choice &&
                            materials.All(row => row.ResourceIdentity.Contains(SleepyHollowMaterialDropPolicy.Rule)), "完整固定杂物和冻结芽数量保留本服来源");
                    }
                    foreach (var ground in materials)
                    {
                        oldKey = ground.Reservation.GroundKey;
                        if (!pickupFault)
                        {
                            var saved = fixture.Read();
                            fixture.Sql("CREATE TRIGGER fail_sleepy_pick AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'sleepy pickup retry'); END");
                            Reject<SqliteException>(() => fixture.Session.PickFormalDungeonItem(oldKey, items.Items, items.EpicPieces, items.GoldLimits),
                                "材料拾取故障保留地面资格并整体回滚");
                            That(QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "拾取失败不改变原资产");
                            fixture.Sql("DROP TRIGGER fail_sleepy_pick");
                            pickupFault = true;
                        }
                        var pickup = fixture.Session.PickFormalDungeonItem(oldKey, items.Items, items.EpicPieces, items.GoldLimits);
                        That(pickup.Outcome == DungeonRunOutcome.Accepted && pickup.Commit is { Duplicate: false }, "两种原类别材料经正式拾取持久化");
                        if (ground.Core.ItemId == SleepyHollowMaterialDropPolicy.UsefulJunkId) junk += ground.Core.Count; else seeds += ground.Core.Count;
                        That(fixture.Session.PickFormalDungeonItem(oldKey, items.Items, items.EpicPieces, items.GoldLimits).Outcome == DungeonRunOutcome.Rejected,
                            "重复拾取不发第二份材料");
                    }
                    var duplicate = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    That(duplicate.Outcome == DungeonRunOutcome.Duplicate && duplicate.Drops.IsEmpty, "重复死亡不重复发布地面材料");
                }
                if (point == receipt.Entry.BossMap.Position) break;
                var next = path[Array.IndexOf(path, point) + 1];
                receipt = fixture.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y)!;
                That(receipt is not null, "沿真实路线过房");
            }
            int Amount(int id) => fixture.Read().Inventory.Items.Where(row => row.Core.ItemId == id).Sum(row => row.Core.Count);
            That(rewarded == 1 && junk == 9 && seeds == 10 + choice && Amount(SleepyHollowMaterialDropPolicy.UsefulJunkId) == junk &&
                Amount(SleepyHollowMaterialDropPolicy.BirthSeedId) == seeds, "每条路线仅Boss一份完整材料实际入库");
            That(fixture.Session.ReturnFromFormalDungeon() is not null && Enter() is not null &&
                fixture.Session.PickFormalDungeonItem(oldKey, items.Items, items.EpicPieces, items.GoldLimits).Outcome == DungeonRunOutcome.Rejected &&
                Amount(SleepyHollowMaterialDropPolicy.UsefulJunkId) == junk && Amount(SleepyHollowMaterialDropPolicy.BirthSeedId) == seeds,
                "回城和新挑战不重发，旧实例地面key失效");
        }
    }
}
