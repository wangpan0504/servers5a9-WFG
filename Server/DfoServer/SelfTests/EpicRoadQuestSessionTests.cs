using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class EpicRoadQuestSessionTests
{
    public static void RunResources(PvfArchive pvf, ItemResources items)
    {
        int before = Count;
        var towns = TownCatalog.Load(pvf, epicRoad2019Enabled: true);
        var characters = CharacterCatalog.Load(pvf, items.Items);
        var learning = Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = Testing.SelfTestCatalogs.Growth(pvf, learning);
        var parameters = Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var quest = QuestCatalog.Load(pvf, epicRoad2019Enabled: true).Get(8712);
        var clock = new DailyBoosterTestClock(new DateTimeOffset(2019, 12, 10, 12, 0, 0, TimeSpan.FromHours(8)));
        ServerEventService events = null!;
        NewCharacter At95(NewCharacter source) => source with { Level = 95,
            InitialLearning = SkillPointLedger.Synchronize(source.Job, source.GrowType, 95,
                source.InitialLearning with { Experience = growth.GetLevelStartExperience(95) }, learning, learning.ExpertContractLevels) };
        using var f = new QuestOrdinaryCompletionTests.Fixture(towns, characters, learning, growth, parameters, items, quest,
            materialCount: 0, active: [], configure: At95, timeProvider: clock,
            createEvents: path => events = new(new ServerEventRepository(path), clock));
        That(QuestEpicRoad2019Entry.Supports(quest) && QuestClientSceneProgress.DialogueNpc(f.Read(), 400001102, quest, f.Catalog, towns),
            "真实8712完整资源与原城镇NPC构成会面场景，不合成NPC");
        That(!QuestCoverage.Unconfirmed(quest).Contains("show quest on event") &&
            QuestCoverage.Unconfirmed(quest with { Id = 8713 }).Contains("show quest on event"),
            "10005显示字段仅由完整8712合同识别，不泛开其它活动任务");
        var eventField = quest.Definition!.Fields.Single(field => field.Name == "show quest on event");
        foreach (var fields in new[] {
            quest.Definition.Fields.Add(eventField),
            quest.Definition.Fields.Select(field => field == eventField ? field with { Children = [new("unknown", [], [])] } : field).ToImmutableArray(),
            quest.Definition.Fields.Select(field => field == eventField ? field with { Values = [new(0, 10006, null)] } : field).ToImmutableArray()
        })
        {
            var unsupported = quest with { Definition = quest.Definition with { Fields = fields } };
            That(!QuestEpicRoad2019Entry.Supports(unsupported) && QuestCoverage.Unconfirmed(unsupported).Contains("show quest on event"),
                "重复、子节点及未知活动号仍作为未确认声明拒绝");
        }
        var initial = f.Read();
        That(f.Change(8712, QuestLifecycleAction.Accept)?.Commit is null && Same(initial, f.Read()) && f.Receipts == 0,
            "10005关闭时真实8712接取不改变角色或奖励回执");
        events.Override(ServerEventService.EpicRoadId, true);
        var accepted = f.Change(8712, QuestLifecycleAction.Accept);
        That(accepted?.Commit is not null &&
            f.Read().ActiveMainQuests is [{ QuestId: 8712, AcceptanceId: var acceptance }] && acceptance != Guid.Empty,
            "10005开启后Session接取真实8712并持久化接取身份：" + accepted?.Reason);
        That(f.Change(8712, QuestLifecycleAction.Condition, new(null, 0, 0))?.Commit is not null &&
            f.Read().ActiveMainQuests is [{ QuestId: 8712, ConditionState: 0 }],
            "原会面NPC条件经Session提交，不预写领奖完成位");
        // 第二角色的已接取槽只用于仓储竞争，不代表自然客户端接取证据。
        var alternate = f.Repository.Create(f.Owner, At95(CharacterCreation.Prepare("EpicAlt", characters.Get(0),
            towns.Room, towns.InitialReturn, learning.Get)) with { ActiveMainQuests = [new(0, 8712, 0)] });
        QuestCompletionPlan Plan(Character source)
        {
            var plan = QuestCompletionPlan.Prepare(source, quest, ushort.MaxValue, f.Catalog, parameters, items.Items,
                items.GoldLimits, items.EpicPieces, growth, clock.GetUtcNow(), epicRoad2019Active: true).Plan
                ?? throw new InvalidOperationException("真实8712领奖规划被拒绝");
            plan.RequiredEvent = new(events.Read(ServerEventService.EpicRoadId).Schedule, clock);
            return plan;
        }
        var source = f.Read();
        var closedPlan = Plan(source);
        events.Override(ServerEventService.EpicRoadId, false);
        Testing.TestCheck.Reject<InvalidOperationException>(() => f.Repository.CommitQuestCompletion(f.Owner, closedPlan),
            "8712规划后关闭10005，奖励仓储事务必须拒绝");
        That(Same(source, f.Read()) && Same(alternate, f.Repository.LoadCharacter(f.Owner, alternate.Slot)) && f.Receipts == 0,
            "晚期关闭保留两角色金币、接取与账号完成位");
        events.Override(ServerEventService.EpicRoadId, true);
        var staleAlternate = Plan(f.Repository.LoadCharacter(f.Owner, alternate.Slot));
        var finished = f.Finish(ushort.MaxValue, 8712);
        var primaryAfter = f.Read();
        var alternateAfter = f.Repository.LoadCharacter(f.Owner, alternate.Slot);
        That(finished.Outcome == QuestFinishOutcome.Committed && finished.Receipt!.Commit.GoldAmount == 100 &&
            primaryAfter.Inventory.Gold == source.Inventory.Gold + 100 && alternateAfter.Inventory.Gold == alternate.Inventory.Gold,
            "8712通过实际Session完成，仅首个角色收到资源声明100金币");
        That(primaryAfter.AccountCompletedQuests.Contains(8712) && alternateAfter.AccountCompletedQuests.Contains(8712) &&
            primaryAfter.ActiveMainQuests.All(row => row.QuestId != 8712) && alternateAfter.ActiveMainQuests.All(row => row.QuestId != 8712) && f.Receipts == 1,
            "8712账号完成与两角色清槽在同笔奖励事务完成");
        Testing.TestCheck.Reject<InvalidOperationException>(() => f.Repository.CommitQuestCompletion(f.Owner, staleAlternate),
            "另一角色陈旧8712规划不能重复发奖");
        That(Same(primaryAfter, f.Read()) && Same(alternateAfter, f.Repository.LoadCharacter(f.Owner, alternate.Slot)) && f.Receipts == 1,
            "陈旧规划拒绝后完整保留两角色资产与唯一回执");
        That(f.Finish(ushort.MaxValue, 8712).Outcome == QuestFinishOutcome.AlreadyCompleted && Same(primaryAfter, f.Read()) && f.Receipts == 1,
            "同Session重复完成8712只返回已完成，不再发金币");
        var origin = f.Session.Selected!.Position;
        That(f.Session.Travel(new(123, 0, 1084, 90, 5, origin.Town, origin.Area, 0, 400001103)) &&
            f.Read().Position is { Town: 123, Area: 0, X: 1084, Y: 90 } && f.Receipts == 1,
            "8712同Session完成后沿原亚瑟NPC传入123城镇，位置持久化且不重复领奖：" + f.Session.TravelReason);
        That(f.Session.Travel(new(origin.Town, origin.Area, origin.X, origin.Y, 5, 123, 0, 0)) &&
            f.Read().Position == origin && f.Receipts == 1,
            "原亚瑟2退出角色沿本次NPC来路返回，同一角色与奖励回执保持：" + f.Session.TravelReason);
        Console.WriteLine($"PASS {Count - before} 真实8712 Session活动门控与账号一次奖励事务断言");
    }
}
