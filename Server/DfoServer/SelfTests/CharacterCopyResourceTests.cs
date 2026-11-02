using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class CharacterCopyTests
{
    public static void RunResources(TcpTestResources r)
    {
        int checks = Count;
        var growth = SelfTestCatalogs.Growth(r.Archive, r.Learning);
        var parameters = SelfTestCatalogs.QuestRewardParameters(r.Archive);
        var a = QuestEligibilityTests.Quest(100) with { SubType = 1, Difficulty = "G", MinimumLevel = 1,
            Attributes = [], Rewards = [], Definition = new([]) { Job = "[all]" } };
        var c = a with { Id = 101, Path = "fixture/101.qst", Definition = a.Definition! with { PrerequisiteGroups = [[100]] } };
        var n = a with { Id = 102, Path = "fixture/102.qst", MinimumLevel = 13,
            Definition = a.Definition! with { PrerequisiteGroups = [[100]] } };
        var graph = QuestStageGraph.Build(new(new[] { a, c, n }.ToDictionary(row => row.Id)));
        using var f = new QuestOrdinaryCompletionTests.Fixture(r.Towns, r.Characters, r.Learning,
            growth, parameters, r.ItemResources, a, materialCount: 0, definitions: [a, c, n], active: [],
            configure: candidate => candidate with { Level = 11, InitialLearning = SkillPointLedger.Synchronize(
                candidate.Job, candidate.GrowType, 11, candidate.InitialLearning with {
                    Experience = growth.GetLevelStartExperience(11) }, r.Learning) }, stages: graph);
        That(f.Change(100, QuestLifecycleAction.Accept) is not null &&
            f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.Committed, "复制夹具产生真实延期阶段因果链");
        That(f.Change(101, QuestLifecycleAction.Accept) is not null, "复制夹具产生真实进行中接取记录");
        var source = f.Read();
        var original = JsonSerializer.Serialize(source);
        var sourceStage = f.Repository.LoadQuestStageState(f.Owner, source);
        f.Session.ReturnToRoster();
        var copy = f.Repository.CopyCharacter(f.Owner, new(source.Name, source.Id, source.Revision,
            "CopyDeferred", r.Towns.Room, r.Towns.InitialReturn));
        var reopened = new CharacterRepository(f.OpenPath, f.Catalog);
        var copiedStage = reopened.LoadQuestStageState(f.Owner, copy);
        That(copiedStage.Witnesses is [{ Status: QuestStageStatus.Deferred }] &&
            copiedStage.Witnesses[0].SourceAcceptanceId != sourceStage.Witnesses[0].SourceAcceptanceId,
            "真实延期来源与proof使用新GUID并可重开读取");
        That(copy.ActiveMainQuests.Single().AcceptanceId != source.ActiveMainQuests.Single().AcceptanceId,
            "真实进行中任务使用独立接取GUID");
        var session = new GameSession(reopened, r.Towns, f.Owner, stageGraph: graph);
        try
        {
            session.Select(copy.Slot); session.MarkReady();
            var result = session.FinishQuest(101, ushort.MaxValue, f.Catalog, parameters,
                r.ItemResources.Items, r.ItemResources.GoldLimits, r.ItemResources.EpicPieces, growth);
            That(result.Outcome == QuestFinishOutcome.Committed && session.Selected!.Level == 13,
                "复制角色经真实Session完成后续任务并沿复制的阶段来源补至13级");
            var completed = reopened.LoadCharacter(f.Owner, copy.Slot);
            That(reopened.LoadQuestStageState(f.Owner, completed).Witnesses is [{ Status: QuestStageStatus.Granted }],
                "复制后的阶段来源可正常消费且保存完成状态");
            session.ReturnToRoster();
            var grantedCopy = reopened.CopyCharacter(f.Owner, new(completed.Name, completed.Id, completed.Revision,
                "CopyGranted", r.Towns.Room, r.Towns.InitialReturn));
            That(reopened.LoadQuestStageState(f.Owner, grantedCopy).Witnesses is [{ Status: QuestStageStatus.Granted }] &&
                grantedCopy.Level == 13 && grantedCopy.Learning.Experience == completed.Learning.Experience,
                "已消费阶段连同新claim因果链可再次独立复制而不重复发奖");
            session.Select(grantedCopy.Slot); session.MarkReady();
            var replay = session.FinishQuest(101, ushort.MaxValue, f.Catalog, parameters,
                r.ItemResources.Items, r.ItemResources.GoldLimits, r.ItemResources.EpicPieces, growth);
            That(replay.Outcome != QuestFinishOutcome.Committed || replay.Receipt!.Commit.Duplicate,
                "复制的已完成任务不能再次取得普通或阶段奖励");
            That(JsonSerializer.Serialize(f.Read()) == original, "后续任务与二次复制均不修改最初角色");
            using var db = f.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_key_check";
            using var reader = cmd.ExecuteReader(); That(!reader.Read(), "非空阶段因果链复制后无孤立外键");
        }
        finally { session.End(); }

        var catalog = new DevelopmentCatalog(r.Archive, r.ItemResources, f.Catalog);
        string command = $"account {f.Owner.Account} character-copy {source.Name} {source.Id} {source.Revision} CopyViaGm";
        That(DevelopmentGm.Answer(command, f.Owner.Account, f.Repository, f.Owners, _ => f.Session, catalog: catalog).StartsWith("ERR "),
            "真实GM拒绝在线账号复制");
        That(DevelopmentGm.Answer(command, f.Owner.Account, f.Repository, f.Owners, _ => null, catalog: catalog).StartsWith("ERR "),
            "真实GM拒绝接管已有owner租约");
        string account = f.Owner.Account;
        f.Owner.Dispose();
        string answer = DevelopmentGm.Answer(command, account, f.Repository, f.Owners, _ => null, catalog: catalog);
        That(answer.StartsWith("OK "), "真实GM在账号离线及租约释放后成功创建");
        using (var owner = f.Owners.Acquire(account, () => { }))
        {
            using var json = JsonDocument.Parse(answer[3..]);
            var entry = f.Repository.ListRoster(owner).Single(row => row.Name == "CopyViaGm");
            var durable = f.Repository.LoadCharacter(owner, entry.Slot);
            That(durable.Id == json.RootElement.GetProperty("characterId").GetUInt16() && durable.Id != source.Id &&
                durable.Slot == json.RootElement.GetProperty("slot").GetByte(), "GM成功返回的是实际持久化的新身份");
        }
        That(DevelopmentGm.Answer(command, account, f.Repository, f.Owners, _ => null, catalog: catalog).StartsWith("ERR "),
            "重发同名复制请求拒绝且不生成第二份");
        using (var broken = new Fixture())
        {
            broken.Sql("UPDATE sqlite_sequence SET seq=2147483647 WHERE name='character_inventory_items'");
            var snapshot = broken.LoadSource();
            string brokenAccount = broken.Owner.Account;
            broken.Owner.Dispose();
            string error = DevelopmentGm.Answer($"account {brokenAccount} character-copy {snapshot.Name} {snapshot.Id} {snapshot.Revision} CopyOverflow",
                brokenAccount, broken.Repository, broken.Owners, _ => null, catalog: catalog);
            That(error.StartsWith("ERR ") && error.Contains("回滚"), "UID溢出转换为明确GM拒绝而非监听任务异常退出");
        }
        Console.WriteLine($"PASS {Count - checks} character copy real quest continuation and GM guards assertions");
    }
}
