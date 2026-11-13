using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019QuestTests
{
    public static void RunCatalog(PvfArchive pvf)
    {
        int before = Count;
        var original = Read(pvf);
        That(original is { Id: 8698, Grade: "[special]", Difficulty: "G", Npc: 400001051, CompleteNpc: -1,
            MinimumLevel: 95, MaximumLevel: 99, Condition: "[meet npc]", SubType: 1, IntData: [400001051],
            RewardType: "[item]", Definition: { IsEvent: true, Job: "[all]", GrowType: -1, Diagnostics.Length: 0 } } &&
            original.Rewards is [{ Type: 0, Text: null, Value: 0 }, { Type: 0, Text: null, Value: 1 }],
            "8698接入读取真实QST1，保留special、event1及0×1原文，不伪造普通任务");
        var catalog = QuestCatalog.Load(pvf);
        That(catalog.TryGet(8698, out var restored) && restored.Path == QuestStrongway2019Entry.ResourcePath &&
            restored.Definition is { Diagnostics.Length: 0, IsEvent: true } && restored.Rewards.SequenceEqual(original.Rewards),
            "原生已证8698恢复QST1缺失索引，经原QuestDefinitionReader装载，不改PVF");
        That(!catalog.All.Any(quest => quest.Path == "n_quest/strong_way/strong_way_2.qst"),
            "后继QST2仅证明8698前置，不据路径猜8699或注册未知编号");
        var absent = new Dictionary<int, string>();
        QuestCatalog.RestoreStrongway2019Index(absent, false);
        That(absent.Count == 0, "真实QST1缺失时不生成空定义或占用8698");
        var explicitId = new Dictionary<int, string> { [8698] = "n_quest/explicit.qst" };
        QuestCatalog.RestoreStrongway2019Index(explicitId, true);
        That(explicitId.Count == 1 && explicitId[8698] == "n_quest/explicit.qst", "显式权威编号映射始终优先");
        var explicitPath = new Dictionary<int, string> { [123] = QuestStrongway2019Entry.ResourcePath };
        QuestCatalog.RestoreStrongway2019Index(explicitPath, true);
        That(explicitPath.Count == 1 && !explicitPath.ContainsKey(8698), "路径已有其它权威编号时不再注册别名");
        var missing = new Dictionary<int, string>();
        QuestCatalog.RestoreStrongway2019Index(missing, true);
        QuestCatalog.RestoreStrongway2019Index(missing, true);
        That(missing.Count == 1 && missing[8698] == QuestStrongway2019Entry.ResourcePath, "唯一缺失首步只恢复一次");
        Console.WriteLine($"PASS {Count - before} 首期8698真实资源与缺失索引恢复");
    }

    public static void RunEligibility(PvfArchive pvf, TownCatalog towns)
    {
        int before = Count;
        var quest = Read(pvf);
        var catalog = new QuestCatalog(new() { [quest.Id] = quest });
        That(QuestStrongway2019Entry.Supports(quest), "仅完整8698真实首步拥有已证活动入口");
        That(QuestEligibility.InspectResource(quest).Entry == QuestResourceEntry.Event &&
            QuestEligibility.InspectResource(quest, strongway2019Active: true).Entry == QuestResourceEntry.Ordinary,
            "未提供312启用事实保持活动关闭，显式启用仅开放已证完整首步");
        foreach (byte job in Enumerable.Range(0, 16).Select(value => (byte)value))
        foreach (byte grow in new byte[] { 0, 1, 5, 16, 17, 21, 32, 33, 37 })
        {
            var character = new Character(1, 0, "SWQuest", 1, towns.Room, null) { Level = 95, Job = job, GrowType = grow };
            That(QuestEligibility.Evaluate(character, quest, catalog, strongway2019Active: true) is { Eligible: true, Visible: true } &&
                QuestEligibility.Candidates(character, catalog, strongway2019Active: true).Contains(8698),
                $"原all/grow-1覆盖合法职业与阶段，活动开启可见且可接：{job}/{grow}");
            That(!QuestEligibility.Evaluate(character, quest, catalog).Eligible &&
                !QuestEligibility.Candidates(character, catalog).Contains(8698) &&
                !QuestEligibility.Evaluate(character with { Level = 94 }, quest, catalog, strongway2019Active: true).Eligible &&
                !QuestEligibility.Evaluate(character with { CompletedQuests = [8698] }, quest, catalog, strongway2019Active: true).Eligible &&
                !QuestEligibility.Evaluate(character with { ActiveMainQuests = [new(0, 8698, 0)] }, quest, catalog, strongway2019Active: true).Eligible,
                "不开活动、不够95、已完成或已接取仍经原资格拒绝");
        }
        var definition = quest.Definition!;
        StoryQuest[] unsupported =
        [
            quest with { Id = 8699 }, quest with { Path = "n_quest/strong_way/strong_way_2.qst" },
            quest with { Grade = "[side]" }, quest with { Difficulty = "F" }, quest with { Npc = 1 },
            quest with { CompleteNpc = 400001051 }, quest with { MinimumLevel = 94 }, quest with { MaximumLevel = 95 },
            quest with { SubType = -1 }, quest with { IntData = [400001051, 1] }, quest with { DeclaredGoldMultiple = 1 },
            quest with { Rewards = [new(0, 0, null), new(0, 2, null)] },
            quest with { Definition = definition with { IsEvent = false } },
            quest with { Definition = definition with { Job = "[swordman]" } },
            quest with { Definition = definition with { GrowType = 1 } },
            quest with { Definition = definition with { Fields = definition.Fields.Select(field => field.Name == "event"
                ? field with { Values = [new(0, 2, null)] } : field).ToImmutableArray() } },
            quest with { Definition = definition with { PrerequisiteGroups = [[123]] } },
            quest with { Definition = definition with { SelectRewards = [new(0, 2058, null), new(0, 1, null)] } },
            quest with { Definition = definition with { Diagnostics = ["未确认声明"] } }
        ];
        foreach (var other in unsupported)
            That(!QuestStrongway2019Entry.Supports(other), "活动兼容不借8698身份泛开变形资源或不同奖励");
        Console.WriteLine($"PASS {Count - before} 首期8698完整形状与312资格门禁");
    }

    public static void RunLifecycle(PvfArchive pvf, TownCatalog towns, CharacterCatalog characters, ItemResources items)
    {
        int before = Count;
        string directory = Path.Combine(Path.GetTempPath(), "S5A9-strongway-quest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var quests = QuestCatalog.Load(pvf);
            var quest = quests.Get(8698);
            var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
            var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning);
            var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
            var database = Path.Combine(directory, "game.db");
            var repository = new CharacterRepository(database, quests);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("strongway-real-quest", () => { });
            var candidate = CharacterCreation.Prepare("StrongQuest", characters.Get(0), towns.Room, towns.InitialReturn, learning.Get);
            candidate = candidate with { Level = 95, InitialLearning = SkillPointLedger.Synchronize(0, 0, 95,
                candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(95) }, learning, learning.ExpertContractLevels) };
            var created = repository.Create(owner, candidate);
            var events = new ServerEventService(new ServerEventRepository(database));
            events.Override(ServerEventService.Strongway2019Id, false);
            var tracked = new QuestCompletionRepositoryTests.LostResultRepository(repository) { LoseCompletionResult = false };
            var session = new GameSession(tracked, towns, owner, serverEvents: events);
            session.Select(created.Slot); session.MarkReady();
            That(session.ChangeQuest(8698, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces)?.Commit is null &&
                !repository.LoadCharacter(owner, created.Slot).CompletedQuests.Contains(8698),
                "312关闭时真实31不能接取或预写8698完成位");
            events.Override(312, true);
            var beforeAccept = repository.LoadCharacter(owner, created.Slot);
            var staleAccept = QuestLifecyclePlan.Accept(beforeAccept, quest, quests, items.Items, items.GoldLimits,
                items.EpicPieces, DateTimeOffset.UtcNow, strongway2019Active: true);
            staleAccept.RequiredEvent = new(events.Read(312).Schedule, TimeProvider.System);
            events.Override(312, false);
            Reject<InvalidOperationException>(() => repository.CommitQuestLifecycle(owner, staleAccept),
                "接取计划开→关后在原写事务中拒绝");
            events.Override(312, true);
            Reject<InvalidOperationException>(() => repository.CommitQuestLifecycle(owner, staleAccept),
                "关闭再开启已换schedule revision，不能复活旧接取计划");
            That(QuestCompletionRepositoryTests.Same(beforeAccept, repository.LoadCharacter(owner, created.Slot)) &&
                Scalar("SELECT count(*) FROM character_quest_lifecycle_receipts") == 0,
                "过期接取计划不写槽、资产、完成位或receipt");
            var accepted = session.ChangeQuest(8698, QuestLifecycleAction.Accept, quests, items.Items, items.GoldLimits, items.EpicPieces);
            That(accepted?.Commit is not null && session.Selected!.ActiveMainQuests is [{ QuestId: 8698, ConditionState: 0, AcceptanceId: var identity }] &&
                identity != Guid.Empty && !session.Selected.CompletedQuests.Contains(8698),
                "真实31只持久化普通接取身份及sub1零scalar，不提前签发完成位");
            var prior = repository.LoadCharacter(owner, created.Slot);
            var staleFinish = QuestCompletionPlan.Prepare(prior, quest, ushort.MaxValue, quests, parameters, items.Items,
                items.GoldLimits, items.EpicPieces, growth, DateTimeOffset.UtcNow, strongway2019Active: true).Plan;
            That(staleFinish is not null && staleFinish.GoldAmount == 1 && staleFinish.Rewards.Length == 0,
                "原生0×1沿通用allowGold冻结1金币，实体奖励集合为空");
            staleFinish!.RequiredEvent = new(events.Read(312).Schedule, TimeProvider.System);
            events.Override(312, false);
            That(repository.CommitQuestLifecycle(owner, accepted!.Commit!.Plan) is { Duplicate: true, ProjectionCurrent: true } &&
                QuestCompletionRepositoryTests.Same(prior, repository.LoadCharacter(owner, created.Slot)),
                "已提交31在活动关闭后仍可精确读取原receipt，不重放写事务");
            Reject<InvalidOperationException>(() => repository.CommitQuestCompletion(owner, staleFinish),
                "完成计划开→关后在原奖励写事务中拒绝");
            That(session.FinishQuest(8698, ushort.MaxValue, quests, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth).Receipt is null &&
                QuestCompletionRepositoryTests.Same(prior, repository.LoadCharacter(owner, created.Slot)),
                "活动关闭后的34重验gate且完整保留原接取及资产");
            events.Override(312, true);
            Reject<InvalidOperationException>(() => repository.CommitQuestCompletion(owner, staleFinish),
                "关闭再开启不复活旧34计划和冻结奖励");
            That(QuestCompletionRepositoryTests.Same(prior, repository.LoadCharacter(owner, created.Slot)) &&
                Scalar("SELECT count(*) FROM character_quest_completion_receipts") == 0,
                "过期完成计划完整保留接取与资产且不写receipt");
            var result = session.FinishQuest(8698, ushort.MaxValue, quests, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth);
            That(result.Receipt?.Commit is { Duplicate: false, ConditionType: 4 } completed &&
                completed.Character.CompletedQuests.Contains(8698) && completed.Character.ActiveMainQuests.All(row => row.QuestId != 8698),
                "真实34沿原QuestCompletion事务完成8698并清理本接取槽");
            var after = repository.LoadCharacter(owner, created.Slot);
            That(after.Inventory.Gold == prior.Inventory.Gold + 1 &&
                after.Inventory.Items.SequenceEqual(prior.Inventory.Items) &&
                Scalar("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=8698 AND gold_amount=1") == 1,
                "真实交付原子保存1金币和唯一回执，不创建item0或新UID实体");
            That(session.FinishQuest(8698, ushort.MaxValue, quests, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth).Outcome ==
                QuestFinishOutcome.AlreadyCompleted && QuestCompletionRepositoryTests.Same(after, repository.LoadCharacter(owner, created.Slot)),
                "重复34保留唯一完成结果，不重算发奖或重新写库");
            events.Override(312, false);
            That(repository.CommitQuestCompletion(owner, tracked.LastPlan!) is { Duplicate: true, ProjectionCurrent: true } &&
                QuestCompletionRepositoryTests.Same(after, repository.LoadCharacter(owner, created.Slot)) &&
                Scalar("SELECT count(*) FROM character_quest_completion_receipts") == 1,
                "已提交34在活动关闭后只读原receipt，不因新gate重奖或丢已完成结果");
            session.End();
            using var replacement = owners.Acquire(owner.Account, () => { });
            var reopened = new CharacterRepository(database, quests);
            var restored = new GameSession(reopened, towns, replacement, serverEvents: events);
            restored.Select(created.Slot); restored.MarkReady();
            That(restored.Selected!.CompletedQuests.Contains(8698) &&
                TownPackets.CompletedQuests(restored.Selected)[4 + 8698] == 1 &&
                !QuestEligibility.Candidates(restored.Selected, quests, strongway2019Active: true).Contains(8698),
                "新仓储/owner/Session从原存档恢复完成表356，原生8698门禁无需108或GM完成标记");
            restored.End();

            long Scalar(string sql)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
                db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }
        finally { TestFiles.DeleteDirectory(directory, true); }
        Console.WriteLine($"PASS {Count - before} 首期8698正常31→34完成与真实存档重连");
        RunAcceptPendingResources(pvf, towns, characters, items);
        RunCompletionPendingResources(pvf, towns, characters, items);
    }

    public static void RunAcceptPendingResources(PvfArchive pvf, TownCatalog towns, CharacterCatalog characters, ItemResources items) =>
        RunPendingResources(pvf, towns, characters, items, completion: false);

    public static void RunCompletionPendingResources(PvfArchive pvf, TownCatalog towns, CharacterCatalog characters, ItemResources items) =>
        RunPendingResources(pvf, towns, characters, items, completion: true);

    static void RunPendingResources(PvfArchive pvf, TownCatalog towns, CharacterCatalog characters, ItemResources items, bool completion)
    {
        int before = Count;
        var quests = QuestCatalog.Load(pvf);
        var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        foreach (bool reopen in new[] { false, true })
        {
            string directory = Path.Combine(Path.GetTempPath(), "S5A9-strongway-quest-pending-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string database = Path.Combine(directory, "game.db");
                var repository = new CharacterRepository(database, quests);
                var owners = new AccountOwners();
                using var owner = owners.Acquire("strongway-pending", () => { });
                var candidate = CharacterCreation.Prepare("PendingQuest", characters.Get(0), towns.Room, towns.InitialReturn, learning.Get);
                candidate = candidate with { Level = 95, InitialLearning = SkillPointLedger.Synchronize(0, 0, 95,
                    candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(95) }, learning, learning.ExpertContractLevels) };
                var created = repository.Create(owner, candidate);
                var events = new ServerEventService(new ServerEventRepository(database));
                var originalSchedule = events.Override(312, true).Schedule;
                var tracked = new QuestCompletionRepositoryTests.LostResultRepository(repository)
                    { LoseLifecycleResult = !completion, LoseCompletionResult = completion };
                var session = new GameSession(tracked, towns, owner, serverEvents: events);
                session.Select(created.Slot); session.MarkReady();
                QuestLifecycleReceipt? Accept() => session.ChangeQuest(8698, QuestLifecycleAction.Accept, quests,
                    items.Items, items.GoldLimits, items.EpicPieces);
                QuestFinishResult Finish() => session.FinishQuest(8698, ushort.MaxValue, quests, parameters,
                    items.Items, items.GoldLimits, items.EpicPieces, growth);
                if (completion) That(Accept()?.Commit is { Duplicate: false }, "34丢结果前由真实31接取8698");
                var previous = session.Selected!;
                if (completion) Reject<IOException>(() => Finish(), "真实34提交后丢失结果，保留原pending");
                else Reject<IOException>(() => Accept(), "真实31提交后丢失结果，保留原pending");
                var saved = repository.LoadCharacter(owner, created.Slot);
                That(ReferenceEquals(previous, session.Selected) && saved.Revision == previous.Revision + 1 &&
                    (completion ? saved.CompletedQuests.Contains(8698) && saved.Inventory.Gold == previous.Inventory.Gold + 1 :
                        saved.ActiveMainQuests is [{ QuestId: 8698, AcceptanceId: var id }] && id != Guid.Empty),
                    "丢结果场景确为SQL已提交而在线仍保留旧快照");
                events.Override(312, false);
                if (reopen)
                    That(events.Override(312, true).Schedule.Revision > originalSchedule.Revision, "关→开实际改变活动revision");
                string state = reopen ? "活动关→开revision变化" : "活动关闭";
                if (completion)
                {
                    var recovered = Finish();
                    That(recovered.Receipt?.Commit is { Duplicate: true, ProjectionCurrent: true } &&
                        tracked.SamePlan && tracked.Calls == 2 && session.CanPublishQuestCompletion(recovered.Receipt),
                        state + "后同一34必须恢复原已提交回执及冻结计划");
                    That(QuestCompletionRepositoryTests.Same(saved, session.Selected!) &&
                        QuestCompletionRepositoryTests.Same(saved, repository.LoadCharacter(owner, created.Slot)),
                        "恢复34只安装原提交快照，不增加金币、经验、物品或完成次数");
                }
                else
                {
                    var recovered = Accept();
                    That(recovered?.Commit is { Duplicate: true, ProjectionCurrent: true } && tracked.SameLifecyclePlan &&
                        session.CanPublishQuestLifecycle(recovered), state + "后同一31必须恢复原已提交回执及operation");
                    That(QuestCompletionRepositoryTests.Same(saved, session.Selected!) &&
                        QuestCompletionRepositoryTests.Same(saved, repository.LoadCharacter(owner, created.Slot)),
                        "恢复31只安装原接取，不生成新acceptance、资产或revision");
                    That(session.ChangeQuest(8698, QuestLifecycleAction.Giveup, quests, items.Items,
                        items.GoldLimits, items.EpicPieces)?.Commit is { Duplicate: false },
                        "恢复31清除pending后允许正常放弃，不永久锁住其他任务请求");
                }
                session.End();
            }
            finally { TestFiles.DeleteDirectory(directory, true); }
        }
        Console.WriteLine($"PASS {Count - before} 首期8698{(completion ? 34 : 31)}丢结果后的活动变更恢复");
    }

    private static StoryQuest Read(PvfArchive pvf) => QuestDefinitionReader.Read(QuestStrongway2019Entry.Id,
        QuestStrongway2019Entry.ResourcePath, pvf.ReadTokens(QuestStrongway2019Entry.ResourcePath), pvf.ResolveString);
}
