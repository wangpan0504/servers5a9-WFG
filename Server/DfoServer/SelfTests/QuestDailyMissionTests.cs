using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestDailyMissionTests
{
    internal static readonly int[] Ids = [2901,2942,4099,4100,4111,4112,6117,8557,8714,8926];

    public static void RunCore()
    {
        int before = Count;
        var quest = QuestEligibilityTests.Quest(4111) with
        { Grade = "[daily mission]", Condition = "[seeking]", IntData = [3329,10] };
        That(QuestCycle.NormalDungeonMission(quest) && QuestCycle.Daily(quest),"曲玉普通材料任务使用每日账本");
        foreach (var invalid in new[]
        {
            quest with { Id = 29999 }, quest with { Grade = "[auto daily mission]" }, quest with { Grade = "[training]" },
            quest with { MainQuest = 100 }, quest with { SubType = 0 }, quest with { IntData = [3329,0] },
            quest with { IntData = [3329,10,9999] }, quest with { RewardType = "[grow type]" },
            quest with { Definition = quest.Definition! with { ExposedByNpc = false } },
            quest with { Definition = quest.Definition! with { AccountQuest = true } },
            quest with { Definition = quest.Definition! with { Diagnostics = ["invalid"] } }
        }) That(!QuestCycle.NormalDungeonMission(invalid),"不将已核对普通每日推广到未知定义或专用入口");
        RunMoonMountainCore();
        Console.WriteLine($"PASS {Count-before} 普通每日入口边界断言");
    }

    static void RunMoonMountainCore()
    {
        var moon = QuestEligibilityTests.Quest(8557) with
        {
            Grade = "[daily mission]",
            Condition = "[seeking]",
            SubType = -1,
            IntData = [10094602, 10]
        };
        That(QuestCycle.IsMoonMountainDailyMission(moon) && QuestCycle.NormalDungeonMission(moon),
            "8557进攻准备识别为月轮山普通每日寻物任务");
        var catalog = new QuestCatalog(new Dictionary<int, StoryQuest> { [8557] = moon });
        var active = new ActiveMainQuest(0, 8557, 1) { AcceptanceId = Guid.NewGuid() };
        var character = new Character(1, 0, "Moon", 1, new(14, 3, 450, 240), null) { Level = 95, ActiveMainQuests = [active] };
        var items = new ItemCatalog(null!, null!, null!, 0);
        items.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [10094602] = new(10094602, ItemKind.Material, 0, 1, 0, 1000, 0, 0, 0, "", 0, 0, "未精炼的念气结晶")
        });
        var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
        var identity = new DungeonRunIdentity(1, 0, 1, Guid.NewGuid(), 1);
        var now = DateTimeOffset.UtcNow;
        foreach (uint dungeonId in new uint[] { 3200, 3201, 3202 })
        {
            var entry = new DungeonEntryPlan(1, 1, dungeonId, 0, byte.MaxValue, 1, null!, null!, null!)
                { QuestAcceptances = [active], RecommendedQuestItems = false };
            That(QuestTargetRules.Drops(moon, entry).Single() == new QuestDropDefinition(0, 0, (int)dungeonId, 1, 10094602, 10, 100, 10),
                $"{dungeonId}月轮山巨龙固定难度1声明终末通关发放10094602×10");
            foreach (var rejected in new[]
            {
                entry with { Difficulty = 0 },
                entry with { Difficulty = 2 },
                entry with { DungeonId = 3199 },
                entry with { DungeonId = 3203 },
                entry with { IsPractice = true },
                entry with { IsTutorial = true },
                entry with { IsQuestReplay = true }
            })
                That(!QuestTargetRules.Drops(moon, rejected).Any(), "非月轮山三图、错误难度或练习/回放/教学模式不发放未精炼的念气结晶");
            var loaded = QuestDropResources.Create(character, entry, catalog, items, pieces);
            var midKill = QuestDropPlan.Create(character, entry, loaded, Guid.NewGuid(), identity, 82105, 69034, 1, false,
                _ => throw new InvalidOperationException("月轮山每日终末掉落不抽随机数"), now);
            That(!midKill.Inventory.Changed && midKill.Outcomes.IsEmpty, "未通关的普通或精英击杀不提前发放10094602");
            var clearDrop = QuestDropPlan.Create(character, entry, loaded, Guid.NewGuid(), identity, 82105, 69033, 1, true,
                _ => throw new InvalidOperationException("月轮山每日终末掉落不抽随机数"), now);
            That(clearDrop.Outcomes is [{ QuestId: 8557, Granted: 10, Error: 0 }] &&
                clearDrop.Inventory.After.Items.Single() is { Core: { ItemId: 10094602, Count: 10 } } &&
                QuestInventoryProgress.Refresh([active], clearDrop.Inventory.After, catalog, now.ToUnixTimeSeconds()).Single().ConditionState == 0,
                $"{dungeonId}真实终末通关一次补齐10个未精炼的念气结晶并达成任务条件");
            var capped = QuestDropPlan.Create(character with { Inventory = clearDrop.Inventory.After }, entry, loaded,
                Guid.NewGuid(), identity, 82105, 69033, 1, true, _ => 0, now);
            That(!capped.Inventory.Changed && capped.Outcomes.IsEmpty, "已持有10个未精炼的念气结晶时不再超量发放");
        }
    }

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items)
    {
        int before = Count;
        var quests = SelfTestCatalogs.Quests(pvf);
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        var growth = SelfTestCatalogs.Growth(pvf,learning);
        var now = DateTimeOffset.UtcNow;
        foreach (int id in Ids)
        {
            var quest = quests.Get(id);
            var character = new Character(17,0,"Daily",95,new Location(38,2,450,240),null)
            { Level = 95, CompletedQuests = quest.Prerequisites.ToArray(), QuestCycleDay = QuestCycle.Day(now),
                DungeonState = CharacterDungeonState.NewOrdinary, Learning = new(growth.GetLevelStartExperience(95),[new(0,0,0,[]),new(1,0,0,[])]) };
            That(QuestCycle.NormalDungeonMission(quest) && QuestCycle.Daily(quest) && QuestCoverage.HasSupportedTarget(quest,quests) &&
                QuestCoverage.Unconfirmed(quest).Length == 0 && QuestEligibility.Evaluate(character,quest,quests) is { Eligible: true, Visible: true },
                $"{id}真实定义、原条件和原奖励通过普通每日入口");
            That(!QuestEligibility.Evaluate(character with { Level = (byte)(quest.MinimumLevel-1) },quest,quests).Eligible &&
                !QuestEligibility.Evaluate(character with { CompletedQuests = [..character.CompletedQuests,id] },quest,quests).Eligible,
                $"{id}保留等级及当日完成限制");
            if (quest.Prerequisites.Count > 0)
                That(!QuestEligibility.Evaluate(character with { CompletedQuests = [] },quest,quests).Eligible,$"{id}不跳过前置");
            var accept = QuestLifecyclePlan.Accept(character,quest,quests,items.Items,items.GoldLimits,items.EpicPieces,now);
            var active = accept.ActiveAfter.Single();
            character = character with { ActiveMainQuests = [active] };
            That(active.AcceptanceId != Guid.Empty && active.ConditionState > 0,$"{id}创建真实未完成接取");
            That(QuestCompletionPlan.Prepare(character,quest,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,
                items.EpicPieces,growth,now).Outcome != QuestCompletionOutcome.Accepted,$"{id}缺条件不能领取奖励");
            var targets = quest.Definition!.Dungeons.Select(row => row.DungeonId).Concat(quest.Definition.Drops.Select(row => row.DungeonId));
            if (QuestCycle.IsMoonMountainDailyMission(quest)) targets = targets.Concat([3200, 3201, 3202]);
            if (QuestHuntProgress.TryTargets(quest,out var hunt)) targets = targets.Concat(hunt.Select(row => row.DungeonId));
            if (QuestDungeonClearProgress.TryTarget(quest,out var clear)) targets = targets.Append(clear.DungeonId);
            foreach (int dungeon in targets.Where(value => value > 0).Distinct())
            {
                var definition = dungeons.Get(dungeon);
                byte difficulty = Enumerable.Range(0,5).Select(value => (byte)value).First(value => definition.AllowsDifficulty(value,false));
                That(DungeonEntryPlan.TryCreate(dungeons,character,(uint)dungeon,(uint)id,difficulty,out var plan,out var reason,
                    quest:quest,quests:quests,draw:_ => 0),$"{id}原目标{dungeon}可显式进图：{reason}");
                That(plan!.QuestAcceptanceId == active.AcceptanceId && plan.QuestId == id,$"{id}进图保留接取身份");
                That(!DungeonEntryPlan.TryCreate(dungeons,character with { ActiveMainQuests = [] },(uint)dungeon,(uint)id,difficulty,
                    out _,out _,quest:quest,quests:quests),$"{id}放弃后不能借原任务进图");
            }
            if (quest.Condition != "[seeking]") continue;
            if (QuestCycle.IsMoonMountainDailyMission(quest))
            {
                foreach (uint dungeonId in new uint[] { 3200, 3201, 3202 })
                {
                    var bound = new DungeonEntryPlan(character.Id, 1, dungeonId, 0, byte.MaxValue, 1, null!, null!, null!)
                        { QuestAcceptances = [active] };
                    var loaded = QuestDropResources.Create(character, bound, quests, items.Items, items.EpicPieces);
                    var identity = new DungeonRunIdentity(character.Id, 0, 1, Guid.NewGuid(), 1);
                    var midKill = QuestDropPlan.Create(character, bound, loaded, Guid.NewGuid(), identity, 1, 69033, 1, false, _ => 0, now);
                    That(midKill.Outcomes.IsEmpty && !midKill.Inventory.Changed, $"{id}在{dungeonId}未通关时不发放10094602");
                    var clearDrop = QuestDropPlan.Create(character, bound, loaded, Guid.NewGuid(), identity, 1, 69033, 1, true, _ => 0, now);
                    That(clearDrop.Outcomes is [{ QuestId: 8557, Granted: 10, Error: 0 }] &&
                        clearDrop.Inventory.After.Items.Any(item => item.Core is { ItemId: 10094602, Count: 10 }),
                        $"{id}在{dungeonId}通关发放10个未精炼的念气结晶");
                }
            }
            foreach (var row in quest.Definition.Drops)
            {
                var bound = new DungeonEntryPlan(character.Id,1,(uint)row.DungeonId,0,byte.MaxValue,0,null!,null!,null!)
                    { QuestAcceptances = [active] };
                var loaded = QuestDropResources.Create(character,bound,quests,items.Items,items.EpicPieces);
                var identity = new DungeonRunIdentity(character.Id,0,1,Guid.NewGuid(),1);
                var drop = QuestDropPlan.Create(character,bound,loaded,Guid.NewGuid(),identity,1,row.TemplateId,1,false,_ => 0,now);
                That(drop.Outcomes.Any(outcome => outcome.Granted > 0) &&
                    drop.Inventory.After.Items.Any(item => item.Core.ItemId == row.ItemId),$"{id}仅按原模板及概率行规划任务材料");
                var unrelated = QuestDropPlan.Create(character,bound,loaded,Guid.NewGuid(),identity,1,int.MaxValue,1,false,_ => 0,now);
                That(unrelated.Outcomes.Length == 0,$"{id}未声明怪物不生成任务材料");
            }
            var material = Enumerable.Range(0,quest.IntData.Count/2).Select(at => new InventoryReward(quest.IntData[at*2],quest.IntData[at*2+1])).ToArray();
            var grant = InventoryRewardGrantService.TryGrantBatch(character,material,items.Items,items.GoldLimits,items.EpicPieces);
            That(grant.Accepted && grant.Plan is not null,$"{id}原材料可入包");
            var full = character with { Inventory = grant.Plan!.After };
            full = full with { ActiveMainQuests = QuestInventoryProgress.Refresh(full.ActiveMainQuests,full.Inventory,quests,now.ToUnixTimeSeconds()).ToArray() };
            var reward = QuestCompletionPlan.Prepare(full,quest,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth,now);
            That(reward.Outcome == QuestCompletionOutcome.Accepted && reward.Plan!.DailyPeriod == QuestCycle.Day(now),$"{id}收齐原材料后可规划原奖励及每日领取");
        }
        foreach (int id in new[] { 6688,6689 })
            That(!QuestCycle.NormalDungeonMission(quests.Get(id)),"决斗每日仍等待真实比赛领域，不以普通每日入口放行");
        Console.WriteLine($"PASS {Count-before} 十项真实普通每日资格、进图与奖励规划断言");
        RunLifecycle(towns,pvf,characters,items);
    }
    static void RunLifecycle(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items)
    {
        int before = Count;
        var quests = SelfTestCatalogs.Quests(pvf);
        var learning = SelfTestCatalogs.SkillLearning(pvf);
        var quest = quests.Get(4111);
        var growth = SelfTestCatalogs.Growth(pvf,learning);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        var clock = new Clock { Now = DateTimeOffset.UtcNow };
        using var f = new QuestSceneTests.Fixture(towns,characters,learning,growth,quests,false,
            level:95,completed:quest.Prerequisites.ToArray(),items:[(3329,10)],timeProvider:clock);
        var accepted = f.Session.ChangeQuest(4111,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces);
        That(accepted?.Commit is not null,"曲玉原定义经Session接取并保存身份");
        var prior = f.Read();
        QuestFinishResult Finish() => f.Session.FinishQuest(4111,ushort.MaxValue,quests,
            parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
        f.Sql("CREATE TRIGGER daily_mission_fail AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'daily mission rollback'); END");
        Reject<SqliteException>(() => Finish(),"每日交付晚期失败");
        That(JsonSerializer.Serialize(prior) == JsonSerializer.Serialize(f.Read()) &&
            JsonSerializer.Serialize(prior) == JsonSerializer.Serialize(f.Session.Selected),"每日失败保留材料、奖励、完成位及领取账本");
        f.Sql("DROP TRIGGER daily_mission_fail");
        var completed = Finish();
        That(completed.Receipt is not null && f.Read().QuestDailyClaims.GetValueOrDefault(4111) == QuestCycle.Day(clock.Now) &&
            f.Read().CompletedQuests.Contains(4111) && !f.Read().Inventory.Items.Any(row => row.Core.ItemId == 3329) &&
            f.Read().Inventory.Items.Where(row => row.Core.ItemId == 4732).Sum(row => row.Core.Count) == 1,
            "重试原事务只扣十曲玉并发一紫色曲玉，保存本日领取");
        var saved = JsonSerializer.Serialize(f.Read());
        Finish();
        That(JsonSerializer.Serialize(f.Read()) == saved,"重复交付不重复扣料领奖");
        f.Session.ReturnToRoster(); f.Session.Select(0,resumeTown:true); f.Session.MarkReady();
        That(f.Read().QuestDailyClaims.ContainsKey(4111) &&
            f.Session.ChangeQuest(4111,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is null,
            "重登保存当日账本并拒绝再次接取");
        clock.Now = clock.Now.AddDays(1);
        That(f.Session.AdvanceQuestCycle(clock.Now) is not null && !f.Read().CompletedQuests.Contains(4111) &&
            !f.Read().QuestDailyClaims.ContainsKey(4111) && f.Read().CompletedQuests.Contains(5760),
            "次日仅清本每日完成投影，永久前置保持");
        // Session普通请求使用系统时钟；模拟次日只在已允许注入时间的领域/仓储边界提交，不把测试时间混入线上请求。
        var next = QuestLifecyclePlan.Accept(f.Read(),quest,quests,items.Items,items.GoldLimits,items.EpicPieces,clock.Now);
        var nextCommit = f.Repository.CommitQuestLifecycle(f.Owner,next);
        That(!nextCommit.Duplicate && f.Read().ActiveMainQuests.Single(row => row.QuestId == 4111).ConditionState == 1,
            "次日原仓储可重新接取但缺材料仍未达成");
        RunMoonMountainLifecycle();
        Console.WriteLine($"PASS {Count-before} 普通每日持久领取、回滚、重登及日切断言");

        void RunMoonMountainLifecycle()
        {
            var moonQuest = quests.Get(8557);
            var dungeons = SelfTestCatalogs.Dungeons(pvf);
            var rooms = SelfTestCatalogs.Rooms(pvf);
            var monsters = SelfTestCatalogs.MonsterDrops(pvf);
            var kills = SelfTestCatalogs.KillExperience(pvf);
            var clears = SelfTestCatalogs.ClearExperience(pvf);
            var drops = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
                monsters.Get, items.Items.Get, items.DungeonSpecialDrops);
            var cards = new DungeonCardRewardPolicy(SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
                items.Items, items.GoldLimits, items.EpicPieces, random: _ => 0, prices: SelfTestCatalogs.CardPrices(pvf));
            var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 3201));
            var done = new HashSet<int>(area.Quests);
            if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
            done.UnionWith(area.Map(done).DungeonGates.First(gate => gate.Conditions.Length == 0).Quests);
            done.UnionWith(moonQuest.Prerequisites);
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town, area.Area, (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2));
            foreach ((uint dungeonId, uint questId) in new (uint, uint)[] { (3201, 0), (3200, 8557), (3202, 0) })
            {
                var moonClock = new Clock { Now = DateTimeOffset.UtcNow };
                using var moonFixture = new QuestSceneTests.Fixture(towns, characters, learning, growth, quests, false,
                    level: 95, completed: done.ToArray(), position: position, timeProvider: moonClock);
                var moonAccepted = moonFixture.Session.ChangeQuest(8557, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces);
                That(moonAccepted?.Commit is not null && moonFixture.Read().ActiveMainQuests.Single(row => row.QuestId == 8557).ConditionState == 1,
                    $"月溪镇白鸣处接取8557进攻准备（测试副本{dungeonId}）");
                var entered = moonFixture.Session.EnterFormalDungeon(dungeonId, questId, 1, dungeons, rooms, monsters, items.Items,
                    draw: _ => 0, pieces: items.EpicPieces, prepareDrops: value => drops.PrepareEntry(value));
                That(entered is not null, $"月轮山{dungeonId}固定难度1入场：{moonFixture.Session.LastFormalDungeonRejection}");
                var route = QuestStoryRouteTests.Path(entered!.Entry.Maze.StaticTopology, entered.Entry.StartMap.Position, entered.Entry.BossMap.Position);
                FormalDungeonDeathResult? terminal = null;
                foreach (var point in route)
                {
                    var loaded = moonFixture.Session.CompleteFormalDungeonLoading()!;
                    var targets = loaded.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                        .OrderBy(row => row.IsBoss).ToArray();
                    for (int index = 0; index < targets.Length; index++)
                    {
                        var death = moonFixture.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded, targets[index]), kills, growth);
                        That(death is { Outcome: DungeonRunOutcome.Accepted, Receipt: not null }, $"{dungeonId}怪物{targets[index].MonsterId}正式死亡");
                        if (point == entered.Entry.BossMap.Position && index < targets.Length - 1)
                            That(death.ClearFact is null && death.QuestDropSlots.Count == 0 &&
                                !moonFixture.Read().Inventory.Items.Any(row => row.Core.ItemId == 10094602) &&
                                moonFixture.Read().ActiveMainQuests.Single(row => row.QuestId == 8557).ConditionState == 1,
                                "双子巨人首只怪死亡尚未清房，不提前发放未精炼的念气结晶");
                        terminal = death;
                    }
                    if (point == entered.Entry.BossMap.Position) break;
                    var nextPoint = route[Array.IndexOf(route, point) + 1];
                    That(moonFixture.Session.MoveFormalDungeon((byte)nextPoint.X, (byte)nextPoint.Y) is not null, $"{dungeonId}过图进入领主房");
                }
                That(terminal?.ClearFact is not null && terminal.QuestDropSlots.Count == 1 &&
                    moonFixture.Read().Inventory.Items.Where(row => row.Core.ItemId == 10094602).Sum(row => row.Core.Count) == 10 &&
                    moonFixture.Read().ActiveMainQuests.Single(row => row.QuestId == 8557).ConditionState == 0,
                    $"通关月轮山{dungeonId}发放未精炼的念气结晶10094602×10并完成8557条件");
                That(moonFixture.Session.SettleFormalDungeon(moonFixture.Read().Id, 99, 99, [6, 0, 8], dungeons, clears, growth, cards) is { Outcome: DungeonRunOutcome.Accepted } &&
                    moonFixture.Session.ReturnFromFormalDungeon() is not null, $"{dungeonId}结算回城");
                long beforePass = moonFixture.Read().Inventory.Items.Where(row => row.Core.ItemId == 490000338).Sum(row => (long)row.Core.Count);
                var finish = moonFixture.Session.FinishQuest(8557, ushort.MaxValue, quests, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth);
                That(finish.Outcome == QuestFinishOutcome.Committed &&
                    !moonFixture.Read().Inventory.Items.Any(row => row.Core.ItemId == 10094602) &&
                    moonFixture.Read().Inventory.Items.Where(row => row.Core.ItemId == 490000338).Sum(row => (long)row.Core.Count) == beforePass + 1 &&
                    moonFixture.Read().CompletedQuests.Contains(8557) && moonFixture.Read().QuestDailyClaims.ContainsKey(8557),
                    $"8557交付扣除10个未精炼的念气结晶并发放深渊派对通行证：{finish.Reason}");
                moonClock.Now = moonClock.Now.AddDays(1);
                That(moonFixture.Session.AdvanceQuestCycle(moonClock.Now) is not null && !moonFixture.Read().CompletedQuests.Contains(8557),
                    "跨06:00重置8557每日完成状态");
            }
        }
    }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

}
