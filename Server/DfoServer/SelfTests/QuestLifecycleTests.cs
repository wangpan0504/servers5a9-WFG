using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestLifecycleTests
{
    public static void Run(TownCatalog towns, ItemResources items)
    {
        int checks = Count;
        var meet = QuestEligibilityTests.Quest(100);
        var gift = meet with { Definition = meet.Definition! with { AcceptanceRewards = [new(0, 2058, null), new(0, 3, null)] } };
        using (var f = new Fixture(towns, items, [gift]))
        {
            var before = f.Session.Selected!;
            var accepted = f.Change(100, QuestLifecycleAction.Accept)!;
            var active = accepted.Character.ActiveMainQuests.Single();
            That(accepted.Commit is { Duplicate: false, ProjectionCurrent: true } && active is { QuestId: 100, Slot: 0, ConditionState: 1 } &&
                active.AcceptanceId != Guid.Empty && accepted.Character.Revision == before.Revision + 1 &&
                accepted.Character.Inventory.Items.Single() is { Uid: > 0, Core.ItemId: 2058, Core.Value: 3 },
                "接取赠品、真实UID、初始条件及独立身份一次提交");
            var plan = accepted.Commit!.Plan;
            var replay = f.Repository.CommitQuestLifecycle(f.Owner, plan);
            That(replay.Duplicate && replay.ProjectionCurrent && Same(replay.Character, accepted.Character) && f.Receipts == 1,
                "原冻结接取重放读取回执，不重复赠品或revision");
            using (var writer = f.Open())
            using (var held = writer.BeginTransaction(deferred: false))
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                That(f.Repository.CommitQuestLifecycle(f.Owner, plan).Duplicate && timer.Elapsed < TimeSpan.FromSeconds(1),
                    "另一WAL writer占用时接取回放仅申请读事务");
                held.Rollback();
            }
            That(f.Change(100, QuestLifecycleAction.Accept)!.Commit is null && f.Receipts == 1,
                "新接取请求不能重复接取活动任务");
            var oldReceipt = accepted;
            var abandoned = f.Change(100, QuestLifecycleAction.Giveup)!;
            That(abandoned.Character.ActiveMainQuests.Length == 0 && abandoned.Character.Inventory.Items.Single().Core.Value == 3 &&
                abandoned.Character.Revision == before.Revision + 2 && !f.Session.CanPublishQuestLifecycle(oldReceipt),
                "放弃清槽但不回收普通消耗品赠品，旧增量不能继续发布");
            var reacquired = f.Change(100, QuestLifecycleAction.Accept)!;
            That(reacquired.Character.ActiveMainQuests.Single().AcceptanceId != active.AcceptanceId &&
                reacquired.Character.Inventory.Items.Single().Core.Value == 6 && f.Receipts == 3 &&
                !f.Repository.CommitQuestLifecycle(f.Owner, plan).ProjectionCurrent,
                "重新接取创建新身份；旧提交回执不能覆盖新活动槽");
            f.Session.ReturnToRoster();
            That(!f.Session.CanPublishQuestLifecycle(reacquired), "回选角立即使旧任务回执失效");
            f.Session.Select(0); f.Session.MarkReady();
            That(Same(f.Session.Selected!, new CharacterRepository(f.Path).LoadCharacter(f.Owner, 0)),
                "重新选角和仓储重开恢复真实任务、赠品与身份");
            f.Sql("INSERT INTO character_completed_quests(character_id,quest_id) VALUES(1,999)");
            That(!f.Repository.CommitQuestLifecycle(f.Owner, reacquired.Commit!.Plan).ProjectionCurrent,
                "同revision外部完成位改变使回放增量失效");
        }
        using (var f = new Fixture(towns, items, [gift]))
        {
            var before = f.Session.Selected!;
            f.Sql("CREATE TRIGGER quest_accept_fail AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'quest lifecycle rollback'); END");
            Reject<SqliteException>(() => f.Change(100, QuestLifecycleAction.Accept), "接取回执晚期SQL失败");
            That(Same(before, f.Read()) && ReferenceEquals(before, f.Session.Selected) && f.Receipts == 0,
                "失败回滚赠品、UID、活动槽、revision，Session不安装规划资产");
            f.Sql("DROP TRIGGER quest_accept_fail");
            var accepted = f.Change(100, QuestLifecycleAction.Accept)!;
            That(accepted.Commit is { Duplicate: false } && accepted.Character.Inventory.Items.Single().Uid == 1,
                "已回滚接取可重试同一冻结规划且UID序列没有泄漏");
        }
        using (var f = new Fixture(towns, items, [gift], loseResult: true))
        {
            var before = f.Session.Selected!;
            Reject<IOException>(() => f.Change(100, QuestLifecycleAction.Accept), "真实提交后返回值丢失");
            That(ReferenceEquals(before, f.Session.Selected) && f.Read().ActiveMainQuests.Length == 1 && f.Receipts == 1,
                "返回丢失保留原在线快照和已提交回执");
            var recovered = f.Change(100, QuestLifecycleAction.Accept)!;
            That(recovered.Commit is { Duplicate: true, ProjectionCurrent: true } && f.Lost!.SameLifecyclePlan &&
                recovered.Character.Inventory.Items.Single().Core.Value == 3 && f.Receipts == 1,
                "相同请求恢复冻结计划、随机/期限与原UID，不再发赠品");
        }
        var seeking = meet with { Condition = "[seeking]", IntData = [10165055, 1] };
        var seekingGift = seeking with { Definition = seeking.Definition! with { AcceptanceRewards = [new(0,10165055,null),new(0,1,null)] } };
        using (var f = new Fixture(towns, items, [seekingGift]))
            That(f.Change(100, QuestLifecycleAction.Accept)!.Character.ActiveMainQuests.Single().ConditionState == 0,
                "接取赠品在同事务参与寻物初始达成，不等待客户端再次33");
        using (var f = new Fixture(towns, items, [seeking], [new(0,100,1)]))
        {
            long revision = f.Read().Revision;
            var gained = f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(10165055,1)], items.Items, items.GoldLimits, items.EpicPieces));
            That(gained.Accepted && f.Read().ActiveMainQuests.Single().ConditionState == 0 && f.Read().Revision == revision + 1,
                "共享库存获得与寻物归零一次revision原子保存");
            var material = f.Read().Inventory.Items.Single();
            var deleted = f.Session.MutateInventory(character => InventoryDeleteService.Plan(character.Inventory, material.Position, material.Core.Value, 1));
            That(deleted.Accepted && f.Read().Inventory.Items.Length == 0 && f.Read().ActiveMainQuests.Single().ConditionState == 1,
                "真实库存减少后寻物恢复未达成，不伪造累计获取次数");
            var before = f.Read();
            f.Sql("CREATE TRIGGER quest_inventory_fail AFTER UPDATE OF condition_state ON character_active_main_quests BEGIN SELECT RAISE(ABORT,'inventory quest rollback'); END");
            Reject<SqliteException>(() => f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(10165055,1)], items.Items, items.GoldLimits, items.EpicPieces)), "库存关联进度SQL失败");
            That(Same(before, f.Read()) && Same(before, f.Session.Selected!), "进度失败回滚完整库存UID/revision且不安装在线快照");
        }
        var shared = QuestEligibilityTests.Quest(101) with { Condition = "[seeking]", IntData = [10165055, 2, 10165055, 3] };
        var child = QuestEligibilityTests.Quest(102) with { MainQuest = 100 };
        using (var f = new Fixture(towns, items, [seeking, shared, child], [new(0, 100, 0), new(1, 101, 0), new(2, 102, 1)], questItems: 10))
        {
            var abandoned = f.Change(100, QuestLifecycleAction.Giveup)!;
            That(abandoned.Character.ActiveMainQuests.Single().QuestId == 101 &&
                abandoned.Character.Inventory.Items.Single().Core.Value == 5,
                "放弃连带移除活动子任务，另一任务重复材料行相加后保留所需数量");
            That(QuestLifecyclePlan.HasMaterials(abandoned.Character, shared, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                "保留的实际库存仍满足另一寻物任务");
        }
        child = child with { Definition = child.Definition! with { CannotGiveup = true } };
        using (var f = new Fixture(towns, items, [seeking, child], [new(0, 100, 1), new(1, 102, 1)], questItems: 10))
        {
            var before = f.Read();
            That(f.Change(100, QuestLifecycleAction.Giveup)!.Commit is null && Same(before, f.Read()) && f.Receipts == 0,
                "关联子任务禁止放弃时整组拒绝，不先回收物品或清父槽");
        }
        using (var f = new Fixture(towns, items, [meet], Enumerable.Range(500, 30).Select((id, index) => new ActiveMainQuest((byte)index, (ushort)id, 1)).ToArray()))
            That(f.Change(100, QuestLifecycleAction.Accept)!.Commit is null && f.Read().ActiveMainQuests.Length == 30 && f.Receipts == 0,
                "30槽满额业务拒绝，不覆盖旧槽");
        using (var f = new Fixture(towns, items, [meet]))
        {
            var expected = f.Read();
            var plan = QuestLifecyclePlan.Accept(expected, meet, f.Catalog, items.Items, items.GoldLimits, items.EpicPieces, DateTimeOffset.UtcNow);
            f.Sql("UPDATE characters SET job=1");
            Reject<InvalidOperationException>(() => f.Repository.CommitQuestLifecycle(f.Owner, plan), "同revision职业变更拒绝旧资格");
            using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
            Reject<InvalidOperationException>(() => f.Repository.CommitQuestLifecycle(f.Owner, plan), "过期owner拒绝任务写入");
            That(f.Receipts == 0, "资格和owner失效均不产生任务回执");
        }
        Console.WriteLine($"PASS {Count - checks} 通用任务接取/放弃、赠品原子性与回放/恢复断言");
    }

    private static bool Same(Character a, Character b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    private sealed class Fixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-quest-lifecycle-" + Guid.NewGuid().ToString("N"));
        private readonly ItemResources items;
        public string Path { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Session { get; }
        public QuestCatalog Catalog { get; }
        public QuestCompletionRepositoryTests.LostResultRepository? Lost { get; }
        public Fixture(TownCatalog towns, ItemResources items, StoryQuest[] definitions, ActiveMainQuest[]? active = null, int questItems = 0, bool loseResult = false)
        {
            this.items = items; Path = System.IO.Path.Combine(root, "game.db"); Catalog = new(definitions.ToDictionary(quest => quest.Id)); Repository = new(Path, Catalog);
            Owner = Owners.Acquire("quest-lifecycle", () => { });
            var inventory = questItems == 0 ? InventorySnapshot.Empty : new InventorySnapshot([new(0, new(InventoryListType.Main, 177),
                InventoryRewardGrantService.Create(items.Items.Get(10165055), questItems))]);
            Repository.Create(Owner, new("Lifecycle", towns.Room, towns.InitialReturn) { ActiveMainQuests = active ?? [], Inventory = inventory });
            Lost = loseResult ? new(Repository) { LoseLifecycleResult = true } : null;
            Session = new(Lost is null ? Repository : Lost, towns, Owner); Session.Select(0); Session.MarkReady();
        }
        public QuestLifecycleReceipt? Change(ushort id, QuestLifecycleAction action) =>
            Session.ChangeQuest(id, action, Catalog, items.Items, items.GoldLimits, items.EpicPieces);
        public Character Read() => Repository.LoadCharacter(Owner, 0);
        public long Receipts { get { using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT count(*) FROM character_quest_lifecycle_receipts"; return (long)c.ExecuteScalar()!; } }
        public SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + Path + ";Pooling=False"); db.Open(); return db; }
        public void Sql(string sql) { using var db = Open(); using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        public void Dispose() { Session.End(); Owner.Dispose(); Directory.Delete(root, true); }
    }
}
