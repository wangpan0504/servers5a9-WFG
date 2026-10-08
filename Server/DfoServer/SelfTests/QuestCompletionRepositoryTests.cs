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
                "真实六房合成统计后任务1780使1974自然到3754/Lv4，revision只推进一次");
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

    internal sealed class Resources(PvfArchive archive, ItemResources items)
    {
        public ItemResources Items { get; } = items;
        public SkillLearningCatalog Learning { get; } = SkillLearningCatalog.Load(archive);
        public CharacterGrowthCatalog Growth { get; } = CharacterGrowthCatalog.Load(archive);
        public DungeonKillExperienceCatalog Kills { get; } = DungeonKillExperienceCatalog.Load(archive);
        public DungeonCatalog Dungeons { get; } = DungeonCatalog.Load(archive);
        public DungeonRoomCatalog Rooms { get; } = DungeonRoomCatalog.Load(archive);
        public QuestCatalog Quests { get; } = QuestCatalog.Load(archive);
        public QuestRewardParameterCatalog Parameters { get; } = QuestRewardParameterCatalog.Load(archive);
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
            byte job = 0, byte growType = 0)
        {
            Resources = resources;
            Path = System.IO.Path.Combine(root, "game.db");
            Repository = new(Path);
            Owner = Owners.Acquire("quest-completion", () => { });
            LostResult = loseResult ? new(Repository) : null;
            Session = new(LostResult is null ? Repository : LostResult, towns, Owner);
            Created = Session.CreateCharacter("QuestAward", characters.Get(job), resources.Learning);
            if (growType != 0)
                Sql("UPDATE characters SET grow_type=" + growType);
            Session.Select(Created.Slot);
            Session.MarkReady();
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
                foreach (var monster in receipt.Monsters.Monsters.Where(monster => monster.Team != 0))
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
        public QuestCompletionPlan Plan() => QuestCompletionPlan.Prepare(Session.Selected!, Session.ReadClearedQuest(3145)!.Clear,
            Resources.Quests.Get(3145), Resources.Parameters, Resources.Items.Items, Resources.Items.GoldLimits,
            Resources.Items.EpicPieces, Resources.Growth, DateTimeOffset.UtcNow).Plan ??
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
        QuestCompletionPlan? first;
        public int Calls { get; private set; }
        public bool SamePlan { get; private set; }
        public bool LastDuplicate { get; private set; }
        public QuestCompletionCommit CommitQuestCompletion(AccountOwners.Lease owner, QuestCompletionPlan plan)
        {
            Calls++;
            first ??= plan;
            SamePlan = ReferenceEquals(first, plan);
            var result = inner.CommitQuestCompletion(owner, plan);
            LastDuplicate = result.Duplicate;
            if (Calls == 1)
                throw new IOException("合成领奖SQL已提交但返回值丢失");
            return result;
        }
        public AccountWallet LoadAccountWallet(AccountOwners.Lease owner) => inner.LoadAccountWallet(owner);
        public CharacterRosterEntry[] ListRoster(AccountOwners.Lease owner) => inner.ListRoster(owner);
        public Character LoadCharacter(AccountOwners.Lease owner, ushort slot) => inner.LoadCharacter(owner, slot);
        public Character[] List(AccountOwners.Lease owner) => inner.List(owner);
        public bool NameExists(string name) => inner.NameExists(name);
        public Character Create(AccountOwners.Lease owner, NewCharacter character) => inner.Create(owner, character);
        public Character Save(AccountOwners.Lease owner, Character expected, Location position, Location? back) => inner.Save(owner, expected, position, back);
        public Character CommitLearning(AccountOwners.Lease owner, Character expected, CharacterLearningState learning, int? totalSp = null, int? totalTp = null) => inner.CommitLearning(owner, expected, learning, totalSp, totalTp);
        public Character CommitSkillLayout(AccountOwners.Lease owner, Character expected, CharacterLearningState learning) => inner.CommitSkillLayout(owner, expected, learning);
        public Character UpdateProgress(AccountOwners.Lease owner, CharacterProgressEdit edit) => inner.UpdateProgress(owner, edit);
        public Character ResetProgress(AccountOwners.Lease owner, CharacterProgressResetPlan plan) => inner.ResetProgress(owner, plan);
        public Character ReserveDungeonAttempt(AccountOwners.Lease owner, Character expected, uint nextObjectKey) => inner.ReserveDungeonAttempt(owner, expected, nextObjectKey);
        public Character ConsumeDungeonRoomFatigue(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run) => inner.ConsumeDungeonRoomFatigue(owner, expected, run);
        public QuestClearReceipt? LoadQuestClear(AccountOwners.Lease owner, Character expected, ushort questId) => inner.LoadQuestClear(owner, expected, questId);
        public DungeonGrowthCommit CommitDungeonGrowth(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run,
            PreparedDungeonDeath prepared, DungeonExperienceReward reward, CharacterGrowthCatalog catalog, FrozenDungeonGroundDeath? loot = null) =>
                inner.CommitDungeonGrowth(owner, expected, run, prepared, reward, catalog, loot);
        public DungeonSettlementCommit CommitDungeonSettlement(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run,
            PreparedDungeonSettlement prepared, CharacterGrowthCatalog catalog) =>
                inner.CommitDungeonSettlement(owner, expected, run, prepared, catalog);
        public DungeonPickupCommit CommitDungeonPickup(AccountOwners.Lease owner, Character expected, DungeonGroundPickupPreparation prepared, InventoryPlan plan) =>
            inner.CommitDungeonPickup(owner, expected, prepared, plan);
        public Character CommitInventory(AccountOwners.Lease owner, Character expected, InventoryPlan plan) => inner.CommitInventory(owner, expected, plan);
    }
}
