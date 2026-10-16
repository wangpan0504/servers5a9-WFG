using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestDungeonClearProgressTests
{
    public static void RunCore()
    {
        int checks = Count;
        var quest = QuestEligibilityTests.Quest(100) with { Condition = "[condition under clear]", SubType = 6, IntData = [7322,-1,3] };
        That(QuestDungeonClearProgress.Initial(quest) == 3 && QuestDungeonClearProgress.Initial(quest with { IntData = [7322,-1] }) == 1,
            "条件通关次数来自第三声明，两字段默认为一次");
        foreach (var values in new int[][] { [-3,-1,3], [0,-1], [7322,-2], [7322,-1,0], [7322,-1,3,7] })
            That(!QuestDungeonClearProgress.TryTarget(quest with { IntData = values },out _),"未知selector、难度、次数或额外字段不能变成普通通关");
        That(!QuestDungeonClearProgress.TryTarget(quest with { SubType = 4 },out _) &&
            !QuestDungeonClearProgress.TryTarget(quest with { Condition = "[clear a dungeon]" },out _),"不把其他子型或类型45套进普通类型1");
        That(QuestDungeonClearProgress.ValidScalar(quest,0) && QuestDungeonClearProgress.ValidScalar(quest,3) &&
            !QuestDungeonClearProgress.ValidScalar(quest,4),"剩余次数保留完整声明边界，不能超出接取初值");
        var map = new DungeonMap("boss",new(0,0),76126,"map/clear-condition-fixture.map");
        var maze = new DungeonMaze(0,1,1,"fixture-only",[map.Position],[map.Position],[map],[]);
        var entry = new DungeonEntryPlan(1,0,7322,0,byte.MaxValue,0,maze,map,map);
        That(QuestDungeonClearProgress.Matches(quest,entry) && !QuestDungeonClearProgress.Matches(quest,entry with { DungeonId = 3 }),
            "目标核对实际地下城而非相似地图");
        var minimum = quest with { IntData = [7322,1] };
        That(!QuestDungeonClearProgress.Matches(minimum,entry) && QuestDungeonClearProgress.Matches(minimum,entry with { Difficulty = 1 }) &&
            QuestDungeonClearProgress.Matches(minimum,entry with { Difficulty = 2 }),"最低难度边界包含，较高合法难度同样满足");
        var alternatives = quest with { Condition = "[clear a dungeon]", IntData = [7165,2,7322,1,5115,-1] };
        That(QuestDungeonClearProgress.Supports(alternatives) && QuestDungeonClearProgress.Initial(alternatives) == 1 &&
            QuestDungeonClearProgress.ValidScalar(alternatives,0) && !QuestDungeonClearProgress.ValidScalar(alternatives,2),
            "原生45保持二值scalar，不把成对列表或难度解释成次数");
        foreach (var values in new int[][] { [], [7322], [7322,-1,5115], [7322,-1,0,-1], [7322,-1,5115,-2], [7322,5] })
            That(!QuestDungeonClearProgress.Supports(alternatives with { IntData = values }),"整表验证拒绝奇数或后续非法pair，不能只核首项");
        That(!QuestDungeonClearProgress.Supports(alternatives with { SubType = 11 }) &&
            !QuestDungeonClearProgress.Matches(alternatives,entry) &&
            QuestDungeonClearProgress.Matches(alternatives,entry with { Difficulty = 1 }) &&
            QuestDungeonClearProgress.Matches(alternatives,entry with { DungeonId = 5115 }) &&
            !QuestDungeonClearProgress.Matches(alternatives,entry with { DungeonId = 7165, Difficulty = 1 }) &&
            QuestDungeonClearProgress.Matches(alternatives,entry with { DungeonId = 7165, Difficulty = 2 }),
            "任一pair匹配即可，分别核最低难度；原生45不继承类型1的初始化子型");
        That(QuestDungeonClearProgress.ContainsDungeon(alternatives,7322) && !QuestDungeonClearProgress.ContainsDungeon(alternatives,3),
            "领奖凭据允许列表后续目标，拒绝列表外地下城");
        Console.WriteLine($"PASS {Count-checks} 本版条件通关目标与次数断言");
    }

    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(),"S5A9-quest-dungeon-clear-"+Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root,"game.db");
        var skills = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var counted = QuestEligibilityTests.Quest(100) with { Condition = "[condition under clear]", SubType = 6,
            IntData = [3,-1,3], CompleteNpc = -1, Attributes = ["not give exp quest"], Rewards = [] };
        var singleMap = counted with { Id = 101, Condition = "[clear map]", SubType = -1, IntData = [76126] };
        var quests = new QuestCatalog(new[] { counted,singleMap }.ToDictionary(row => row.Id));
        var owners = new AccountOwners();
        using var owner = owners.Acquire("quest-dungeon-clear",() => { });
        try
        {
            var repository = new CharacterRepository(path,quests);
            var current = repository.Create(owner,SkillRepositoryTests.Fixture("ClearCount",towns,characters,skills));
            Accept(counted); Accept(singleMap);
            That(State() == 3,"统一接取事务保存三次清图初值，不依赖固定任务号");
            var first = Prepare(current);
            Sql("CREATE TRIGGER fail_clear_counter AFTER UPDATE ON character_active_main_quests WHEN NEW.quest_id=100 BEGIN SELECT RAISE(ABORT,'clear counter rollback'); END");
            var before = current;
            Reject<SqliteException>(() => Commit(first,before),"通关减数末段SQL失败");
            That(QuestCompletionRepositoryTests.Same(before,repository.LoadCharacter(owner,current.Slot)) &&
                Scalar("SELECT count(*) FROM character_dungeon_growth_receipts") == 0 &&
                Scalar("SELECT count(*) FROM character_quest_clear_receipts") == 0,"经验、两树、减数及两个清图目标同事务回滚");
            Sql("DROP TRIGGER fail_clear_counter");
            var committed = Commit(first,before);
            var replay = Commit(first,before);
            current = committed.Character;
            That(State() == 2 && replay.Duplicate && QuestCompletionRepositoryTests.Same(current,replay.Character) &&
                Scalar("SELECT count(*) FROM character_quest_clear_receipts") == 1 &&
                repository.LoadQuestClear(owner,current,100) is null,"精确重放不二次减数；并行单地图目标达成，计数目标尚无领奖凭据");
            first.Run.ConfirmCommittedDeath(first.Pending);
            var reopened = new CharacterRepository(path,quests).LoadCharacter(owner,current.Slot);
            That(QuestCompletionRepositoryTests.Same(current,reopened) && State() == 2,"重开仓储保持接取身份及中间次数");
            var sync = new GameSession(repository,towns,owner); sync.Select(current.Slot); sync.MarkReady();
            var result = sync.ChangeQuest(100,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0));
            That(result is { Outcome: QuestLifecycleOutcome.Unchanged } && result.Character.ActiveMainQuests.Single(row => row.QuestId == 100).ConditionState == 2 &&
                State() == 2,"客户端33的value0只读取权威剩余2次，不赋零或补通关");
            var second = Prepare(current);
            current = Commit(second,current).Character; second.Run.ConfirmCommittedDeath(second.Pending);
            That(State() == 1,"另一真实实例再次通关，只减少一次");
            var oldRun = Prepare(current);
            Guid oldAcceptance = current.ActiveMainQuests.Single(row => row.QuestId == 100).AcceptanceId;
            current = repository.CommitQuestLifecycle(owner,QuestLifecyclePlan.Giveup(current,counted,quests,DateTimeOffset.UtcNow)).Character;
            Accept(counted);
            Guid newAcceptance = current.ActiveMainQuests.Single(row => row.QuestId == 100).AcceptanceId;
            current = Commit(oldRun,current).Character; oldRun.Run.ConfirmCommittedDeath(oldRun.Pending);
            That(oldAcceptance != newAcceptance && State() == 3 && repository.LoadQuestClear(owner,current,100) is null,
                "放弃重接重置次数及身份；原已绑定实例的通关不能推进新接取");
            for (uint remaining = 2; ; remaining--)
            {
                var clear = Prepare(current); before = current;
                current = Commit(clear,before).Character;
                var duplicate = Commit(clear,before);
                That(State() == remaining && duplicate.Duplicate && QuestCompletionRepositoryTests.Same(current,duplicate.Character),
                    "每次真实通关及返回丢失恢复都只减数一次");
                clear.Run.ConfirmCommittedDeath(clear.Pending);
                if (remaining == 0) break;
            }
            var receipt = repository.LoadQuestClear(owner,current,100);
            That(receipt is not null && receipt.AcceptanceId == newAcceptance && receipt.DungeonId == 3 && receipt.MapId == 76126 &&
                Scalar("SELECT count(*) FROM character_quest_clear_receipts") == 2 && !current.CompletedQuests.Contains(100),
                "最终次数归零持久最后真实通关凭据，仍未发奖或写完成位");
            var finalSession = new GameSession(repository,towns,owner); finalSession.Select(current.Slot); finalSession.MarkReady();
            var claim = finalSession.FinishQuest(100,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(claim.Receipt?.Commit is { ConditionType: 1, ExperienceReward.Amount: 0, Rewards.Length: 0 } finished &&
                finished.Character.CompletedQuests.Contains(100) &&
                Scalar("SELECT count(*) FROM character_quest_completion_receipts WHERE condition_type=1") == 1,
                "本版类型1沿统一凭据、零奖励和完成事务交付");
            That(finalSession.FinishQuest(100,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome ==
                QuestFinishOutcome.AlreadyCompleted,"类型1重复交付不能重写奖励及完成记录");

            void Accept(StoryQuest quest) => current = repository.CommitQuestLifecycle(owner,
                QuestLifecyclePlan.Accept(current,quest,quests,items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow)).Character;
            uint State() => current.ActiveMainQuests.Single(row => row.QuestId == 100).ConditionState;
            DungeonGrowthCommit Commit((SinglePlayerDungeonRun Run,PreparedDungeonDeath Pending) clear,Character expected) =>
                repository.CommitDungeonGrowth(owner,expected,clear.Run,clear.Pending,new("fixture-only",DungeonKillExperienceCatalog.ResourcePath,
                    clear.Pending.Monster.MonsterId,clear.Pending.Monster.CandidateLevel,1),growth);
            void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            SqliteConnection Open() { var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open(); return db; }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
        Console.WriteLine($"PASS {Count-checks} 条件通关计数、原子回滚、重放、重接及领奖断言（合成战斗）");
        RunAlternatives(towns,pvf,characters,items);
        RunResourceAccept(towns,pvf,characters,items);
    }

    static void RunResourceAccept(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(),"S5A9-quest-list-resources-"+Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root,"game.db");
        var quests = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf);
        var skills = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf,skills);
        using var owner = new AccountOwners().Acquire("quest-list-resources",() => { });
        try
        {
            var repository = new CharacterRepository(path,quests);
            foreach (ushort id in new ushort[] { 1680,1681,1682,2929,6400,12871 })
            {
                var quest = quests.Get(id);
                That(quest.Condition == "[clear a dungeon]" && quest.SubType == 6 && QuestCoverage.HasSupportedTarget(quest,quests) &&
                    QuestCoverage.HasOrdinaryDeclarations(quest),"真实外传声明沿列表通关目标，不用任务号放行："+id);
                var current = repository.Create(owner,SkillRepositoryTests.Fixture("List"+id,towns,characters,skills) with
                {
                    Level = checked((byte)quest.MinimumLevel),
                    CompletedQuests = quest.Definition!.PrerequisiteGroups.Take(1).SelectMany(group => group).ToArray()
                });
                That(QuestEligibility.Evaluate(current,quest,quests).Eligible &&
                    !QuestEligibility.Evaluate(current with { Level = checked((byte)(quest.MinimumLevel-1)) },quest,quests).Eligible,
                    "真实前置准备后可接，仍保留原最低等级："+id);
                var session = new GameSession(repository,towns,owner); session.Select(current.Slot); session.MarkReady();
                var accepted = session.ChangeQuest(id,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces);
                That(accepted is { Outcome: QuestLifecycleOutcome.Committed } &&
                    accepted.Character.ActiveMainQuests.Single() is { ConditionState: 1, AcceptanceId: var acceptance } && acceptance != Guid.Empty,
                    "真实外传通过统一Session/SQLite接取，不预先完成："+id);
                That(session.ChangeQuest(id,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0)) is
                    { Outcome: QuestLifecycleOutcome.Unchanged } && session.FinishQuest(id,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome == QuestFinishOutcome.Rejected &&
                    repository.LoadQuestClear(owner,accepted!.Character,id) is null,
                    "真实资源33不能伪造塔或其它模式清图，提前34拒绝："+id);
                var restored = new CharacterRepository(path,quests).LoadCharacter(owner,current.Slot);
                That(QuestCompletionRepositoryTests.Same(accepted!.Character,restored),"重开保存原接取及实际赠品资产："+id);
            }
            foreach (int id in new[] { 6401,6402,6403 })
                That(quests.Get(id).Condition == "[custom multi condition]" && !QuestDungeonClearProgress.Supports(quests.Get(id)),
                    "炫目后续多条件任务不能冒充type45列表通关："+id);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
        Console.WriteLine($"PASS {Count-checks} 真实外传资源接取与未清图拒绝断言；不证明专用塔准入或客户端通关");
    }

    static void RunAlternatives(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int checks = Count;
        const ushort questId = 1103;
        string root = Path.Combine(Path.GetTempPath(),"S5A9-quest-dungeon-list-"+Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root,"game.db");
        var skills = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf,skills);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var quest = QuestEligibilityTests.Quest(questId) with { Condition = "[clear a dungeon]", SubType = 6,
            IntData = [4,2,3,1], CompleteNpc = -1, Attributes = ["not give exp quest"],
            Rewards = [new(0,3311,null),new(0,10,null)] };
        var quests = new QuestCatalog(new Dictionary<int,StoryQuest> { [questId] = quest });
        using var owner = new AccountOwners().Acquire("quest-dungeon-list",() => { });
        try
        {
            var repository = new CharacterRepository(path,quests);
            var current = repository.Create(owner,SkillRepositoryTests.Fixture("ClearList",towns,characters,skills));
            Accept();
            That(State() == 1 && repository.LoadQuestClear(owner,current,questId) is null,"列表目标接取保存独立身份和pending1，不补历史清图");
            var session = new GameSession(repository,towns,owner); session.Select(current.Slot); session.MarkReady();
            That(session.ChangeQuest(questId,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0)) is
                { Outcome: QuestLifecycleOutcome.Unchanged } && State() == 1 &&
                session.FinishQuest(questId,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome == QuestFinishOutcome.Rejected,
                "客户端33/value0不能完成列表目标，提前34仍拒绝且不发奖励");
            foreach (var target in new[] { (Dungeon: 5u,Difficulty: (byte)4),(Dungeon: 3u,Difficulty: (byte)0) })
            {
                var wrong = Prepare(current,target.Dungeon,target.Difficulty);
                current = Commit(wrong,current).Character; wrong.Run.ConfirmCommittedDeath(wrong.Pending);
                That(State() == 1 && repository.LoadQuestClear(owner,current,questId) is null,"列表外或最低难度不足的真实清图不推进该任务");
            }
            var stale = Prepare(current,3,1);
            Guid original = current.ActiveMainQuests.Single().AcceptanceId;
            current = repository.CommitQuestLifecycle(owner,QuestLifecyclePlan.Giveup(current,quest,quests,DateTimeOffset.UtcNow)).Character;
            Accept();
            current = Commit(stale,current).Character; stale.Run.ConfirmCommittedDeath(stale.Pending);
            That(State() == 1 && current.ActiveMainQuests.Single().AcceptanceId != original &&
                repository.LoadQuestClear(owner,current,questId) is null,"旧实例的匹配通关不能完成重新接取的列表目标");
            var clear = Prepare(current,3,1);
            var before = current;
            Sql("CREATE TRIGGER fail_list_clear AFTER UPDATE ON character_active_main_quests BEGIN SELECT RAISE(ABORT,'list clear rollback'); END");
            Reject<SqliteException>(() => Commit(clear,before),"列表归零末段SQL失败");
            That(QuestCompletionRepositoryTests.Same(before,repository.LoadCharacter(owner,current.Slot)) &&
                Scalar("SELECT count(*) FROM character_quest_clear_receipts") == 0,"真实死亡、归零和领奖来源一起回滚");
            Sql("DROP TRIGGER fail_list_clear");
            current = Commit(clear,before).Character;
            var duplicate = Commit(clear,before); clear.Run.ConfirmCommittedDeath(clear.Pending);
            var receipt = repository.LoadQuestClear(owner,current,questId);
            That(State() == 0 && duplicate.Duplicate && receipt?.DungeonId == 3 && receipt.AcceptanceId == current.ActiveMainQuests.Single().AcceptanceId &&
                Scalar("SELECT count(*) FROM character_quest_clear_receipts") == 1,"后续pair一次匹配即归零，同事务持久原接取来源，重放不重复");
            repository = new CharacterRepository(path,quests);
            That(QuestCompletionRepositoryTests.Same(current,repository.LoadCharacter(owner,current.Slot)),"重开保留列表目标的原接取、scalar0和真实凭据");
            session = new GameSession(repository,towns,owner); session.Select(current.Slot); session.MarkReady();
            Sql("CREATE TRIGGER fail_list_finish AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'list finish rollback'); END");
            before = current;
            Reject<SqliteException>(() => Finish(),"类型45领奖末段SQL失败");
            That(QuestCompletionRepositoryTests.Same(before,repository.LoadCharacter(owner,current.Slot)) &&
                Scalar("SELECT count(*) FROM character_quest_completion_receipts") == 0,"原生45奖励、完成位、接取清除及回执整笔回滚");
            Sql("DROP TRIGGER fail_list_finish");
            var finished = Finish().Receipt!.Commit;
            current = finished.Character;
            That(finished.ConditionType == 45 && finished.Rewards is [{ GrantedCount: 10, Item.Uid: > 0, Item.Core.ItemId: 3311 }] &&
                current.CompletedQuests.Contains(questId) && current.ActiveMainQuests.Length == 0 &&
                Scalar("SELECT count(*) FROM character_quest_completion_receipts WHERE condition_type=45 AND clear_acceptance_id=acceptance_id AND event_id IS NOT NULL") == 1 &&
                Finish().Outcome == QuestFinishOutcome.AlreadyCompleted,"原生45沿真实清图来源和统一资产事务发完整奖励，只交付一次");
            foreach (string assignment in new[] { "clear_acceptance_id=NULL", "plan_snapshot=NULL", "event_id=NULL", "instance_id=NULL", "task_attempt=NULL", "epoch=NULL",
                "clear_acceptance_id='ffffffffffffffffffffffffffffffff'" })
                Reject<SqliteException>(() => Sql("UPDATE character_quest_completion_receipts SET "+assignment),"原生45数据库拒绝缺失或替换真实凭据/冻结计划");

            void Accept() => current = repository.CommitQuestLifecycle(owner,QuestLifecyclePlan.Accept(current,quest,quests,items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow)).Character;
            uint State() => current.ActiveMainQuests.Single().ConditionState;
            DungeonGrowthCommit Commit((SinglePlayerDungeonRun Run,PreparedDungeonDeath Pending) candidate,Character expected) =>
                repository.CommitDungeonGrowth(owner,expected,candidate.Run,candidate.Pending,new("fixture-only",DungeonKillExperienceCatalog.ResourcePath,
                    candidate.Pending.Monster.MonsterId,candidate.Pending.Monster.CandidateLevel,1),growth);
            QuestFinishResult Finish() => session.FinishQuest(questId,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            SqliteConnection Open() { var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open(); return db; }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
        Console.WriteLine($"PASS {Count-checks} 列表通关45原接取、OR难度、清图/领奖回滚、重放及重开断言（合成战斗）");
    }

    public static void RunInitialization(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int checks = Count;
        var threshold = QuestEligibilityTests.Quest(1100) with { Condition = "[condition under clear]", SubType = 11,
            IntData = [3,-1,2], CompleteNpc = -1, Attributes = ["not give exp quest"], Rewards = [] };
        var mazeTarget = threshold with { Id = 1101, IntData = [3,-1,-1] };
        var eight = threshold with { Id = 1102, IntData = [3,-1,8] };
        That(QuestDungeonClearProgress.Initial(eight) == 1 && QuestDungeonClearProgress.ValidScalar(eight,0) &&
            QuestDungeonClearProgress.ValidScalar(eight,1) && !QuestDungeonClearProgress.ValidScalar(eight,2),
            "sub11 keeps a boolean scalar instead of initialization count or remaining eight");
        foreach (var data in new int[][] { [3,-1], [3,-1,0], [3,-1,-2], [3,-1,8,1], [-1,-1,8] })
            That(!QuestDungeonClearProgress.TryTarget(eight with { IntData = data },out _),
                "unknown initialization threshold or selector does not become an ordinary supported target");
        string root = Path.Combine(Path.GetTempPath(),"S5A9-quest-initialization-"+Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root,"game.db");
        var quests = new QuestCatalog(new[] { threshold,mazeTarget,eight }.ToDictionary(row => row.Id));
        var skills = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var owners = new AccountOwners();
        using var owner = owners.Acquire("quest-initialization",() => { });
        try
        {
            var repository = new CharacterRepository(path,quests);
            var current = repository.Create(owner,SkillRepositoryTests.Fixture("Initialize",towns,characters,skills));
            Accept(threshold); Accept(mazeTarget); Accept(eight);
            var first = PrepareInitialization(current,2);
            That(first.Run.ConfirmedInitializationCount == 2 && first.Run.MazeInitializationThreshold == 2 &&
                first.Run.ClearFact is null && State(1100) == 1 && State(1101) == 1 && State(1102) == 1,
                $"loading facts remain in the run and never update persistent quest progress before a real clear; count={first.Run.ConfirmedInitializationCount}, threshold={first.Run.MazeInitializationThreshold}, clear={first.Run.ClearFact is not null}, states={State(1100)}/{State(1101)}/{State(1102)}");
            That(QuestDungeonClearProgress.CountsInitializations(eight,first.Run.Entry) &&
                QuestDungeonClearProgress.Matches(threshold,first.Run.Entry,first.Run) &&
                QuestDungeonClearProgress.Matches(mazeTarget,first.Run.Entry,first.Run) &&
                !QuestDungeonClearProgress.Matches(eight,first.Run.Entry,first.Run) &&
                !QuestDungeonClearProgress.Matches(threshold,first.Run.Entry) &&
                !QuestDungeonClearProgress.Matches(threshold with { IntData = [3,-1,3] },first.Run.Entry,first.Run),
                "the same real count supports an exact positive boundary and maze N-minus-one but not a higher threshold or missing run");
            var difficult = threshold with { IntData = [3,1,2] };
            That(!QuestDungeonClearProgress.CountsInitializations(difficult,first.Run.Entry) &&
                QuestDungeonClearProgress.CountsInitializations(difficult,first.Run.Entry with { Difficulty = 1 }) &&
                QuestDungeonClearProgress.CountsInitializations(difficult,first.Run.Entry with { Difficulty = 2 }) &&
                !QuestDungeonClearProgress.CountsInitializations(threshold,first.Run.Entry with { DungeonId = 4 }),
                "initialization qualification uses minimum difficulty and the actual dungeon");
            Sql("CREATE TRIGGER fail_initialization AFTER UPDATE ON character_active_main_quests WHEN NEW.quest_id=1100 BEGIN SELECT RAISE(ABORT,'initialization rollback'); END");
            var before = current;
            Reject<SqliteException>(() => Commit(first,before),"sub11 final clear SQL failure");
            That(QuestCompletionRepositoryTests.Same(before,repository.LoadCharacter(owner,current.Slot)) &&
                Scalar("SELECT count(*) FROM character_dungeon_growth_receipts") == 0 &&
                Scalar("SELECT count(*) FROM character_quest_clear_receipts") == 0 && first.Run.ClearFact is null &&
                first.Run.IsPendingDeath(first.Pending) && first.Run.ConfirmedInitializationCount == 2,
                "failed clear rolls back growth, both qualifying boolean progress states and receipts while retaining the same real run candidate");
            Sql("DROP TRIGGER fail_initialization");
            current = Commit(first,before).Character;
            var replay = Commit(first,before);
            That(State(1100) == 0 && State(1101) == 0 && State(1102) == 1 && replay.Duplicate &&
                QuestCompletionRepositoryTests.Same(current,replay.Character) &&
                Scalar("SELECT count(*) FROM character_quest_clear_receipts") == 2,
                "only qualifying sub11 targets become zero atomically and return-loss recovery does not add another receipt");
            first.Run.ConfirmCommittedDeath(first.Pending);
            var reopened = new CharacterRepository(path,quests).LoadCharacter(owner,current.Slot);
            That(QuestCompletionRepositoryTests.Same(current,reopened) && repository.LoadQuestClear(owner,current,1100) is not null &&
                repository.LoadQuestClear(owner,current,1101) is not null && repository.LoadQuestClear(owner,current,1102) is null,
                "reopening preserves the real final clear receipt without persisting transient loading counters");
            var sync = new GameSession(repository,towns,owner); sync.Select(current.Slot); sync.MarkReady();
            var condition = sync.ChangeQuest(1102,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0));
            That(condition?.Outcome == QuestLifecycleOutcome.Unchanged && State(1102) == 1,
                "client scalar zero cannot replace the missing eight-initialization clear fact");
            var second = PrepareInitialization(current,2);
            current = Commit(second,current).Character; second.Run.ConfirmCommittedDeath(second.Pending);
            That(State(1102) == 1 && second.Run.ConfirmedInitializationCount == 2 &&
                Scalar("SELECT count(*) FROM character_quest_clear_receipts") == 2,
                "another run starts its own shared counter and two partial clears do not accumulate toward eight");
            var oldRun = PrepareInitialization(current,8);
            Guid oldAcceptance = current.ActiveMainQuests.Single(row => row.QuestId == 1102).AcceptanceId;
            current = repository.CommitQuestLifecycle(owner,QuestLifecyclePlan.Giveup(current,eight,quests,DateTimeOffset.UtcNow)).Character;
            Accept(eight);
            Guid newAcceptance = current.ActiveMainQuests.Single(row => row.QuestId == 1102).AcceptanceId;
            current = Commit(oldRun,current).Character; oldRun.Run.ConfirmCommittedDeath(oldRun.Pending);
            That(oldAcceptance != newAcceptance && State(1102) == 1 && repository.LoadQuestClear(owner,current,1102) is null &&
                Scalar("SELECT count(*) FROM character_quest_clear_receipts") == 2,
                "an old run reaching eight cannot advance or issue a clear receipt for a renewed acceptance");
            var final = PrepareInitialization(current,8); before = current;
            That(QuestDungeonClearProgress.Matches(eight,final.Run.Entry,final.Run) &&
                !QuestDungeonClearProgress.Matches(eight with { IntData = [3,-1,9] },final.Run.Entry,final.Run),
                "positive eight is inclusive and does not satisfy nine");
            current = Commit(final,before).Character;
            var finalReplay = Commit(final,before); final.Run.ConfirmCommittedDeath(final.Pending);
            var receipt = repository.LoadQuestClear(owner,current,1102);
            That(State(1102) == 0 && finalReplay.Duplicate && receipt is not null && receipt.AcceptanceId == newAcceptance &&
                receipt.InstanceId == final.Run.Snapshot.Identity.InstanceId && receipt.EventId == final.Pending.Death.EventId &&
                receipt.MapId == 76126 && Scalar("SELECT count(*) FROM character_quest_clear_receipts") == 3 &&
                !current.CompletedQuests.Contains(1102),
                "exact eight creates the current acceptance's final real-death receipt only once and has not awarded or finished the quest");
            var finishing = new GameSession(repository,towns,owner); finishing.Select(current.Slot); finishing.MarkReady();
            var claim = finishing.FinishQuest(1102,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(claim.Receipt?.Commit is { ConditionType: 1, ExperienceReward.Amount: 0, Rewards.Length: 0 } done &&
                done.Character.CompletedQuests.Contains(1102) &&
                finishing.FinishQuest(1102,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome == QuestFinishOutcome.AlreadyCompleted,
                "sub11 uses the existing zero-reward completion transaction and rejects duplicate handover");

            void Accept(StoryQuest quest) => current = repository.CommitQuestLifecycle(owner,
                QuestLifecyclePlan.Accept(current,quest,quests,items.Items,items.GoldLimits,items.EpicPieces,DateTimeOffset.UtcNow)).Character;
            uint State(ushort questId) => current.ActiveMainQuests.Single(row => row.QuestId == questId).ConditionState;
            DungeonGrowthCommit Commit((SinglePlayerDungeonRun Run,PreparedDungeonDeath Pending) clear,Character expected) =>
                repository.CommitDungeonGrowth(owner,expected,clear.Run,clear.Pending,new("fixture-only",DungeonKillExperienceCatalog.ResourcePath,
                    clear.Pending.Monster.MonsterId,clear.Pending.Monster.CandidateLevel,1),growth);
            void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            SqliteConnection Open() { var db = new SqliteConnection("Data Source="+path+";Pooling=False"); db.Open(); return db; }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
        Console.WriteLine($"PASS {Count-checks} sub11 initialization threshold, real-clear rollback, replay and renewed-acceptance assertions");
    }

    static (SinglePlayerDungeonRun Run,PreparedDungeonDeath Pending) PrepareInitialization(Character character,int count)
    {
        var start = new DungeonMap("map",new(0,0),76124,"map/initialization-start-fixture.map");
        var middle = new DungeonMap("map",new(1,0),76125,"map/initialization-middle-fixture.map");
        var boss = new DungeonMap("boss",new(2,0),76126,"map/initialization-boss-fixture.map");
        var maze = new DungeonMaze(0,3,1,"BBFFEE",[start.Position],[boss.Position],[start,middle,boss],[]);
        var entry = new DungeonEntryPlan(character.Id,character.Revision,3,0,byte.MaxValue,0,maze,start,boss)
        { QuestAcceptances = character.ActiveMainQuests.ToImmutableArray(), InitializationCounterSupported = true, PlayerTeam = 0 };
        var source = new DungeonRoomMonster(1,[1,0,3,100,100,0,1,1],"[fixed]","[normal]","monster/initialization-fixture.mob");
        DungeonMonsterPlan Room(DungeonMap map,ushort key,bool friendly) => DungeonMonsterPlan.FromRoom(
            new DungeonRoomDefinition(map,[source with { RankKind = friendly ? "[normal]" : "[boss]" }],[],[])
                { MonsterTeams = friendly ? [0] : [] },character.Id,key,playerTeam: 0);
        var bossPlan = Room(boss,22,false);
        var route = new DungeonFixedRoute([Room(start,20,true),Room(middle,21,true),bossPlan],
            [new(start.Position,middle.Position),new(middle.Position,boss.Position)]);
        var authority = new FixtureAuthority();
        if (count is not (2 or 8) || !SinglePlayerDungeonRun.TryCreate(entry,1,1,route,authority,out var created,out _))
            throw new InvalidOperationException("初始化计数fixture只能生成两次或八次实际加载");
        var run = created!;
        That(run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid(),true).Outcome == DungeonRunOutcome.Accepted,
            "初始化计数夹具必须完成实际首房加载");
        for (int index = 1; index < count; index++)
        {
            That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),run.Snapshot.CurrentMap == start ? middle.Position : start.Position).Outcome == DungeonRunOutcome.Accepted &&
                run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid(),true).Outcome == DungeonRunOutcome.Accepted,
                "初始化计数夹具每次移动与后续加载均须被正式Run接受");
        }
        That(run.Move(run.Snapshot.Identity,Guid.NewGuid(),boss.Position).Outcome == DungeonRunOutcome.Accepted &&
            run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid(),true).Outcome == DungeonRunOutcome.Accepted,
            "初始化计数夹具须实际到达并加载原Boss房");
        return (run,run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,bossPlan.Monsters.Single().ObjectKey)).Pending!);
    }

    static (SinglePlayerDungeonRun Run,PreparedDungeonDeath Pending) Prepare(Character character,uint dungeonId = 3,byte difficulty = 0)
    {
        var map = new DungeonMap("boss",new(0,0),76126,"map/clear-condition-fixture.map");
        var maze = new DungeonMaze(0,1,1,"fixture-only",[map.Position],[map.Position],[map],[]);
        var entry = new DungeonEntryPlan(character.Id,character.Revision,dungeonId,0,byte.MaxValue,difficulty,maze,map,map)
        { QuestAcceptances = character.ActiveMainQuests.ToImmutableArray() };
        var source = new DungeonRoomMonster(1,[1,0,3,100,100,0,1,1],"[fixed]","[boss]","monster/clear-condition-fixture.mob");
        var room = new DungeonRoomDefinition(map,[source],[],[]);
        var monsters = DungeonMonsterPlan.FromRoom(room,character.Id,20);
        var authority = new FixtureAuthority();
        if (!SinglePlayerDungeonRun.TryCreate(entry,1,1,new([monsters],[]),authority,out var run,out var reason))
            throw new InvalidOperationException("条件通关fixture实例不能建立: "+reason);
        run!.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid());
        return (run,run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,monsters.Monsters.Single().ObjectKey)).Pending!);
    }

    // 仅供隔离仓储测试，不能代替实际客户端战斗或通关验收。
    sealed class FixtureAuthority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity,ushort key) => ConfirmDeath(identity,Guid.NewGuid(),key,1);
    }
}
