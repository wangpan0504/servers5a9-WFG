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
                "loading facts remain in the run and never update persistent quest progress before a real clear");
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
        { QuestAcceptances = character.ActiveMainQuests.ToImmutableArray(), InitializationCounterSupported = true };
        var source = new DungeonRoomMonster(1,[1,0,3,100,100,0,1,1],"[fixed]","[normal]","monster/initialization-fixture.mob");
        DungeonMonsterPlan Room(DungeonMap map,ushort key,bool friendly) => DungeonMonsterPlan.FromRoom(
            new DungeonRoomDefinition(map,[source with { RankKind = friendly ? "[normal]" : "[boss]" }],[],[])
                { MonsterTeams = friendly ? [0] : [] },character.Id,key);
        var bossPlan = Room(boss,22,false);
        var route = new DungeonFixedRoute([Room(start,20,true),Room(middle,21,true),bossPlan],
            [new(start.Position,middle.Position),new(middle.Position,boss.Position)]);
        var authority = new FixtureAuthority();
        if (count is not (2 or 8) || !SinglePlayerDungeonRun.TryCreate(entry,1,1,route,authority,out var created,out _))
            throw new InvalidOperationException("初始化计数fixture只能生成两次或八次实际加载");
        var run = created!;
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid(),true);
        for (int index = 1; index < count; index++)
        {
            run.Move(run.Snapshot.Identity,Guid.NewGuid(),run.Snapshot.CurrentMap == start ? middle.Position : start.Position);
            run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid(),true);
        }
        run.Move(run.Snapshot.Identity,Guid.NewGuid(),boss.Position);
        run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid(),true);
        return (run,run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity,bossPlan.Monsters.Single().ObjectKey)).Pending!);
    }

    static (SinglePlayerDungeonRun Run,PreparedDungeonDeath Pending) Prepare(Character character)
    {
        var map = new DungeonMap("boss",new(0,0),76126,"map/clear-condition-fixture.map");
        var maze = new DungeonMaze(0,1,1,"fixture-only",[map.Position],[map.Position],[map],[]);
        var entry = new DungeonEntryPlan(character.Id,character.Revision,3,0,byte.MaxValue,0,maze,map,map)
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
