using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class WeekDungeonRewardTests
{
    public static void Run(TcpTestResources resources)
    {
        int before = Count;
        var pvf = resources.Archive;
        var items = resources.ItemResources;
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var growth = SelfTestCatalogs.Growth(pvf, resources.Learning);
        var kills = SelfTestCatalogs.KillExperience(pvf);
        var drops = DungeonMonsterDropCatalog.Load(pvf);
        var policy = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            drops.Get, items.Items.Get, items.DungeonSpecialDrops);
        var area = resources.Towns.Areas[((byte)107, (byte)0)];
        var done = new HashSet<int>(area.Quests) { 8709, 8710 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        var seen = new HashSet<int>();
        for (int maze = 0; maze < 9; maze++) Verify(maze);
        That(seen.SetEquals([400000486, 400000492, 400000498]), "三条隐藏路线均实际到达终点宝箱");
        Console.WriteLine($"PASS {Count - before} 异次元遗迹九路线、隐藏宝箱、掉落、回滚与幂等断言");

        void Verify(int mazeChoice)
        {
            using var f = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning, growth,
                resources.Quests, false, 95, done.ToArray(), position: position, items: [(10161065, 1)]);
            bool chestDraw = false;
            int draws = 0;
            var receipt = f.Session.EnterFormalDungeon(WeekDungeonRewardPolicy.DungeonId, 0, 1, dungeons, rooms,
                drops, items.Items, bound => { draws++; return bound == 9 ? mazeChoice : chestDraw ? 0 : bound - 1; },
                items.EpicPieces, prepareDrops: entry => policy.PrepareEntry(entry));
            That(receipt is not null, "遗迹真实入场成功：" + f.Session.LastFormalDungeonRejection);
            if (mazeChoice >= 6) seen.Add(receipt!.Entry.BossMap.Id);
            That(receipt!.Entry.Difficulty == 1, "沿真实DGN强制难度1，不按显示选项猜奖励数量");
            var path = QuestStoryRouteTests.Path(receipt.Entry.Maze.StaticTopology, receipt.Entry.StartMap.Position,
                receipt.Entry.BossMap.Position);
            var picked = new Dictionary<int, int>();
            foreach (var point in path)
            {
                receipt = f.Session.CompleteFormalDungeonLoading()!;
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.CanReportDeath).OrderBy(row => row.IsBoss).ToArray())
                {
                    if (monster.MonsterId == WeekDungeonRewardPolicy.CarrierId) continue;
                    var observation = FormalDungeonSessionTests.Observation(receipt, monster);
                    bool chest = monster.MonsterId == WeekDungeonRewardPolicy.ChestId;
                    var carrier = chest ? receipt.Monsters.Monsters.Single(row => row.MonsterId == WeekDungeonRewardPolicy.CarrierId) : null;
                    chestDraw = chest;
                    if (chest)
                    {
                        var prepare = policy.PrepareEntry(receipt.Entry);
                        var plan = prepare(monster);
                        That(plan.ResourceSource.Contains(WeekDungeonRewardPolicy.Rule) && plan.Trials.Length == 2 &&
                            plan.Trials.All(row => row.IndependentDrop) && plan.Trials[0].Attempts == 40 &&
                            plan.Trials[0].Threshold == 1000000 && plan.Trials[1].Attempts == 1,
                            "最终PVF隐藏载体40碎片与一次礼盒原表迁入真正宝箱，无通用金币装备混入");
                        That(prepare(carrier!).Trials.IsEmpty, "载体先报或晚报均无第二份奖励计划");
                        Reject<NotSupportedException>(() => WeekDungeonRewardPolicy.Prepare(receipt.Entry, monster,
                            (_, _) => new("missing", [])), "缺失原奖励资源明确拒绝，不静默吞奖");
                        var lottery = plan.Trials[1].Items!;
                        That(lottery.TotalWeight == 100000 && new[] { 0, 14999, 15000, 44999, 45000, 74999, 75000, 99999 }
                            .Select(roll => lottery.Select(roll).Id).SequenceEqual(new[]
                            { 490013631, 490013631, 490013632, 490013632, 490013633, 490013633, 490013634, 490013634 }) &&
                            plan.Trials.All(row => !row.GrowthContractEligible),
                            "四档礼盒保留15000/30000/30000/25000权重边界，普通载体不借Boss成长契约多发");
                        var foreign = monster with { SourceMap = monster.SourceMap! with { Id = 1 } };
                        That(!prepare(foreign).ResourceSource.Contains(WeekDungeonRewardPolicy.Rule) &&
                            prepare(monster).ResourceSource.Contains(WeekDungeonRewardPolicy.Rule), "同模板外部来源不串专项缓存");
                        That(!WeekDungeonRewardPolicy.Matches(receipt.Entry with { IsPractice = true }, monster) &&
                            !WeekDungeonRewardPolicy.Matches(receipt.Entry with { DungeonId = 1 }, monster) &&
                            !WeekDungeonRewardPolicy.Matches(receipt.Entry, monster with { IsFriendly = true }),
                            "练习、其它副本及友方不借用专属宝箱奖励");
                        if (mazeChoice == 8)
                        {
                            var early = f.Session.ObserveFormalDungeonScriptedDeath(
                                new(carrier!.ObjectKey, 1, true, new string('B', 64)), kills, growth);
                            That(early is { Outcome: DungeonRunOutcome.Accepted, Drops.IsEmpty: true, Receipt: not null },
                                "载体退场先到也无掉落，奖励仍等待真宝箱");
                            receipt = early.Receipt!;
                        }
                        var saved = f.Read();
                        int invalidDraws = draws;
                        That(f.Session.ObserveFormalDungeonDeath(observation with { ChecksumMatches = false }, kills, growth).Outcome == DungeonRunOutcome.Rejected &&
                            draws == invalidDraws, "坏校验不抽取宝箱奖励");
                        f.Sql("CREATE TRIGGER fail_week_chest AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'week chest rollback'); END");
                        Reject<SqliteException>(() => f.Session.ObserveFormalDungeonDeath(observation, kills, growth), "宝箱死亡晚期SQL故障回滚");
                        That(QuestCompletionRepositoryTests.Same(saved, f.Read()), "宝箱故障不提前提交资产或结算");
                        f.Sql("DROP TRIGGER fail_week_chest");
                    }
                    int frozenDraws = draws;
                    var death = f.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    That(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null, "原合法死亡正常提交");
                    receipt = death.Receipt!;
                    if (!chest)
                    {
                        if (monster.MonsterId is 407000476 or 407000483 or 407000493)
                        {
                            That(!policy.PrepareEntry(receipt.Entry)(monster).ResourceSource.Contains(WeekDungeonRewardPolicy.Rule) &&
                                death.Drops.Where(row => row.Core.ItemId == 490013621).Sum(row => row.Core.Count) == 40,
                                "普通及困难原Boss保留原独立40碎片，不借隐藏礼盒计划");
                            Pick(death.Drops.Where(row => row.Core.ItemId == 490013621));
                        }
                        continue;
                    }
                    That(draws == frozenDraws && death.Drops.Length == 41 && death.Drops.All(row => !row.IsGold),
                        "故障重试恢复冻结41个奖励对象，不重新抽奖或生成通用金币");
                    That(death.Drops.Where(row => row.Core.ItemId == 490013621).Sum(row => row.Core.Count) == 40 &&
                        death.Drops.Single(row => row.Core.ItemId != 490013621).Core.ItemId == 490013631,
                        "难度1按当前表40个世界碎片及权重首档礼盒");
                    Pick(death.Drops);
                    That(f.Session.ObserveFormalDungeonDeath(observation, kills, growth) is { Outcome: DungeonRunOutcome.Duplicate, Drops.IsEmpty: true },
                        "重复宝箱死亡不重复发奖");
                    var carrierReport = new NativeDungeonScriptedDeathObservation(
                        carrier!.ObjectKey, 1, true, new string('A', 64));
                    That(f.Session.ObserveFormalDungeonScriptedDeath(carrierReport, kills, growth) is { Outcome: DungeonRunOutcome.Duplicate, Drops.IsEmpty: true },
                        "原生零参与者载体退场仍按清场重放，不绕开普通死亡保护");
                }
                if (point == receipt.Entry.BossMap.Position) break;
                var next = path[Array.IndexOf(path, point) + 1];
                That(f.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y) is not null, "遗迹沿原门位进入下一房间");
            }
            int boxes = mazeChoice >= 6 ? 1 : 0;
            That(picked.GetValueOrDefault(490013621) == 40 && picked.GetValueOrDefault(490013631) == boxes,
                "九路线各自原碎片与隐藏礼盒拾取完成");
            f.Session.ReturnFromFormalDungeon();
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Read().Inventory.Items.Where(row => row.Core.ItemId == 490013621).Sum(row => row.Core.Count) == 40 &&
                f.Read().Inventory.Items.Where(row => row.Core.ItemId == 490013631).Sum(row => row.Core.Count) == boxes,
                "回城重选后奖励仍在且无重复");

            void Pick(IEnumerable<DungeonGroundObject> groundDrops)
            {
                foreach (var ground in groundDrops)
                {
                    var pickup = f.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits);
                    That(pickup.Outcome == DungeonRunOutcome.Accepted, "遗迹地面奖励实际入库存储");
                    picked[ground.Core.ItemId] = picked.GetValueOrDefault(ground.Core.ItemId) + ground.Core.Count;
                }
            }
        }
    }
}
