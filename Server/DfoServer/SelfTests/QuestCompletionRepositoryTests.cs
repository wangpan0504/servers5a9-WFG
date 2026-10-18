using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer;

internal static class QuestCompletionRepositoryTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items)
    {
        int before = TestCheck.Count;
        var resources = new Resources(pvf, items);
        using (var fixture = new Fixture(towns, characters, resources))
        {
            var plan = fixture.Plan();
            var expected = plan.Expected;
            var commit = fixture.Repository.CommitQuestCompletion(fixture.Owner, plan);
            Check(!commit.Duplicate && commit.ProjectionCurrent && commit.PreviousLevel == expected.Level && commit.ExperienceReward.Amount == 1780 &&
                commit.Character.Revision == expected.Revision + 1 && commit.Character.Learning.Experience == expected.Learning.Experience + 1780 &&
                commit.Character.Level == plan.Growth.TargetLevel && commit.Character.Level == 4,
                "真实六房合成统计后任务经验按统一规则自然成长，revision只推进一次");
            Check(commit.Character.Learning.Trees.Zip(plan.Growth.Learning.Trees).All(pair => pair.First == pair.Second ||
                pair.First.Context == pair.Second.Context && pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp &&
                pair.First.Skills.SequenceEqual(pair.Second.Skills)) && commit.Character.Skills.SequenceEqual(expected.Skills),
                "两树实际余额/TP/技能等级与稳定槽按原成长计划保留");
            Check(commit.Rewards.Length == 7 && commit.Rewards.Select(row => row.Item.Uid).Distinct().Count() == 7 &&
                commit.Rewards.Select((row, index) => row.Item.Uid > 0 && row.Item.Core == plan.Rewards[index].Core &&
                    row.Item.Position == plan.Rewards[index].Position && row.GrantedCount == 1).All(value => value),
                "七项原序奖励取得真实唯一UID、完整Core及实际库存槽");
            Check(commit.Character.CompletedQuests.Contains(3145) && commit.Character.ActiveMainQuests.All(active => active.QuestId != 3145) &&
                commit.Character.Inventory.Gold == expected.Inventory.Gold && fixture.Count() == 1 && fixture.Read().Inventory.Items.Length == expected.Inventory.Items.Length + 7 &&
                Same(commit.Character, fixture.Read()), "任务清槽/完成表/全部资产/Gold0与唯一账本同事务持久化");
            var replay = fixture.Repository.CommitQuestCompletion(fixture.Owner, plan);
            Check(replay.Duplicate && replay.ProjectionCurrent && replay.PreviousLevel == expected.Level && Same(replay.Character, commit.Character) && fixture.Count() == 1 &&
                replay.Rewards.SequenceEqual(commit.Rewards), "原计划精确重放只读真实receipt，不重复经验/七项资产");
            using (var writer = fixture.Open())
            using (var held = writer.BeginTransaction(deferred: false))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var readOnly = fixture.Repository.CommitQuestCompletion(fixture.Owner, plan);
                Check(readOnly.Duplicate && watch.Elapsed < TimeSpan.FromSeconds(1), "另一真实WAL writer占用时，精确重放不申请写锁");
                held.Rollback();
            }
            var reopened = new CharacterRepository(fixture.Path);
            Check(Same(reopened.LoadCharacter(fixture.Owner, fixture.Created.Slot), commit.Character),
                "仓储重开恢复EXP/等级/SP/七UID/Core/任务，不等同于实际服务重启");
            fixture.Sql($"UPDATE characters SET job=1 WHERE id={fixture.Created.Id}");
            var changed = fixture.Repository.CommitQuestCompletion(fixture.Owner, plan);
            Check(changed.Duplicate && !changed.ProjectionCurrent && fixture.Count() == 1,
                "已提交后同revision身份改变只读账本但增量投影失效");
            fixture.Sql($"UPDATE characters SET job=0 WHERE id={fixture.Created.Id}");
            // 真实已领奖3145数据退回已发布表形状后升级；保留原26列、非连续rowid、规则和实际七UID/Core。
            fixture.Sql("UPDATE character_quest_completion_receipts SET rowid=17; UPDATE character_quest_clear_receipts SET rowid=29");
            string clearRows = JsonSerializer.Serialize(Testing.TcpTestFixture.Rows(fixture.Path,"SELECT rowid,* FROM character_quest_clear_receipts"));
            var receiptColumns = "rowid,character_id,account,acceptance_id,quest_id,event_id,instance_id,task_attempt,epoch,generation,rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,difficulty_key,difficulty_weight,experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,before_revision,revision,rewards";
            string ReceiptRows() => JsonSerializer.Serialize(Testing.TcpTestFixture.Rows(fixture.Path, "SELECT " + receiptColumns + " FROM character_quest_completion_receipts"));
            string originalReceipt = ReceiptRows();
            using (var db = fixture.Open()) DatabaseSchemaTests.RewindQuestSystemToV13(db);
            var migrated = new CharacterRepository(fixture.Path);
            var oldReplay = migrated.CommitQuestCompletion(fixture.Owner, plan);
            Check(ReceiptRows() == originalReceipt && clearRows == JsonSerializer.Serialize(Testing.TcpTestFixture.Rows(fixture.Path,"SELECT rowid,* FROM character_quest_clear_receipts")) && Same(migrated.LoadCharacter(fixture.Owner, fixture.Created.Slot), commit.Character) &&
                oldReplay is { Duplicate: true, ProjectionCurrent: true } && oldReplay.Rewards.SequenceEqual(commit.Rewards),
                "真实v13非空领奖迁移逐列保留旧规则/rowid/EXP/七UID/Core，原计划仍精确只读重放");
        }
        using (var fixture = new Fixture(towns, characters, resources))
        {
            var plan = fixture.Plan();
            var expected = fixture.Read();
            fixture.Sql("CREATE TRIGGER quest_completion_fail AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'quest completion late rollback'); END");
            Reject<SqliteException>(() => fixture.Repository.CommitQuestCompletion(fixture.Owner, plan), "SQL最后账本阶段ABORT整批回滚");
            Check(fixture.Count() == 0 && Same(expected, fixture.Read()) && ReferenceEquals(fixture.Session.Selected, plan.Expected),
                "晚期失败回滚EXP/两树/资产/清槽/完成位，不安装在线规划UID");
            fixture.Sql("DROP TRIGGER quest_completion_fail");
            var retried = fixture.Repository.CommitQuestCompletion(fixture.Owner, plan);
            Check(!retried.Duplicate && fixture.Count() == 1 && retried.Rewards.All(row => row.Item.Uid > 0),
                "明确回滚后原冻结计划重试，不重算奖励或丢失资格");
        }
        using (var fixture = new Fixture(towns, characters, resources))
        {
            var plan = fixture.Plan();
            fixture.Sql($"UPDATE characters SET job=1 WHERE id={fixture.Created.Id}");
            Reject<InvalidOperationException>(() => fixture.Repository.CommitQuestCompletion(fixture.Owner, plan),
                "未提交计划拒绝同revision GM身份修改");
            Check(fixture.Count() == 0 && !fixture.Read().CompletedQuests.Contains(3145), "同revision拒绝未写奖励或完成位");
            fixture.Sql($"UPDATE characters SET job=0 WHERE id={fixture.Created.Id}");
            using var replacement = fixture.Owners.Acquire(fixture.Owner.Account, () => { });
            Reject<InvalidOperationException>(() => fixture.Repository.CommitQuestCompletion(fixture.Owner, plan),
                "过期generation不能写本次领奖");
            Check(fixture.Count() == 0, "旧owner拒绝不产生新receipt");
        }
        // 合成其它编号及奖励声明，验证与3145共用同次真实清图，而非给测试写完成位。
        var variable = resources.Quests.Get(3145) with { Id = 29997, Path = "n_quest/selftest-variable-clear.qst",
            Attributes = ["not give exp quest"], Rewards = [new(0,2058,null),new(0,3,null)],
            Definition = resources.Quests.Get(3145).Definition! with { CannotGiveup = false,
                SelectRewards = [new(0,10403,null),new(0,1,null),new(0,2058,null),new(0,2,null)] } };
        var empty = variable with { Id = 29998, Path = "n_quest/selftest-empty-clear.qst", Rewards = [],
            Definition = variable.Definition! with { SelectRewards = [] } };
        var invalid = empty with { Id = 29996, Path = "n_quest/selftest-invalid-old-clear.qst",
            Definition = empty.Definition! with { Diagnostics = ["旧脚本定义无效"] } };
        var definitions = resources.Quests.All.ToDictionary(row => row.Id); definitions.Add(variable.Id,variable); definitions.Add(empty.Id,empty); definitions.Add(invalid.Id,invalid);
        var common = new Resources(pvf,items,new(definitions));
        using (var fixture = new Fixture(towns,characters,common,additional: [29997,29998],legacy: [29996]))
        {
            Check(fixture.Scalar("SELECT count(*) FROM character_quest_clear_receipts") == 3 &&
                fixture.Scalar("SELECT count(DISTINCT event_id) FROM character_quest_clear_receipts") == 1 &&
                fixture.Scalar("SELECT count(DISTINCT instance_id) FROM character_quest_clear_receipts") == 1,
                "同次权威通关推进三个匹配任务，分别保存接取凭据，不按任务编号或事件唯一性截断");
            Check(fixture.Read().ActiveMainQuests.Single(row => row.QuestId == 29996).ConditionState == 1 &&
                fixture.Session.ReadClearedQuest(29996) is null,
                "旧存档中的失效定义不推进、不补清图账本，也不阻断本次有效任务");
            var prior = fixture.Read();
            var result = fixture.Session.FinishQuest(29997,1,common.Quests,common.Parameters,items.Items,items.GoldLimits,items.EpicPieces,common.Growth);
            Check(result.Receipt?.Commit is { ConditionType: 3, ExperienceReward.Amount: 0, Rewards.Length: 1 } commit &&
                commit.Rewards[0].Item.Core.ItemId == 2058 && commit.Rewards[0].GrantedCount == 5 &&
                commit.Character.Level == prior.Level && JsonSerializer.Serialize(commit.Character.Learning) == JsonSerializer.Serialize(prior.Learning) && commit.Character.Inventory.Gold == prior.Inventory.Gold,
                "其它清图任务按固定3件及选择2件合并真实增量，零经验不改变成长，不按七项领奖限制");
            result = fixture.Session.FinishQuest(29998,ushort.MaxValue,common.Quests,common.Parameters,items.Items,items.GoldLimits,items.EpicPieces,common.Growth);
            Check(result.Receipt?.Commit is { ConditionType: 3, Rewards.Length: 0, ExperienceReward.Amount: 0 } &&
                fixture.Scalar("SELECT count(*) FROM character_quest_completion_receipts WHERE rule_id<>''") == 0,
                "空奖励清图正常交付，新回执不写奖励规则特殊标识");
            Check(fixture.Session.FinishQuest(29998,ushort.MaxValue,common.Quests,common.Parameters,items.Items,items.GoldLimits,items.EpicPieces,common.Growth).Outcome ==
                QuestFinishOutcome.AlreadyCompleted && fixture.Count() == 2,"零奖励任务重复交付不重写完成回执");
        }
        Console.WriteLine($"PASS {TestCheck.Count - before} 3145领奖真实SQLite原子提交/回滚/重放/恢复断言（合成战斗，非实机）");
    }

    internal static void Check(bool condition, string name) => TestCheck.That(condition, name);
    internal static bool Same(Character first, Character second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    internal static void Reject<T>(Action action, string name) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            Check(true, name);
            return;
        }
        throw new InvalidOperationException("FAIL 未遇到预期领奖故障: " + name);
    }

    internal sealed class Resources(PvfArchive archive, ItemResources items, QuestCatalog? questCatalog = null)
    {
        public ItemResources Items { get; } = items;
        public SkillLearningCatalog Learning { get; } = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(archive);
        public CharacterGrowthCatalog Growth { get; } = global::DfoServer.Testing.SelfTestCatalogs.Growth(archive);
        public DungeonKillExperienceCatalog Kills { get; } = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(archive);
        public DungeonCatalog Dungeons { get; } = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(archive);
        public DungeonRoomCatalog Rooms { get; } = global::DfoServer.Testing.SelfTestCatalogs.Rooms(archive);
        public QuestCatalog Quests { get; } = questCatalog ?? global::DfoServer.Testing.SelfTestCatalogs.Quests(archive);
        public QuestRewardParameterCatalog Parameters { get; } = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(archive);
    }

    internal sealed class Fixture : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-quest-completion-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public Resources Resources { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Session { get; }
        public Character Created { get; }
        public ServerLog Log { get; }
        public LostResultRepository? LostResult { get; }
        public Fixture(TownCatalog towns, CharacterCatalog characters, Resources resources, bool loseResult = false,
            byte job = 0, byte growType = 0, ushort[]? additional = null, ushort[]? legacy = null)
        {
            Resources = resources;
            Path = System.IO.Path.Combine(root, "game.db");
            Repository = new(Path, Resources.Quests);
            Owner = Owners.Acquire("quest-completion", () => { });
            LostResult = loseResult ? new(Repository) : null;
            Session = new(LostResult is null ? Repository : LostResult, towns, Owner);
            Created = Session.CreateCharacter("QuestAward", characters.Get(job), resources.Learning);
            for (int at = 0; at < (legacy?.Length ?? 0); at++)
                Sql($"INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) VALUES({Created.Id},{29-at},{legacy![at]},1,'{Guid.NewGuid():N}')");
            if (growType != 0)
                Sql("UPDATE characters SET grow_type=" + growType);
            Session.Select(Created.Slot);
            Session.MarkReady();
            Check(Session.ChangeQuest(3145, QuestLifecycleAction.Accept, resources.Quests, resources.Items.Items,
                resources.Items.GoldLimits, resources.Items.EpicPieces)?.Outcome == QuestLifecycleOutcome.Committed,
                "领奖fixture通过通用接取事务，不依赖建角特例");
            foreach (ushort id in additional ?? [])
                Check(Session.ChangeQuest(id,QuestLifecycleAction.Accept,resources.Quests,resources.Items.Items,
                    resources.Items.GoldLimits,resources.Items.EpicPieces)?.Outcome == QuestLifecycleOutcome.Committed,
                    "额外清图定义通过相同接取事务");
            Check(Session.LeaveRoom() && Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "领奖fixture沿合法入场准备");
            Log = new(System.IO.Path.Combine(root, "logs"));
            Clear();
        }
        void Clear()
        {
            Check(Session.EnterFormalDungeon(3, 3145, 0, Resources.Dungeons, Resources.Rooms) is not null,
                "领奖fixture使用正式单人实例而非直接修改条件");
            var targets = new (byte X, byte Y)[] { (1, 1), (2, 1), (2, 0), (3, 0), (4, 0) };
            for (int room = 0; room < 6; room++)
            {
                var receipt = Session.CompleteFormalDungeonLoading()!;
                foreach (var monster in receipt.Monsters.Monsters.Where(monster => monster.IsBlocking))
                {
                    // 明确合成正常HP统计，仅验证生产资格/事务，不声称已实机击杀。
                    var observation = new NativeDungeonDeathObservation(monster.ObjectKey, receipt.Character.Id,
                        0x111, receipt.Character.Id, 600, 1, 0, checked((uint)monster.CandidateLevel), 504, 650, 1,
                        true, new string('D', 64));
                    Check(Session.ObserveFormalDungeonDeath(observation, Resources.Kills, Resources.Growth).Outcome == DungeonRunOutcome.Accepted,
                        "领奖fixture每个敌方死亡经正式成长事务，不伪造Tobi死亡");
                }
                if (room < 5)
                    Check(Session.MoveFormalDungeon(targets[room].X, targets[room].Y) is not null, "领奖fixture连续合法过房");
            }
            Check(Session.Phase == SessionPhase.Settlement && Session.ReadClearedQuest(3145) is not null,
                "真正末次敌方事务产生本接取的稳定clear receipt");
        }
        public Character Read() => Repository.LoadCharacter(Owner, Created.Slot);
        public QuestCompletionPlan Plan() => QuestCompletionPlan.Prepare(Session.Selected!, Resources.Quests.Get(3145), ushort.MaxValue, Resources.Quests, Resources.Parameters, Resources.Items.Items, Resources.Items.GoldLimits,
            Resources.Items.EpicPieces, Resources.Growth, DateTimeOffset.UtcNow, Session.ReadClearedQuest(3145)!.Clear).Plan ??
                throw new InvalidOperationException("FAIL 领奖fixture未生成完整计划");
        public QuestFinishResult Finish() => Session.FinishSingleClearQuest(3145, Resources.Quests, Resources.Parameters,
            Resources.Items.Items, Resources.Items.GoldLimits, Resources.Items.EpicPieces, Resources.Growth);
        public long Count() => Scalar("SELECT count(*) FROM character_quest_completion_receipts");
        public long Scalar(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        public void Sql(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        public SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
            db.Open();
            return db;
        }
        public void Dispose()
        {
            Log.Dispose();
            Owner.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    // 真实提交结果丢失，仅在第一次返回之后注入；保存原计划引用用于精确重试检查。
    internal sealed class LostResultRepository(ICharacterRepository inner) : ICharacterRepository
    {
        public bool LoseExperienceResult { get; set; }
        public Character CommitExperienceConsumable(AccountOwners.Lease owner, ExperienceConsumablePlan plan, DateTimeOffset now)
        {
            var result = inner.CommitExperienceConsumable(owner, plan, now);
            if (LoseExperienceResult) throw new IOException("合成经验物品已提交但返回结果丢失");
            return result;
        }
        public string ReadAdventureName(AccountOwners.Lease owner) => inner.ReadAdventureName(owner);
        public bool AdventureNameExists(AccountOwners.Lease owner, string name) => inner.AdventureNameExists(owner, name);
        public DfoServer.Game.Adventure.AdventureNameCommit CommitAdventureName(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureNamePlan plan,DfoServer.GameWorld.Adventure.AdventureCatalog catalog,DateTimeOffset now) =>
            inner.CommitAdventureName(owner,plan,catalog,now);
        public DfoServer.Game.Adventure.AdventureSnapshot CommitAdventureExpedition(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureExpeditionPlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.CommitAdventureExpedition(owner, plan, catalog, now);
        public DfoServer.Game.Adventure.AdventureShopCommit CommitAdventureShop(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureShopPlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.CommitAdventureShop(owner, plan, catalog, now);
        public DfoServer.Game.Adventure.AdventureCapsuleCommit CommitAdventureCapsule(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureCapsulePlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.CommitAdventureCapsule(owner, plan, catalog, now);
        public DfoServer.Game.Adventure.AdventureSnapshot CommitAdventureRepresentative(AccountOwners.Lease owner,
            DfoServer.Game.Adventure.AdventureRepresentativePlan plan, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.CommitAdventureRepresentative(owner, plan, catalog, now);
        public DfoServer.Game.Adventure.AdventureSnapshot ReadAdventure(AccountOwners.Lease owner, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.ReadAdventure(owner, catalog, now);
        public void InitializeAdventure(AccountOwners.Lease owner, DfoServer.GameWorld.Adventure.AdventureCatalog catalog, DateTimeOffset now) => inner.InitializeAdventure(owner, catalog, now);
        public DfoServer.Game.Tutorials.TutorialCheckpointResult CommitTutorialCheckpoint(AccountOwners.Lease owner,
            DfoServer.Game.Tutorials.TutorialCheckpointPlan plan) => inner.CommitTutorialCheckpoint(owner, plan);
        public Character CommitTitleAchievement(AccountOwners.Lease owner, DfoServer.Game.Titles.TitleAchievementPlan plan) => inner.CommitTitleAchievement(owner, plan);
        public InventorySnapshot ReadPublicTitleBook(AccountOwners.Lease owner, ushort characterId) => inner.ReadPublicTitleBook(owner, characterId);
        public CardMembership LoadCardMembership(AccountOwners.Lease owner,ushort id,DateTimeOffset now) => inner.LoadCardMembership(owner,id,now);
        public CardMembership SetCardMembership(AccountOwners.Lease owner,string name,CardMembershipKind kind,long expires) => inner.SetCardMembership(owner,name,kind,expires);
        public Character GrantBlackDiamondFatigue(AccountOwners.Lease owner, Character expected, DateTimeOffset now) => inner.GrantBlackDiamondFatigue(owner, expected, now);
        public DungeonPaidCardCommit CommitPaidDungeonCard(AccountOwners.Lease owner,Character expected,PreparedDungeonSettlement prepared,DungeonCardDelivery delivery,DateTimeOffset now)
            => inner.CommitPaidDungeonCard(owner,expected,prepared,delivery,now);
        public DungeonFreeCardCommit CommitFreeDungeonCard(AccountOwners.Lease owner, Character expected,
            PreparedDungeonSettlement prepared, DungeonCardDelivery delivery)
            => inner.CommitFreeDungeonCard(owner, expected, prepared, delivery);
        public Character AdvanceQuestCycle(AccountOwners.Lease owner, Character expected, int day) => inner.AdvanceQuestCycle(owner, expected, day);
        public Character ConsumeDungeonRoomFatigue(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run) =>
            inner.ConsumeDungeonRoomFatigue(owner, expected, run);
        QuestCompletionPlan? first;
        public QuestCompletionPlan? FirstPlan => first;
        public int Calls { get; private set; }
        public bool SamePlan { get; private set; }
        public bool LastDuplicate { get; private set; }
        public bool LoseLifecycleResult { get; init; }
        public bool LoseSceneResult { get; init; }
        public bool LoseGrowthResult { get; init; }
        public Func<PreparedDungeonDeath,bool>? GrowthLossCondition { get; init; }
        public bool LoseCompletionResult { get; init; } = true;
        public int CompletionLossCall { get; set; } = 1;
        public QuestCompletionPlan? LastPlan { get; private set; }
        public bool LoseInventoryResult { get; init; }
        int inventoryCalls;
        public bool SameLifecyclePlan { get; private set; }
        QuestLifecyclePlan? firstLifecycle;
        int lifecycleCalls;
        int sceneCalls;
        public QuestQuickClearCommit CommitQuestQuickClear(AccountOwners.Lease owner, QuestQuickClearPlan plan) => inner.CommitQuestQuickClear(owner, plan);
        int growthCalls;
        public QuestLifecycleCommit CommitQuestLifecycle(AccountOwners.Lease owner, QuestLifecyclePlan plan)
        {
            firstLifecycle ??= plan;
            SameLifecyclePlan = ReferenceEquals(firstLifecycle, plan);
            var result = inner.CommitQuestLifecycle(owner, plan);
            if (LoseLifecycleResult && ++lifecycleCalls == 1)
                throw new IOException("合成任务SQL已提交但返回值丢失");
            if (LoseSceneResult && plan.SceneEvent is not null && ++sceneCalls == 1)
                throw new IOException("合成场景SQL已提交但返回值丢失");
            return result;
        }
        public QuestCompletionCommit CommitQuestCompletion(AccountOwners.Lease owner, QuestCompletionPlan plan)
        {
            Calls++;
            LastPlan = plan;
            first ??= plan;
            SamePlan = ReferenceEquals(first, plan);
            var result = inner.CommitQuestCompletion(owner, plan);
            LastDuplicate = result.Duplicate;
            if (LoseCompletionResult && Calls == CompletionLossCall)
                throw new IOException("合成领奖SQL已提交但返回值丢失");
            return result;
        }
        public AccountWallet LoadAccountWallet(AccountOwners.Lease owner) => inner.LoadAccountWallet(owner);
        public CharacterRosterEntry[] ListRoster(AccountOwners.Lease owner) => inner.ListRoster(owner);
        public int StageLoads { get; private set; }
        public DfoServer.Game.Quests.QuestStageState LoadQuestStageState(AccountOwners.Lease owner, Character expected)
        {
            StageLoads++;
            return inner.LoadQuestStageState(owner, expected);
        }
        public Character LoadCharacter(AccountOwners.Lease owner, ushort slot) => inner.LoadCharacter(owner, slot);
        public Character[] List(AccountOwners.Lease owner) => inner.List(owner);
        public bool NameExists(string name) => inner.NameExists(name);
        public Character Create(AccountOwners.Lease owner, NewCharacter character) => inner.Create(owner, character);
        public CharacterDeletion LoadDeletionTarget(AccountOwners.Lease owner, ushort slot) => inner.LoadDeletionTarget(owner, slot);
        public CharacterDeletion Delete(AccountOwners.Lease owner, CharacterDeletion expected) => inner.Delete(owner, expected);
        public Character Save(AccountOwners.Lease owner, Character expected, Location position, Location? back, InventoryPlan? inventory = null) => inner.Save(owner, expected, position, back, inventory);
        public Character CommitTeleport(AccountOwners.Lease owner, DfoServer.Game.Town.TeleportPlan plan) => inner.CommitTeleport(owner, plan);
        public DfoServer.Game.Dungeons.AssaultDailyCounts LoadAssaultDaily(AccountOwners.Lease owner, ushort characterId, int day) => inner.LoadAssaultDaily(owner, characterId, day);
        public DfoServer.Game.Dungeons.AssaultDailyCommit CommitAssaultClear(AccountOwners.Lease owner, Character expected, int day, bool hidden, DfoServer.Game.Inventory.Models.InventoryPlan? reward) => inner.CommitAssaultClear(owner, expected, day, hidden, reward);
        public Character CommitLearning(AccountOwners.Lease owner, Character expected, CharacterLearningState learning, int? bonusSp = null, int? bonusTp = null) => inner.CommitLearning(owner, expected, learning, bonusSp, bonusTp);
        public Character UpdateProgress(AccountOwners.Lease owner, CharacterProgressEdit edit) => inner.UpdateProgress(owner, edit);
        public Character AdvanceAcceptedStoryLevel(AccountOwners.Lease owner, Character expected) => inner.AdvanceAcceptedStoryLevel(owner, expected);
        public Character CommitSkillLayout(AccountOwners.Lease owner, Character expected, CharacterLearningState learning) => inner.CommitSkillLayout(owner, expected, learning);
        public Character ResetProgress(AccountOwners.Lease owner, CharacterProgressResetPlan plan) => inner.ResetProgress(owner, plan);
        public Character CommitClassChange(AccountOwners.Lease owner, ClassChangePlan plan) => inner.CommitClassChange(owner, plan);
        public DungeonSettlementCommit CommitDungeonSettlement(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run, PreparedDungeonSettlement prepared, CharacterGrowthCatalog catalog, InventoryPlan? cardInventory = null, bool mailCardItem = false,DungeonCardDelivery? premiumDelivery = null,
            bool grantLuckyStar = false, int luckyStarLimit = 0)
            => inner.CommitDungeonSettlement(owner, expected, run, prepared, catalog, cardInventory, mailCardItem,premiumDelivery, grantLuckyStar, luckyStarLimit);
        public Character ReserveDungeonAttempt(AccountOwners.Lease owner, Character expected, uint nextObjectKey) => inner.ReserveDungeonAttempt(owner, expected, nextObjectKey);
        public QuestClearReceipt? LoadQuestClear(AccountOwners.Lease owner, Character expected, ushort questId) => inner.LoadQuestClear(owner, expected, questId);
        public DungeonGrowthCommit CommitDungeonGrowth(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run,
            PreparedDungeonDeath prepared, DungeonExperienceReward reward, CharacterGrowthCatalog catalog, FrozenDungeonGroundDeath? loot = null, QuestDropPlan? questDrops = null)
        {
            var result = inner.CommitDungeonGrowth(owner, expected, run, prepared, reward, catalog, loot, questDrops);
            if (LoseGrowthResult && (GrowthLossCondition?.Invoke(prepared) ?? true) && ++growthCalls == 1)
                throw new IOException("合成死亡事务已提交但返回值丢失");
            return result;
        }
        public DungeonPickupCommit CommitDungeonPickup(AccountOwners.Lease owner, Character expected, DungeonGroundPickupPreparation prepared, InventoryPlan plan) =>
            inner.CommitDungeonPickup(owner, expected, prepared, plan);
        public Character CommitDungeonDiscard(AccountOwners.Lease owner, DungeonInventoryDiscardPreparation prepared) =>
            inner.CommitDungeonDiscard(owner, prepared);
        public Character CommitInventory(AccountOwners.Lease owner, Character expected, InventoryPlan plan)
        {
            var result = inner.CommitInventory(owner, expected, plan);
            if (LoseInventoryResult && ++inventoryCalls == 1) throw new IOException("合成库存已提交但返回结果丢失");
            return result;
        }
        public Character CommitCollectBox(AccountOwners.Lease owner, DfoServer.Game.CollectBox.CollectBoxPlan plan) =>
            inner.CommitCollectBox(owner, plan);
    }
}
