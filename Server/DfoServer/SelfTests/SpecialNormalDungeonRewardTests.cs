using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Inventory.Services;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SpecialNormalDungeonRewardTests
{
    public static void Run(TcpTestResources resources)
    {
        int before = Count;
        var pvf = resources.Archive;
        var items = resources.ItemResources;
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var monsters = SelfTestCatalogs.MonsterDrops(pvf);
        var growth = SelfTestCatalogs.Growth(pvf, resources.Learning);
        var kills = SelfTestCatalogs.KillExperience(pvf);
        var clears = SelfTestCatalogs.ClearExperience(pvf);
        var drops = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            monsters.Get, items.Items.Get, items.DungeonSpecialDrops);
        var cards = new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            items.Items, items.GoldLimits, items.EpicPieces, random: _ => 0, prices: SelfTestCatalogs.CardPrices(pvf));
        var fiendBoss = items.DungeonSpecialDrops.IndependentByMonster[110994].Single(row => row.ListFlag == 1);
        That(fiendBoss.Probabilities.SequenceEqual([40000,40000,40000,40000,40000]) &&
            fiendBoss.Attempts.SequenceEqual([1,2,3,4,1]) && fiendBoss.Items.Any(row => row.ItemId == 401000048 && row.Weight == 900),
            "复核超时空Boss原独立掉落概率、五档次数及原候选权重");
        var independent = drops.Prepare(110994,120,3,2,120).Trials.Single(row => row.ProbabilityDenominator == 1000000 && row.Attempts == 3);
        var independentPlan = new DungeonDropGenerationPlan("独立掉落边界回归", [independent]);
        That(independentPlan.Generate(_ => 0,DateTimeOffset.UtcNow).Length == 3 &&
            independentPlan.Generate(bound => bound - 1,DateTimeOffset.UtcNow).IsEmpty,
            "超时空独立掉落命中三次与全未命中均沿真实计划，不提高生产概率");
        var errors = new List<string>();
        foreach (int id in SpecialNormalDungeonEntryTests.DungeonIds)
        {
            try { Verify(id); }
            catch (Exception error) { errors.Add($"{id}: {error}"); }
        }
        foreach (var scenario in new[] { (Quest: 10019, Route: new[] { 9599 }), (Quest: 2683, Route: new[] { 9595, 9597, 9599, 9603 }),
            (Quest: 2902, Route: new[] { 5101, 5102, 5103, 5104, 5105, 5106 }),
            (Quest: 13200, Route: new[] { 9607, 9607, 9607, 9607, 9607 }),
            (Quest: 8683, Route: new[] { 400000016, 400000016, 400000016, 400000016, 400000016 }) })
        {
            try { VerifyQuest(scenario.Quest, scenario.Route); }
            catch (Exception error) { errors.Add($"quest {scenario.Quest}: {error}"); }
        }
        That(errors.Count == 0, "普通特殊副本完整奖励链：" + string.Join("\n", errors));
        Console.WriteLine($"PASS {Count - before} 普通安徒恩、卢克和超时空正式死亡、拾取、通关与翻牌断言");

        (Location Position, HashSet<int> Completed) Entrance(int id)
        {
            var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == id));
            var done = new HashSet<int>(area.Quests);
            if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
            var options = resources.Towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.Where(option => option.DungeonId == id).ToArray();
            if (!options.Any(option => option.QuestId == -1)) done.Add(options.First(option => !option.InProgress).QuestId);
            done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town, area.Area, (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2));
            return (position, done);
        }

        void Verify(int id)
        {
            var definition = dungeons.Get(id);
            var (position, done) = Entrance(id);
            using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
                growth, resources.Quests, false, 95, done.ToArray(), position: position,
                items: definition.OrdinaryEntryTicket is { } ticket ? [(ticket.ItemId, ticket.Count)] : []);
            Play(fixture, id);
        }

        void Play(QuestSceneTests.Fixture fixture, int id, uint questId = 0)
        {
            var definition = dungeons.Get(id);
            var fee = definition.OrdinaryEntryTicket;
            long CountItem(int itemId) => fixture.Read().Inventory.Items.Where(row => row.Core.ItemId == itemId).Sum(row => (long)row.Core.Count);
            long beforeFee = fee is { } cost ? CountItem(cost.ItemId) : 0;
            var random = new Random(id);
            bool rollbackChecked = false;
            var entry = fixture.Session.EnterFormalDungeon((uint)id, questId, 2, dungeons, rooms, monsters, items.Items,
                draw: bound => id == 400000016 && bound == 1000000 ? 0 : random.Next(bound),
                pieces: items.EpicPieces, prepareDrops: value => drops.PrepareEntry(value));
            That(entry is not null, $"{id}真实掉落准备后入场：{fixture.Session.LastFormalDungeonRejection}");
            if (fee is { } paid) That(CountItem(paid.ItemId) == beforeFee - paid.Count, "按资源完整数量扣入场材料");
            var route = QuestStoryRouteTests.Path(entry!.Entry.Maze.StaticTopology, entry.Entry.StartMap.Position, entry.Entry.BossMap.Position);
            FormalDungeonDeathResult? last = null;
            foreach (var point in route)
            {
                var loaded = fixture.Session.CompleteFormalDungeonLoading()!;
                That(loaded is not null, $"{id}/{point}正式加载");
                foreach (var monster in loaded!.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                    .OrderBy(row => row.IsBoss).ToArray())
                {
                    var observation = FormalDungeonSessionTests.Observation(loaded, monster);
                    if (point == entry.Entry.BossMap.Position && monster.IsBoss &&
                        monster.ObjectKey == loaded.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                            .OrderBy(row => row.IsBoss).Last().ObjectKey &&
                        fixture.Read().ActiveMainQuests.Any(row => row.ConditionState == 5 &&
                        QuestDungeonClearProgress.CountOncePerDay(resources.Quests.Get(row.QuestId))))
                    {
                        var beforeFailure = JsonSerializer.Serialize(fixture.Read());
                        fixture.Sql("CREATE TRIGGER daily608_fail AFTER INSERT ON character_quest_daily_clear BEGIN SELECT RAISE(ABORT,'daily clear rollback'); END");
                        Reject<SqliteException>(() => fixture.Session.ObserveFormalDungeonDeath(observation,kills,growth),
                            "每日计数写入失败回滚终末死亡、任务、掉落与成长");
                        That(JsonSerializer.Serialize(fixture.Read()) == beforeFailure &&
                            fixture.Query("SELECT count(*) FROM character_quest_daily_clear") is 0L,
                            "失败不占用当天资格或留下部分任务进度，原冻结死亡可重试");
                        fixture.Sql("DROP TRIGGER daily608_fail");
                    }
                    if (!rollbackChecked && id == 400000016)
                    {
                        var beforeFailure = JsonSerializer.Serialize(fixture.Read());
                        fixture.Sql("CREATE TRIGGER reward608_fail AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'zero reward rollback'); END");
                        Reject<SqliteException>(() => fixture.Session.ObserveFormalDungeonDeath(observation,kills,growth),
                            "零经验死亡写入失败回滚掉落与任务进度");
                        That(JsonSerializer.Serialize(fixture.Read()) == beforeFailure,"失败不能保留半份资产或任务计数");
                        fixture.Sql("DROP TRIGGER reward608_fail");
                        rollbackChecked = true;
                    }
                    var death = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    That(death is { Outcome: DungeonRunOutcome.Accepted, Receipt: not null }, $"{id}/{point}/{monster.MonsterId}首次死亡");
                    if (definition.DisableMonsterAndClearExperience)
                        That(death.GainedExperience == 0 && death.ProficiencyGained == 0, "禁经验副本仍提交死亡与掉落但不增加经验或熟练度");
                    if (id == 400000016 && monster.MonsterId == 110994)
                        That(death.Drops.Count(row => !row.IsGold && fiendBoss.Items.Any(item => item.ItemId == row.Core.ItemId)) >= 3,
                            "边界抽样命中的三件Boss独立掉落全部进入真实地面账本");
                    last = death;
                    var after = JsonSerializer.Serialize(fixture.Read());
                    fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    That(JsonSerializer.Serialize(fixture.Read()) == after, "重复死亡不增加资产和任务进度");
                    foreach (var ground in death.Drops)
                    {
                        var pickup = fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits);
                        That(pickup is { Outcome: DungeonRunOutcome.Accepted, Commit: not null }, "实际地面对象拾取入库");
                        after = JsonSerializer.Serialize(fixture.Read());
                        fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits);
                        That(JsonSerializer.Serialize(fixture.Read()) == after, "重复拾取不增发资产");
                    }
                }
                if (point == entry.Entry.BossMap.Position) break;
                var next = route[Array.IndexOf(route, point) + 1];
                That(fixture.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y) is not null, $"{id}实际清房后过图");
            }
            That(last?.ClearFact is not null && fixture.Session.Phase == SessionPhase.Settlement, $"{id}真实终点生成通关事实");
            var result = fixture.Session.SettleFormalDungeon(fixture.Read().Id, 99, 99, [6, 0, 8], dungeons, clears, growth, cards);
            That(result is { Outcome: DungeonRunOutcome.Accepted, Card: not null }, $"{id}通关冻结奖励");
            if (definition.IsOrdinaryAnton)
                That(result.Card!.Item is { ItemId: 10093974, Value: 3 }, "安徒恩专用翻牌为资源声明的3个魔能石");
            if (definition.IsNormalFiendwar)
                That(result.Card!.Item is { ItemId: 490007849, Value: 180 } && result.GainedExperience == 0 && result.ProficiencyGained == 0 &&
                    result.Options is { PaidDisabled: true, BlackDiamond: null }, "普通超时空180个材料、禁经验、无黄金牌和黑钻加牌");
            var settled = JsonSerializer.Serialize(fixture.Read());
            fixture.Session.SettleFormalDungeon(fixture.Read().Id, 99, 99, [6, 0, 8], dungeons, clears, growth, cards);
            That(JsonSerializer.Serialize(fixture.Read()) == settled, "重复结算不增加经验和资产");
            fixture.Session.AdvanceFormalDungeonCards(false);
            fixture.Session.AdvanceFormalDungeonCards(true);
            That(fixture.Session.SelectFormalDungeonFreeCard(0, 0) is not null, $"{id}免费牌实际领取");
            settled = JsonSerializer.Serialize(fixture.Read());
            fixture.Session.SelectFormalDungeonFreeCard(0, 0);
            That(JsonSerializer.Serialize(fixture.Read()) == settled, "重复翻牌不重复发放");
            fixture.Session.SetCharacterGold(fixture.Read().Name, 1000000);
            var goldBefore = fixture.Read().Inventory.Gold;
            if (definition.DisablePaidCard)
                That(fixture.Session.SelectFormalDungeonPaidCard(1, 0, cards) is null && fixture.Read().Inventory.Gold == goldBefore,
                    "禁止黄金牌的副本拒绝付费领取且不扣费");
            else
                That(fixture.Session.SelectFormalDungeonPaidCard(1, 0, cards) is not null &&
                    fixture.Read().Inventory.Gold == goldBefore - result.Options!.Price, $"{id}付费牌按冻结价格扣费");
            settled = JsonSerializer.Serialize(fixture.Read());
            fixture.Session.SelectFormalDungeonPaidCard(1, 0, cards);
            That(JsonSerializer.Serialize(fixture.Read()) == settled, "重复付费牌不扣费、不重抽");
            That(fixture.Session.ReturnFromFormalDungeon() is not null, $"{id}领奖后回城");
        }

        void VerifyQuest(int questId, int[] route)
        {
            var quest = resources.Quests.Get(questId);
            bool dailyClear = QuestDungeonClearProgress.CountOncePerDay(quest);
            var (position, done) = Entrance(route[0]);
            done.UnionWith(quest.Prerequisites);
            foreach (int id in route) done.UnionWith(Entrance(id).Completed);
            That(!done.Contains(questId), "夹具不预先完成被测任务");
            using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
                growth, resources.Quests, false, 95, done.ToArray(), position: position,
                items: (dailyClear ? route.Append(route[0]) : route).Select(dungeons.Get).Where(row => row.OrdinaryEntryTicket is not null)
                    .Select(row => row.OrdinaryEntryTicket!.Value).GroupBy(row => row.ItemId)
                    .Select(group => (group.Key, group.Sum(row => row.Count))).ToArray());
            var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
            var accept = fixture.Session.ChangeQuest((ushort)questId, QuestLifecycleAction.Accept, resources.Quests,
                items.Items, items.GoldLimits, items.EpicPieces);
            That(accept?.Commit is not null, $"{questId}原资源接取：{QuestEligibility.Evaluate(fixture.Read(), quest, resources.Quests).Reason}");
            if (questId == 10019)
                That(fixture.Session.ChangeQuest(2683,QuestLifecycleAction.Accept,resources.Quests,
                    items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,"神经细胞与高级塔尔坦材料任务可同时接取");
            var original = fixture.Read().ActiveMainQuests.Single(row => row.QuestId == questId);
            bool daily = QuestCycle.NormalDungeonMission(quest);
            That(original.ConditionState == (daily ? 1 : 5), "寻物或五次通关采用对应的初始条件");
            ushort selector = daily ? ushort.MaxValue : (ushort)0;
            QuestFinishResult Finish() => fixture.Session.FinishQuest((ushort)questId, selector, resources.Quests,
                parameters, items.Items, items.GoldLimits, items.EpicPieces, growth);
            That(Finish().Outcome == QuestFinishOutcome.Rejected, "条件未达成不能提前领奖");
            for (int index = 0; index < route.Length; index++)
            {
                if (dailyClear && index > 0)
                {
                    fixture.Session.ReturnToRoster();
                    fixture.Sql($"UPDATE character_quest_daily_clear SET day={QuestCycle.Day(DateTimeOffset.UtcNow.AddDays(-1))} WHERE quest_id={questId}; UPDATE character_fatigue SET used=0,remaining=maximum");
                    fixture.Session.Select(0, resumeTown: true); fixture.Session.MarkReady();
                }
                Play(fixture, route[index], (uint)questId);
                var active = fixture.Read().ActiveMainQuests.Single(row => row.QuestId == questId);
                That(active.AcceptanceId == original.AcceptanceId, "多次入场保留同一真实接取身份");
                if (!daily) That(active.ConditionState == 4 - index, "每次真实通关只减一次，重放不额外计数");
                if (dailyClear && index == 0)
                {
                    fixture.Session.ReturnToRoster(); fixture.Session.Select(0, resumeTown: true); fixture.Session.MarkReady();
                    Play(fixture,route[index], (uint)questId);
                    That(fixture.Read().ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState == 4,
                        "重登后同日第二个真实实例仍不累计；下一轮只把账本日期设为前日来复核跨日资格");
                }
                if (index < route.Length - 1) That(Finish().Outcome == QuestFinishOutcome.Rejected, "尚缺材料或次数仍拒绝交付");
            }
            That(fixture.Read().ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState == 0,
                $"{questId}真实击杀掉落或完整通关后条件归零");
            if (daily)
            {
                That(!QuestCycle.NormalDungeonMission(quest with { Id = 29999 }) &&
                    !QuestCycle.NormalDungeonMission(quest with { Grade = "[auto daily mission]" }),
                    "本次普通材料任务支持不误放未知任务或自动每日分配");
                for (int at = 0; at < quest.IntData.Count; at += 2)
                    That(fixture.Read().Inventory.Items.Where(row => row.Core.ItemId == quest.IntData[at]).Sum(row => row.Core.Count) == quest.IntData[at + 1],
                        "任务材料按原掉落行和持有上限获得完整数量");
            }
            if (daily)
                That(DungeonEntryPlan.TryCreate(dungeons,fixture.Read(),(uint)route[^1],(uint)questId,2,out var ready,out _,
                    quest:quest,quests:resources.Quests) && ready is { IsOrdinary:true },
                    "材料收齐但未交付仍可携带原任务身份进入普通路线");
            if (questId == 10019)
                That(fixture.Read().ActiveMainQuests.Any(row => row.QuestId == 2683 && row.ConditionState == 1) &&
                    fixture.Read().Inventory.Items.Any(row => row.Core.ItemId == 10093923),
                    "同一次真实能源通关也推进另一材料任务，但缺少其余材料不能提前达成");
            var rewardPairs = daily ? quest.Rewards : quest.Definition!.SelectRewards.Take(2).ToArray();
            var expectedRewards = Enumerable.Range(0, rewardPairs.Count / 2)
                .Select(at => (Item: rewardPairs[at * 2].Value, Count: rewardPairs[at * 2 + 1].Value)).ToArray();
            long Held(int itemId) => fixture.Read().Inventory.Items.Where(row => row.Core.ItemId == itemId).Sum(row => (long)row.Core.Count);
            var beforeRewards = expectedRewards.Select(row => Held(row.Item)).ToArray();
            var finish = Finish();
            That(finish.Outcome == QuestFinishOutcome.Committed, $"{questId}完成及奖励原子提交：{finish}");
            var completed = fixture.Read();
            if (daily)
                That(quest.IntData.Where((_,index) => index % 2 == 0).All(id => completed.Inventory.Items.All(row => row.Core.ItemId != id)),
                    "任务交付实际消耗完整材料，不只写完成标志");
            var rewardIds = expectedRewards.Select(row => row.Item).ToArray();
            for (int at = 0; at < expectedRewards.Length; at++)
                That(Held(expectedRewards[at].Item) == beforeRewards[at] + expectedRewards[at].Count,
                    "固定或选择奖励按资源完整数量增加，不以已有同名物品代替领奖验收");
            string saved = JsonSerializer.Serialize(completed);
            Finish();
            That(JsonSerializer.Serialize(fixture.Read()) == saved, "重复交付不二次消耗材料或发任务奖励");
            fixture.Session.ReturnToRoster();
            fixture.Session.Select(0, resumeTown: true);
            fixture.Session.MarkReady();
            That(fixture.Read().CompletedQuests.Contains(questId) && rewardIds.All(id => fixture.Read().Inventory.Items.Any(row => row.Core.ItemId == id)),
                "重登保留任务完成与奖励资产");
            That(fixture.Session.ChangeQuest((ushort)questId, QuestLifecycleAction.Accept, resources.Quests,
                items.Items, items.GoldLimits, items.EpicPieces)?.Commit is null, "同日或一次性任务完成后不能重接刷奖");
            if (daily)
            {
                That(fixture.Read().QuestDailyClaims.ContainsKey(questId), "每日交付记录真实领取周期");
                That(fixture.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1)) is not null &&
                    !fixture.Read().CompletedQuests.Contains(questId) &&
                    QuestEligibility.Evaluate(fixture.Read(), quest, resources.Quests) is { Eligible: true, Visible: true },
                    "下一游戏日按既有周期恢复资格，永久前置与奖励保留");
            }
        }
    }
}
