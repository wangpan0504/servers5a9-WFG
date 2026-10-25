using DfoServer.GameWorld.Characters;
using DfoServer.Infrastructure;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
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
        Console.WriteLine($"PASS {Count-before} 普通每日入口边界断言");
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
        Console.WriteLine($"PASS {Count-before} 普通每日持久领取、回滚、重登及日切断言");
    }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

}
