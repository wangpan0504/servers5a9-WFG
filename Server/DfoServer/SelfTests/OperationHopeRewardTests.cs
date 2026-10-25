using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class OperationHopeRewardTests
{
    public static void Run(PvfArchive pvf,ItemResources items)
    {
        int before = Count;
        var resources = new Resources(pvf,items);
        using (var f = new Fixture(resources))
        {
            var (run,binding) = f.Run();
            var expected = f.Read();
            var preparation = f.Prepare(expected,run,binding);
            var plan = preparation.Plan ?? throw new InvalidOperationException(preparation.Reason);
            That(plan.Rewards.Sum(row => row.GrantedCount) == 60 && plan.Rewards.All(row => row.Core.ItemId == 10307438),"希望奖励按实际资源整批60材料");
            That(plan.ExperienceReward.DifficultyWeight == 110 && plan.ExperienceReward.Amount == 0 && plan.ExperienceReward.Bonuses is null &&
                plan.GrowthPlan is null && plan.Proficiency is null && plan.Snapshot.Contains("op-hope-grade29-not-give-exp-v1"),
                "原生grade29禁经验保留F110来源，不产生额外成长和账号经验");
            That(f.Prepare(expected with { Id = 2 },run,binding).Plan is null &&
                f.Prepare(expected with { ActiveMainQuests = [binding.Acceptance] },run,binding).Plan is null,
                "异角色及未达成scalar不能伪造领奖");
            That(f.Prepare(expected,run,binding with { Identity = binding.Identity with { InstanceId = Guid.NewGuid() } }).Plan is null &&
                f.Prepare(expected,run,binding with { TargetDeath = null }).Plan is null,"旧实例身份及无真实目标死亡拒绝");
            var reward = plan.Inventory.After.Items.Single(row => row.Core.ItemId == 10307438);
            var range = ItemSlotBoundService.Range(reward.Core.Kind,expected.Inventory);
            int maximum = resources.Items.Items.Get(10307438).StackLimit;
            var full = expected.Inventory with { Items = Enumerable.Range(range.Start,range.End-range.Start+1)
                .Select(slot => reward with { Uid = slot+1, Position = new(InventoryListType.Main,checked((short)slot)),
                    Core = reward.Core with { Value = maximum } }).ToImmutableArray() };
            That(f.Prepare(expected with { Inventory = full },run,binding).Outcome == QuestCompletionOutcome.InsufficientSpace,
                "全部材料槽满且堆叠上限时整批拒绝，不扣资格或截断60");
            string adventure = f.Rows("SELECT * FROM account_adventures");
            string original = f.Rows("SELECT * FROM character_completed_quests");
            f.Sql("CREATE TRIGGER hope_late_failure AFTER INSERT ON character_operation_hope_reward_receipts BEGIN SELECT RAISE(ABORT,'hope rollback'); END");
            Reject<SqliteException>(() => f.Repository.CommitOperationHopeReward(f.Owner,plan),"末段收据失败整笔回滚");
            That(f.Receipts == 0 && Same(expected,f.Read()) && f.Rows("SELECT * FROM account_adventures") == adventure &&
                f.Rows("SELECT * FROM character_completed_quests") == original,"失败不留下材料/UID/revision/清槽/成长/账号变化");
            f.Sql("DROP TRIGGER hope_late_failure");
            var first = f.Repository.CommitOperationHopeReward(f.Owner,plan);
            That(!first.Duplicate && first.ProjectionCurrent && first.Rewards.All(row => row.Item.Uid > 0) &&
                first.Character.Revision == expected.Revision+1 && first.Character.ActiveMainQuests.All(row => row.AcceptanceId != plan.AcceptanceId),
                "同一SQL事务交付真实UID并只清本次接取");
            That(first.Character.CompletedQuests.SequenceEqual(expected.CompletedQuests) &&
                first.Character.Learning.Experience == expected.Learning.Experience && f.Rows("SELECT * FROM account_adventures") == adventure &&
                f.Rows("SELECT * FROM character_completed_quests") == original,"希望领奖不写普通永久完成或额外成长冒险团经验");
            // 丢弃首次返回相当于提交成功、发布前结果丢失；原冻结计划必须恢复同一真实UID。
            var replay = f.Repository.CommitOperationHopeReward(f.Owner,plan);
            That(replay.Duplicate && replay.ProjectionCurrent && replay.Rewards.SequenceEqual(first.Rewards) && f.Receipts == 1 &&
                Same(replay.Character,first.Character),"提交丢结果后原计划重放只读收据，不重复材料");
            using (var db = f.Open())
            using (var held = db.BeginTransaction(deferred: false))
            {
                That(f.Repository.CommitOperationHopeReward(f.Owner,plan).Duplicate,"重复领奖在另一WAL写事务持锁时仍只读恢复");
                held.Rollback();
            }
            var reopened = new CharacterRepository(f.Path,resources.Quests,resources.Adventure,resources.Growth);
            That(Same(reopened.LoadCharacter(f.Owner,f.Created.Slot),first.Character),"仓储重开恢复实际奖励和清槽，不补发");
            var collision = f.Run(expected,acceptance: binding.Acceptance);
            var collisionPlan = f.Prepare(expected,collision.Run,collision.Binding).Plan!;
            Reject<InvalidDataException>(() => f.Repository.CommitOperationHopeReward(f.Owner,collisionPlan),"新实例不能重用已经领奖的旧acceptance");
            f.NewAcceptance();
            var secondRun = f.Run();
            var secondPlan = f.Prepare(f.Read(),secondRun.Run,secondRun.Binding).Plan!;
            var second = f.Repository.CommitOperationHopeReward(f.Owner,secondPlan);
            That(!second.Duplicate && f.Receipts == 2 && second.Rewards.Sum(row => row.GrantedCount) == 60 &&
                second.Character.Inventory.Items.Where(row => row.Core.ItemId == 10307438).Sum(row => row.Core.Count) == 120,
                "新的合法实例与接取可再领60，合堆保持原真实UID");
            That(second.Rewards.Single().Item.Uid == first.Rewards.Single().Item.Uid &&
                !f.Repository.CommitOperationHopeReward(f.Owner,plan).ProjectionCurrent,"历史实例重放不覆盖后续版本或发布旧差量");
            run.Return(run.Snapshot.Identity,Guid.NewGuid());
            That(f.Prepare(expected,run,binding).Plan is null,"退出后不能重新规划旧Run奖励");
            var other = f.Repository.Create(f.Owner,new("HopeOther",new(1,0,10,10),null) {
                Level = 95, DungeonState = CharacterDungeonState.NewOrdinary, CompletedQuests = [6177,3145],
                ActiveMainQuests = [new(0,6177,0) { AcceptanceId = Guid.NewGuid() }],
                InitialLearning = new(resources.Growth.GetLevelStartExperience(95),[new(0,10,0,[]),new(1,20,0,[])]) });
            // Create保留种子任务顺序，正式加载按数据库规范序恢复；领奖必须基于实际加载快照。
            other = f.Repository.LoadCharacter(f.Owner,other.Slot);
            var otherRun = f.Run(other);
            var otherPlan = f.Prepare(other,otherRun.Run,otherRun.Binding).Plan!;
            var otherReward = f.Repository.CommitOperationHopeReward(f.Owner,otherPlan);
            string otherReceipt = f.Rows($"SELECT * FROM character_operation_hope_reward_receipts WHERE character_id={other.Id}");
            That(f.Receipts == 3 && otherReward.Rewards.Sum(row => row.GrantedCount) == 60,"另一角色也通过真实领奖事务留下独立收据");
            f.Repository.Delete(f.Owner,f.Repository.LoadDeletionTarget(f.Owner,f.Created.Slot));
            That(f.Receipts == 1 && f.Rows($"SELECT * FROM character_operation_hope_reward_receipts WHERE character_id={other.Id}") == otherReceipt,
                "删除角色清除本角色全部希望收据且保留另一角色收据");
            That(Same(f.Repository.LoadCharacter(f.Owner,other.Slot),otherReward.Character),"删除其它角色不改变幸存角色材料、任务或版本");
        }
        using (var f = new Fixture(resources))
        {
            var (run,binding) = f.Run();
            var expected = f.Read();
            var plan = f.Prepare(expected,run,binding).Plan!;
            f.Sql("UPDATE characters SET revision=revision+1");
            Reject<InvalidOperationException>(() => f.Repository.CommitOperationHopeReward(f.Owner,plan),"未提交旧revision领奖必须拒绝");
            That(f.Receipts == 0 && f.Read().ActiveMainQuests.Any(row => row.AcceptanceId == plan.AcceptanceId),"旧规划拒绝仍保留本次接取");
            using var replacement = f.Owners.Acquire(f.Owner.Account,() => { });
            Reject<InvalidOperationException>(() => f.Repository.CommitOperationHopeReward(f.Owner,plan),"旧owner不能提交或回放到新会话");
        }
        using (var f = new Fixture(resources))
        {
            var expected = f.Read();
            var original = expected.ActiveMainQuests.Single();
            Reject<InvalidDataException>(() => f.Repository.ClearOperationHopeAcceptances(f.Owner,expected,resources.Hope,
                [original with { AcceptanceId = Guid.NewGuid() }]),"清理不能删同槽的新接取或伪造接取");
            f.Sql("CREATE TRIGGER hope_cleanup_failure AFTER DELETE ON character_active_main_quests BEGIN SELECT RAISE(ABORT,'hope cleanup rollback'); END");
            Reject<SqliteException>(() => f.Repository.ClearOperationHopeAcceptances(f.Owner,expected,resources.Hope,[original]),"清理末段失败整笔回滚");
            That(Same(expected,f.Read()),"清理失败保留任务和原revision");
            f.Sql("DROP TRIGGER hope_cleanup_failure");
            var cleaned = f.Repository.ClearOperationHopeAcceptances(f.Owner,expected,resources.Hope,[original]);
            That(cleaned.Revision == expected.Revision+1 && cleaned.ActiveMainQuests.Length == 0 &&
                cleaned.CompletedQuests.SequenceEqual(expected.CompletedQuests),"退出精确清理旧接取但不删永久历史");
            That(Same(cleaned,f.Repository.ClearOperationHopeAcceptances(f.Owner,cleaned,resources.Hope,[])),"无孤立接取清理不重复推进revision");
            var accepted = new ActiveMainQuest(0,6177,1) { AcceptanceId = Guid.NewGuid() };
            var identity = new DungeonRunIdentity(cleaned.Id,0,cleaned.DungeonState!.LastAttempt+1,Guid.NewGuid(),1);
            var charge = new DungeonEntryCharge(null,0,identity,550040) { DungeonId = 8523,
                Hope = new(resources.Hope,0,accepted,cleaned.Revision) };
            f.Sql("CREATE TRIGGER hope_entry_failure AFTER INSERT ON character_active_main_quests BEGIN SELECT RAISE(ABORT,'hope entry rollback'); END");
            Reject<SqliteException>(() => f.Repository.CommitDungeonEntry(f.Owner,cleaned,20,charge),"任务接取失败回滚同事务尝试号与对象key");
            That(Same(cleaned,f.Read()) && f.Read().DungeonState == cleaned.DungeonState,"入场失败没有预留一半身份或扣一半资产");
            f.Sql("DROP TRIGGER hope_entry_failure");
            Reject<InvalidDataException>(() => f.Repository.CommitDungeonEntry(f.Owner,cleaned,20,
                charge with { Hope = charge.Hope with { SourceRevision = cleaned.Revision+1 } }),"入场不能替换抽取来源revision");
            Reject<InvalidDataException>(() => f.Repository.CommitDungeonEntry(f.Owner,cleaned,20,
                charge with { Hope = charge.Hope with { MazeOrdinal = -1 } }),"入场不能绑定未知迷宫候选");
            var entered = f.Repository.CommitDungeonEntry(f.Owner,cleaned,20,charge).Character;
            That(entered.Revision == cleaned.Revision+1 && entered.DungeonState!.LastAttempt == identity.TaskAttempt &&
                entered.DungeonState.NextObjectKey == 20 && entered.ActiveMainQuests.SequenceEqual(new[] { accepted }) &&
                entered.CompletedQuests.SequenceEqual(cleaned.CompletedQuests),"一次入场原子预留身份和固定原任务，不新增永久完成");
            Reject<InvalidOperationException>(() => f.Repository.CommitDungeonEntry(f.Owner,cleaned,20,charge),"同入场重复不能再预留身份或接取");
            That(Same(entered,f.Read()),"重复入场拒绝不改变已落库选择");
            var ordinary = new ActiveMainQuest(1,3146,1) { AcceptanceId = Guid.NewGuid() };
            f.Sql($"INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) VALUES({entered.Id},1,3146,1,'{ordinary.AcceptanceId:N}')");
            var mixed = f.Read();
            Reject<InvalidDataException>(() => f.Repository.ClearOperationHopeAcceptances(f.Owner,mixed,resources.Hope,[ordinary]),"希望清理不允许普通任务");
            var preserved = f.Repository.ClearOperationHopeAcceptances(f.Owner,mixed,resources.Hope,[accepted]);
            That(preserved.ActiveMainQuests.SequenceEqual(new[] { ordinary }) && preserved.CompletedQuests.SequenceEqual(cleaned.CompletedQuests),
                "清理只删除指定Hope接取，保留无关活动任务和所有历史");
        }
        Console.WriteLine($"PASS {Count-before} 代号希望实例领奖原子性断言");
    }

    static bool Same(Character a,Character b) => a.Revision == b.Revision && a.Level == b.Level &&
        a.ActiveMainQuests.SequenceEqual(b.ActiveMainQuests) && a.CompletedQuests.SequenceEqual(b.CompletedQuests) &&
        JsonSerializer.Serialize(a.Inventory) == JsonSerializer.Serialize(b.Inventory) && JsonSerializer.Serialize(a.Learning) == JsonSerializer.Serialize(b.Learning);

    sealed class Resources(PvfArchive pvf,ItemResources items)
    {
        public ItemResources Items { get; } = items;
        public QuestCatalog Quests { get; } = SelfTestCatalogs.Quests(pvf);
        public QuestRewardParameterCatalog Parameters { get; } = SelfTestCatalogs.QuestRewardParameters(pvf);
        public CharacterGrowthCatalog Growth { get; } = SelfTestCatalogs.Growth(pvf);
        public AdventureCatalog Adventure { get; } = new(pvf);
        public OperationHopeCatalog Hope { get; } = OperationHopeCatalog.Load(pvf,SelfTestCatalogs.Dungeons(pvf),SelfTestCatalogs.Quests(pvf),SelfTestCatalogs.Rooms(pvf));
    }

    sealed class Fixture : IDisposable
    {
        readonly Resources resources;
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),"S5A9-hope-reward-"+Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public Character Created { get; }
        public Fixture(Resources resources)
        {
            this.resources = resources; Path = System.IO.Path.Combine(root,"game.db");
            Repository = new(Path,resources.Quests,resources.Adventure,resources.Growth);
            new AuthenticationService(new AuthenticationRepository(Path)).Create("hope-award","fixture-password");
            Owner = Owners.Acquire("hope-award",() => { });
            Created = Repository.Create(Owner,new("HopeAward",new(1,0,10,10),null) {
                Level = 95, DungeonState = CharacterDungeonState.NewOrdinary, CompletedQuests = [6177,3145],
                ActiveMainQuests = [new(0,6177,0) { AcceptanceId = Guid.NewGuid() }],
                InitialLearning = new(resources.Growth.GetLevelStartExperience(95),[new(0,10,0,[]),new(1,20,0,[])]) });
            Repository.InitializeAdventure(Owner,resources.Adventure,DateTimeOffset.UtcNow);
        }
        public Character Read() => Repository.LoadCharacter(Owner,Created.Slot);
        public OperationHopeRewardPreparation Prepare(Character character,SinglePlayerDungeonRun run,OperationHopeMissionBinding binding) =>
            OperationHopeRewardPlan.Prepare(character,resources.Hope,binding,run,resources.Quests,resources.Parameters,
                resources.Items.Items,resources.Items.GoldLimits,resources.Items.EpicPieces,resources.Growth,DateTimeOffset.UtcNow);
        public (SinglePlayerDungeonRun Run,OperationHopeMissionBinding Binding) Run(Character? character = null,ActiveMainQuest? acceptance = null)
        {
            character ??= Read();
            var active = (acceptance ?? character.ActiveMainQuests.Single(row => row.QuestId == 6177)) with { ConditionState = 1 };
            var mission = resources.Hope.Missions[6177];
            var map = new DungeonMap("boss",new(7,0),550059,"map/fixture-hope-reward.map");
            var maze = new DungeonMaze(0,9,3,"fixture",[map.Position],[map.Position],[map],[]);
            var entry = new DungeonEntryPlan(character.Id,1,8523,0,byte.MaxValue,resources.Hope.Difficulty,maze,map,map) { QuestAcceptances = [active] };
            var monster = new DungeonRoomMonster(mission.MonsterId,[mission.MonsterId,0,3,100,100,0,1,1],"[fixed]","[boss]","monster/fixture.mob");
            var room = new DungeonRoomDefinition(map,[monster],[],[]);
            var authority = new Authority();
            That(SinglePlayerDungeonRun.TryCreate(entry,1,100,new([DungeonMonsterPlan.FromRoom(room,95,10)],[]),authority,out var created,out _),"构造明确合成的正式Run目标");
            var run = created!;
            var binding = OperationHopePlan.TryBind(resources.Hope,6177,0,95,run.Snapshot.Identity,active);
            That(binding is not null,"合成SQL夹具绑定普通副本quest0与真实希望任务");
            That(run.CompleteLoading(run.Snapshot.Identity,Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,"合成SQL夹具完成真实Run加载");
            var death = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity));
            That(death is { Outcome: DungeonRunOutcome.Accepted, Pending: not null },"合成SQL夹具原Run接受可信死亡");
            run.ConfirmCommittedDeath(death.Pending!);
            return (run,OperationHopePlan.RecordTarget(binding!,run,death.Pending!));
        }
        public void NewAcceptance() => Sql($"INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) VALUES({Created.Id},0,6177,0,'{Guid.NewGuid():N}'); UPDATE characters SET revision=revision+1 WHERE id={Created.Id}");
        public long Receipts { get { using var db = Open(); using var query = db.CreateCommand(); query.CommandText = "SELECT count(*) FROM character_operation_hope_reward_receipts"; return (long)query.ExecuteScalar()!; } }
        public string Rows(string sql) => JsonSerializer.Serialize(TcpTestFixture.Rows(Path,sql));
        public void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public SqliteConnection Open() { var db = new SqliteConnection("Data Source="+Path+";Pooling=False;Foreign Keys=True"); db.Open(); return db; }
        public void Dispose() { Owner.Dispose(); if (Directory.Exists(root)) Directory.Delete(root,true); }
    }

    sealed class Authority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity) => ConfirmDeath(identity,Guid.NewGuid(),10,1);
    }
}
