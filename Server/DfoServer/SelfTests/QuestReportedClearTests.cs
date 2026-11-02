using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestReportedClearTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items)
    {
        int before = Count;
        var source = SelfTestCatalogs.Quests(pvf);
        var skills = SelfTestCatalogs.SkillLearning(pvf);
        var growth = SelfTestCatalogs.Growth(pvf, skills);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var kills = SelfTestCatalogs.KillExperience(pvf);
        var drops = SelfTestCatalogs.MonsterDrops(pvf);
        var quest = QuestEligibilityTests.Quest(29986) with { Grade = "[side]", Condition = "[condition under clear]",
            SubType = 8, IntData = [3, -1, 2], Attributes = ["not give exp quest"] };
        var skill = quest with { Id = 29987, Condition = "[use skill]", SubType = -1, IntData = [1, 2] };
        var catalog = new QuestCatalog(source.All.Concat(new[] { quest, skill }).ToDictionary(row => row.Id));
        using var f = new QuestSceneTests.Fixture(towns, characters, skills, growth, catalog, false, 1, []);
        f.Session.LeaveRoom();
        foreach (ushort id in new ushort[] { 3145, 29986, 29987 })
            That(f.Session.ChangeQuest(id, QuestLifecycleAction.Accept, catalog, items.Items, items.GoldLimits, items.EpicPieces)?.Commit is not null,
                "统计条件沿正常任务接取：" + id);
        f.Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0));
        var opening = f.Session.EnterFormalDungeon(3, 0, 0, dungeons, rooms, drops, items.Items, _ => 0, items.EpicPieces)!;
        var receipt = f.Session.CompleteFormalDungeonLoading()!;
        That(ObserveSkill()?.Character.ActiveMainQuests.Single(row => row.QuestId == 29987).ConditionState == 1 &&
            ObserveSkill()?.Character.ActiveMainQuests.Single(row => row.QuestId == 29987).ConditionState == 0,
            "技能使用沿已加载原Run的普通33累计，不借用称号417或一次报告清除全部次数");
        var observed = f.Read();
        That(ObserveSkill()?.Outcome == QuestLifecycleOutcome.Unchanged && QuestCompletionRepositoryTests.Same(observed, f.Read()),
            "技能计数归零后重复33没有资产或版本变化");
        That(Report()?.Commit is null && f.Read().ActiveMainQuests.Single(row => row.QuestId == 29986).ConditionState == 1,
            "客户端统计不能替代尚未发生的真实清图");
        var path = QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology, opening.Entry.StartMap.Position, opening.Entry.BossMap.Position);
        foreach (var point in path)
        {
            foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).ToArray())
                That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth).Outcome == DungeonRunOutcome.Accepted,
                    "统计测试按真实死亡完成原路线");
            if (point == opening.Entry.BossMap.Position) break;
            var next = path[Array.IndexOf(path, point) + 1];
            f.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y);
            receipt = f.Session.CompleteFormalDungeonLoading()!;
        }
        That(f.Read().ActiveMainQuests.Single(row => row.QuestId == 29986).ConditionState == 1 && f.Session.ReadClearedQuest(29986) is null,
            "真实清图不伪造尚未报告的本地背击条件");
        var saved = f.Read();
        f.Sql("CREATE TRIGGER stat_clear_abort AFTER INSERT ON character_quest_clear_receipts WHEN NEW.quest_id=29986 BEGIN SELECT RAISE(ABORT,'stat clear rollback'); END");
        Reject<SqliteException>(() => Report(), "统计回执末段SQL失败");
        That(QuestCompletionRepositoryTests.Same(saved, f.Read()) && f.Session.ReadClearedQuest(29986) is null,
            "失败回滚条件、清图回执与角色revision");
        f.Sql("DROP TRIGGER stat_clear_abort");
        var reported = Report();
        That(reported?.Commit is { ProjectionCurrent: true } &&
            reported.Character.ActiveMainQuests.Single(row => row.QuestId == 29986).ConditionState == 0 &&
            f.Session.ReadClearedQuest(29986)?.Clear is { DungeonId: 3, SourceType: QuestClearSourceType.MonsterDeath },
            "统计报告只在原真实终末后原子归零并保存可领奖凭据");
        var after = f.Read();
        That(Report()?.Outcome == QuestLifecycleOutcome.Unchanged && QuestCompletionRepositoryTests.Same(after, f.Read()),
            "重复统计确认不重复写revision或清图回执");
        var completion = QuestCompletionPlan.Prepare(after, quest, ushort.MaxValue, catalog, SelfTestCatalogs.QuestRewardParameters(pvf),
            items.Items, items.GoldLimits, items.EpicPieces, growth, DateTimeOffset.UtcNow, f.Session.ReadClearedQuest(29986)!.Clear);
        That(completion.Outcome == QuestCompletionOutcome.Accepted, "条件已达成后进入原领奖链：" + completion.Reason);
        Console.WriteLine($"PASS {Count - before} 客户端统计报告、真实清图、SQL回滚与交付凭据断言");

        DfoServer.Game.Session.QuestLifecycleReceipt? Report() => f.Session.ChangeQuest(29986, QuestLifecycleAction.Condition,
            catalog, items.Items, items.GoldLimits, items.EpicPieces, new(null, 0, 0));
        DfoServer.Game.Session.QuestLifecycleReceipt? ObserveSkill() => f.Session.ChangeQuest(29987, QuestLifecycleAction.Condition,
            catalog, items.Items, items.GoldLimits, items.EpicPieces, new(null, 0, 0));
    }
}
