using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestQuickClearRepositoryTests
{
    public static void RunCore()
    {
        int checks = Count;
        var question = QuestEligibilityTests.Quest(100) with { Condition = "[question]", Definition = new([
            new("question info",Enumerable.Range(0,5).Select(index => new QuestRewardToken(6,index,"text")).ToImmutableArray(),[])]) { Job = "[all]" } };
        var marker = QuestEligibilityTests.Quest(101) with { Grade = "[invisible]", IntData = [-1],
            Definition = new([new("synchronization account quest",[new(0,1,null)],[])]) };
        var first = QuestEligibilityTests.Quest(200) with { MainQuest = 300 };
        var parent = QuestEligibilityTests.Quest(300) with { Condition = "[quest clear]", SubType = 2, IntData = [200,301] };
        var child = QuestEligibilityTests.Quest(301) with { MainQuest = 300 };
        var state = QuestEligibilityTests.Quest(302) with { Condition = "[clear quest]", IntData = [202] };
        var definitions = new[] { question,marker,first,parent,child,state,QuestEligibilityTests.Quest(201),
            QuestEligibilityTests.Quest(202),QuestEligibilityTests.Quest(6381),QuestEligibilityTests.Quest(400) };
        var quests = new QuestCatalog(definitions.ToDictionary(row => row.Id));
        var connections = Connections(1007);
        var learning = new CharacterLearningState(123456,[new(0,50,5,[new(150,100,1) { Command = "0102" }]),new(1,70,7,[new(151,100,1)])])
            { BonusSp = 3, BonusTp = 4 };
        var candidate = new NewCharacter("QuickClear",new(38,0,450,250),new(38,1,300,250))
        {
            Job = 15, GrowType = 17, Level = 95, EquipmentSlotFlags = 3, AcceptedStoryLevel = 42,
            InitialLearning = learning, Skills = [new(100,1)], CompletedQuests = [201,900],
            DungeonState = new(700,4,123,2,987),
            Inventory = new([new(0,new(InventoryListType.Main,65),new(177,ItemKind.Consumable,7))],Gold:1234),
            ActiveMainQuests = [Active(0,100,QuestQuestionProgress.Unanswered),Active(1,200),Active(2,300,2),Active(3,302),Active(4,400)]
        };
        using (var f = new Fixture(quests,connections,1007,candidate))
        {
            f.Sql("UPDATE characters SET quest_cycle_day=20261009; UPDATE character_completed_quests SET answer=1,daily_period=20261008 WHERE quest_id=201; UPDATE character_completed_quests SET answer=2 WHERE quest_id=900; INSERT INTO account_completed_quests(account,quest_id,character_id,acceptance_id,daily_period,answer) VALUES('quick-clear',901,1,'1234567890abcdef1234567890abcdef',0,1)");
            var plan = f.Plan(); var expected = plan.Expected; string assets = Assets(expected);
            var result = f.Repository.CommitQuestQuickClear(f.Owner,plan);
            That(!result.Duplicate && result.ProjectionCurrent && result.Character.Revision == expected.Revision+1 && f.ReceiptCount == 1,
                "无奖励跳过任务、答案和回执只推进一次revision");
            That(result.Character.CompletedQuests.SequenceEqual([100,101,200,201,202,900,6381]) &&
                result.Character.QuestAnswers[100] == 2 && result.Character.QuestAnswers[201] == 1 && result.Character.QuestAnswers[900] == 2 &&
                result.Character.AccountCompletedQuests.SequenceEqual([101,901]) && result.Character.AccountQuestAnswers[901] == 1,
                "级联标记同步账号，旅程零基答案与旧账号答案保持独立");
            That(result.Character.ActiveMainQuests.Select(row => (int)row.QuestId).SequenceEqual([300,302,400]) &&
                result.Character.ActiveMainQuests.Single(row => row.QuestId == 300).ConditionState == 1 &&
                result.Character.ActiveMainQuests.Single(row => row.QuestId == 302).ConditionState == 0,
                "只移除范围活动槽，并同事务更新其他父条件和角色任务状态");
            That(Assets(result.Character) == assets && result.Character.Inventory.Items.Single().Uid > 0 &&
                f.Scalar("SELECT daily_period FROM character_completed_quests WHERE quest_id=201") == 20261008 &&
                f.Scalar("SELECT count(*) FROM character_completed_quests WHERE quest_id<>201 AND daily_period<>0") == 0,
                "物品UID/Core、金币、成长双树及角色状态未变，已有每日完成周期保留");
            var replay = f.Repository.CommitQuestQuickClear(f.Owner,plan);
            That(replay is { Duplicate:true, ProjectionCurrent:true } && f.ReceiptCount == 1 && Same(replay.Character,result.Character),
                "精确重复提交读取相同持久结果，不重写完成记录或资产");
            using (var writer = f.Open())
            using (var held = writer.BeginTransaction(deferred:false))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                That(f.Repository.CommitQuestQuickClear(f.Owner,plan).Duplicate && watch.Elapsed < TimeSpan.FromSeconds(1),
                    "WAL另一writer占用时重复回执恢复只读，不请求写锁");
                held.Rollback();
            }
            That(QuestQuickClearPlan.TryCreate(expected,1008,quests,Connections(1008),out var different,out _),"相同question可构造不同parent冻结规划供冲突核验");
            Reject<InvalidDataException>(() => f.Repository.CommitQuestQuickClear(f.Owner,different!),"相同接取身份但parent或Snapshot不同拒绝");
            That(QuestQuickClearPlan.TryCreate(expected with { Inventory = expected.Inventory with { Gold = expected.Inventory.Gold+1 } },
                1007,quests,connections,out var changedAssets,out _),"相同接取及revision但不同完整资产可构造冲突规划");
            Reject<InvalidDataException>(() => f.Repository.CommitQuestQuickClear(f.Owner,changedAssets!),"相同接取身份不能使用资产不同的Snapshot恢复，不降为过期投影");
            var reopened = new CharacterRepository(f.Path,quests);
            That(reopened.CommitQuestQuickClear(f.Owner,plan) is { Duplicate:true, ProjectionCurrent:true } &&
                Same(reopened.LoadCharacter(f.Owner,0),result.Character),"仓储重开恢复原回执、完成集与资产，不等同于独立进程重启");
            var moved = f.Repository.Save(f.Owner,result.Character,result.Character.Position with { X = 451 },result.Character.Return);
            var late = reopened.CommitQuestQuickClear(f.Owner,plan);
            That(late is { Duplicate:true, ProjectionCurrent:false } && Same(late.Character,moved) && f.ReceiptCount == 1,
                "提交后角色继续变化，旧回执仍只读但不允许重发过期增量");
        }
        using (var f = new Fixture(quests,connections,1007,candidate))
        {
            var plan = f.Plan(); string before = JsonSerializer.Serialize(f.Read());
            f.Sql("CREATE TRIGGER skip_abort AFTER INSERT ON character_quest_skip_receipts BEGIN SELECT RAISE(ABORT,'skip rollback'); END");
            Reject<SqliteException>(() => f.Repository.CommitQuestQuickClear(f.Owner,plan),"回执末段SQL失败");
            That(JsonSerializer.Serialize(f.Read()) == before && f.ReceiptCount == 0,"末段失败回滚完成、答案、活动槽、UID、双树与revision");
            f.Sql("DROP TRIGGER skip_abort; CREATE TRIGGER skip_wrong_result AFTER INSERT ON character_quest_skip_receipts BEGIN UPDATE characters SET gold=gold+1 WHERE id=NEW.character_id; END");
            Reject<InvalidOperationException>(() => f.Repository.CommitQuestQuickClear(f.Owner,plan),"最终复读拒绝触发器改写资产");
            That(JsonSerializer.Serialize(f.Read()) == before && f.ReceiptCount == 0,"复读不一致仍回滚全部跳过修改");
            f.Sql("DROP TRIGGER skip_wrong_result");
            That(f.Repository.CommitQuestQuickClear(f.Owner,plan) is { Duplicate:false, ProjectionCurrent:true },"明确回滚后原冻结计划正常提交");
        }
        using (var f = new Fixture(quests,connections,1007,candidate))
        {
            var plan = f.Plan(); var lost = new LostCommitResult(f.Repository);
            Reject<IOException>(() => lost.Commit(f.Owner,plan),"已提交但返回丢失");
            var recovery = lost.Commit(f.Owner,plan);
            That(recovery is { Duplicate:true, ProjectionCurrent:true } && f.ReceiptCount == 1 &&
                recovery.Character.Revision == plan.Expected.Revision+1 && Assets(recovery.Character) == Assets(plan.Expected),
                "返回丢失用原计划恢复唯一回执，无奖励也不会重复写完成或推进revision");
            using var reconnect = f.Owners.Acquire(f.Owner.Account,() => { });
            Reject<InvalidOperationException>(() => f.Repository.CommitQuestQuickClear(f.Owner,plan),"重连接管后旧owner不能恢复或修改");
            That(new CharacterRepository(f.Path,quests).CommitQuestQuickClear(reconnect,plan) is { Duplicate:true, ProjectionCurrent:true },
                "新owner重连恢复同一接取回执与完整已提交状态");
        }
        using (var f = new Fixture(quests,connections,1007,candidate))
        {
            var plan = f.Plan();
            foreach (var (change,restore) in new[] {
                ("UPDATE characters SET revision=revision+1","UPDATE characters SET revision=revision-1"),
                ("UPDATE characters SET gold=gold+1","UPDATE characters SET gold=gold-1"),
                ("UPDATE characters SET grow_type=18","UPDATE characters SET grow_type=17"),
                ("UPDATE character_skill_points SET sp=sp+1 WHERE context=0","UPDATE character_skill_points SET sp=sp-1 WHERE context=0"),
                ("UPDATE character_active_main_quests SET condition_state=2 WHERE quest_id=200","UPDATE character_active_main_quests SET condition_state=1 WHERE quest_id=200"),
                ("UPDATE characters SET quest_cycle_day=20261009","UPDATE characters SET quest_cycle_day=0") })
            {
                f.Sql(change);
                Reject<InvalidOperationException>(() => f.Repository.CommitQuestQuickClear(f.Owner,plan),"未提交的revision、资产、职业、技能、任务和周期变化拒绝原规划");
                That(f.ReceiptCount == 0 && !f.Read().CompletedQuests.Contains(100),"失效规划拒绝不写完成、答案或回执");
                f.Sql(restore);
            }
        }
        Console.WriteLine($"PASS {Count-checks} 无奖励剧情跳过SQL原子、回滚、重放与重连断言");
    }

    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int checks = Count;
        var quests = QuestCatalog.Load(pvf); var connections = QuestConnectionCatalog.Load(pvf);
        var skills = SkillLearningCatalog.Load(pvf, DfoServer.GameWorld.Items.PremiumContractCatalog.Load(pvf).ExpertSkillLevels); var growth = CharacterGrowthCatalog.Load(pvf,skills);
        foreach (int key in new[] { 19,21,23 })
        {
            That(connections.TryGet(key,out var unit),"真实quick clear关联单元可读取: " + key);
            var question = quests.Get(unit.QuestionQuestId!.Value); var definition = question.Definition!;
            var required = definition.PrerequisiteGroups.FirstOrDefault(group => group.Length != 0);
            var completed = (required.IsDefault ? ImmutableArray<int>.Empty : required)
                .Concat(definition.RequiredAnswers.Select(row => row.QuestId)).Distinct().ToArray();
            byte job = key == 21 ? (byte)13 : (byte)0;
            var candidate = CharacterCreation.Prepare("ActualQuick",characters.Get(job),towns.Room,towns.InitialReturn,skills.Get);
            candidate = candidate with { Level = 95, CompletedQuests = completed,
                InitialLearning = SkillPointLedger.Synchronize(job,0,95,candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(95) },skills),
                ActiveMainQuests = [Active(0,checked((ushort)question.Id),QuestQuestionProgress.Unanswered)] };
            using var f = new Fixture(quests,connections,(uint)key,candidate);
            foreach (var answer in definition.RequiredAnswers)
                f.Sql($"UPDATE character_completed_quests SET answer={answer.Answer} WHERE quest_id={answer.QuestId}");
            var plan = f.Plan(); string assets = Assets(plan.Expected);
            var commit = f.Repository.CommitQuestQuickClear(f.Owner,plan);
            That(commit is { Duplicate:false, ProjectionCurrent:true } && commit.Character.ActiveMainQuests.Length == 0 &&
                commit.Character.CompletedQuests.SequenceEqual(plan.CompletedAfter) && commit.Character.QuestAnswers[question.Id] == plan.Answer &&
                commit.Character.CompletedQuests.Contains(plan.MarkerQuestId) && Assets(commit.Character) == assets &&
                f.Scalar("SELECT count(*) FROM character_quest_completion_receipts") == 0,
                "真实旅程全部原序任务、级联标记与回答同事务保存，无普通领奖、资产或职业变化: " + key);
            That(f.Repository.CommitQuestQuickClear(f.Owner,plan) is { Duplicate:true, ProjectionCurrent:true } && f.ReceiptCount == 1,
                "真实三范围均可精确恢复唯一跳过回执: " + key);
        }
        Console.WriteLine($"PASS {Count-checks} 匹配PVF三条旅程无奖励跳过持久化断言");
    }

    static ActiveMainQuest Active(byte slot,ushort id,uint scalar = 1) => new(slot,id,scalar) { AcceptanceId = Guid.NewGuid() };
    static bool Same(Character first,Character second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    static string Assets(Character character) => JsonSerializer.Serialize(new { character.Job,character.GrowType,character.Level,
        character.EquipmentSlotFlags,character.AcceptedStoryLevel,character.Skills,character.Learning,character.Inventory,character.DungeonState,
        character.Position,character.Return,character.AccountQuestAnswers,character.AccountQuestDailyClaims,character.QuestCycleDay });

    static QuestConnectionCatalog Connections(int parent)
    {
        string[] strings = ["[connect quest unit]","[type]","episode quest","[parent key]","[name]","fixture",
            "[question quest]","[quick clear]","[sequence]","[/sequence]","[/connect quest unit]"];
        var tokens = new List<PvfToken> { new(3,0),new(3,1),new(6,2),new(3,3),new(0,parent),new(3,4),new(6,5),new(3,6),new(0,100),
            new(3,7),new(0,101),new(0,2),new(0,200),new(0,202),new(3,8) };
        foreach (int id in new[] { 100,200,201,6381,202 }) { tokens.Add(new(0,id)); tokens.Add(new(0,0)); }
        tokens.Add(new(3,9)); tokens.Add(new(3,10));
        return QuestConnectionCatalog.Parse(tokens,index => strings[index]);
    }

    sealed class LostCommitResult(CharacterRepository inner)
    {
        bool lost;
        public QuestQuickClearCommit Commit(AccountOwners.Lease owner,QuestQuickClearPlan plan)
        {
            var result = inner.CommitQuestQuickClear(owner,plan);
            if (!lost) { lost = true; throw new IOException("selftest committed result lost"); }
            return result;
        }
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),"S5A9-quest-skip-"+Guid.NewGuid().ToString("N"));
        readonly QuestCatalog quests; readonly QuestConnectionCatalog connections; readonly uint parent;
        public string Path { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public long ReceiptCount => Scalar("SELECT count(*) FROM character_quest_skip_receipts");
        public Fixture(QuestCatalog quests,QuestConnectionCatalog connections,uint parent,NewCharacter candidate)
        {
            this.quests = quests; this.connections = connections; this.parent = parent;
            Path = System.IO.Path.Combine(root,"game.db");
            Repository = new(Path,quests);
            new AuthenticationService(new AuthenticationRepository(Path)).Create("quick-clear","fixture-password");
            Owner = Owners.Acquire("quick-clear",() => { });
            Repository.Create(Owner,candidate);
        }
        public Character Read() => Repository.LoadCharacter(Owner,0);
        public QuestQuickClearPlan Plan()
        {
            That(QuestQuickClearPlan.TryCreate(Read(),parent,quests,connections,out var plan,out string reason),"真实持久原快照可规划跳过: " + reason);
            return plan!;
        }
        public SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open(); return db;
        }
        public void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public long Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public void Dispose() { Owner.Dispose(); Directory.Delete(root,true); }
    }
}
