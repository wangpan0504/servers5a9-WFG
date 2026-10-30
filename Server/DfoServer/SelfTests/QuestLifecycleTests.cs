using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.Game.Rental;
using DfoServer.Game.Currency;
using DfoServer.Game.Pvp;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Characters;
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
        using (var f = new Fixture(towns, items, [gift,gift with { Id = 101 }]))
        {
            var before = f.Session.Selected!;
            f.Sql("CREATE TRIGGER quest_accept_fail AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'quest lifecycle rollback'); END");
            Reject<SqliteException>(() => f.Change(100, QuestLifecycleAction.Accept), "接取回执晚期SQL失败");
            That(Same(before, f.Read()) && ReferenceEquals(before, f.Session.Selected) && f.Receipts == 0,
                "失败回滚赠品、UID、活动槽、revision，Session不安装规划资产");
            f.Sql("DROP TRIGGER quest_accept_fail");
            That(f.Change(101,QuestLifecycleAction.Accept) is { Commit: null } && Same(before,f.Read()) &&
                ReferenceEquals(before,f.Session.Selected) && f.Receipts == 0,
                "已回滚但仍待精确重试的接取不被另一个合法任务抢占");
            var accepted = f.Change(100, QuestLifecycleAction.Accept)!;
            That(accepted.Commit is { Duplicate: false } && accepted.Character.Inventory.Items.Single().Uid == 1,
                "已回滚接取可重试同一冻结规划且UID序列没有泄漏");
        }
        using (var f = new Fixture(towns, items, [gift,gift with { Id = 101 },meet with { Id = 102 }],
            active: [new(0,102,1)], loseResult: true))
        {
            var before = f.Session.Selected!;
            Reject<IOException>(() => f.Change(100, QuestLifecycleAction.Accept), "真实提交后返回值丢失");
            var saved = f.Read();
            string operation;
            using (var db = f.Open())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT operation_id FROM character_quest_lifecycle_receipts";
                operation = (string)command.ExecuteScalar()!;
            }
            That(ReferenceEquals(before, f.Session.Selected) && saved.ActiveMainQuests.Length == 2 && f.Receipts == 1,
                "返回丢失保留原在线快照和已提交回执");
            That(f.Change(101,QuestLifecycleAction.Accept) is { Commit: null } &&
                f.Change(102,QuestLifecycleAction.Giveup) is { Commit: null } &&
                f.Session.ChangeQuest(102,QuestLifecycleAction.Condition,f.Catalog,items.Items,items.GoldLimits,items.EpicPieces,
                    new(null,0,0)) is { Commit: null } && Same(saved,f.Read()) && ReferenceEquals(before,f.Session.Selected) && f.Receipts == 1,
                "不同接取、放弃与条件不得替换未决operation，原资产和在线快照不变");
            var recovered = f.Change(100, QuestLifecycleAction.Accept)!;
            That(recovered.Commit is { Duplicate: true, ProjectionCurrent: true } && f.Lost!.SameLifecyclePlan &&
                recovered.Character.Inventory.Items.Single().Core.Value == 3 && f.Receipts == 1 &&
                recovered.Commit.Plan.OperationId.ToString("N") == operation && Same(saved,recovered.Character) &&
                ReferenceEquals(recovered.Character,f.Session.Selected) && f.Session.CanPublishQuestLifecycle(recovered),
                "相同请求恢复冻结计划、随机/期限与原UID，不再发赠品");
            var abandoned = f.Change(100,QuestLifecycleAction.Giveup)!;
            var accepted = f.Change(100,QuestLifecycleAction.Accept)!;
            That(abandoned.Commit is { Duplicate: false } && accepted.Commit is { Duplicate: false } && f.Receipts == 3 &&
                accepted.Character.ActiveMainQuests.Single(row => row.QuestId == 100).AcceptanceId != recovered.Commit!.Plan.AcceptanceId,
                "恢复安装之后允许正常放弃和新接取，不永久锁住任务");
        }
        foreach (string change in new[] { "UPDATE characters SET revision=revision+1",
            "INSERT INTO character_completed_quests(character_id,quest_id) VALUES(1,999)" })
        using (var f = new Fixture(towns, items, [gift], loseResult: true))
        {
            var before = f.Session.Selected!;
            Reject<IOException>(() => f.Change(100, QuestLifecycleAction.Accept), "接取已提交后丢失返回值，随后持久状态变化");
            f.Sql(change);
            var saved = f.Read();
            var recovered = f.Change(100, QuestLifecycleAction.Accept)!;
            That(recovered is { Outcome: QuestLifecycleOutcome.Committed, Commit: { Duplicate: true, ProjectionCurrent: false } } &&
                f.Lost!.SameLifecyclePlan && ReferenceEquals(before,f.Session.Selected) &&
                Same(saved,recovered.Character) && !f.Session.CanPublishQuestLifecycle(recovered),
                "revision或同revision完成位变化后仅返回实际失效回执，不安装Selected或发布成功");
            var retried = f.Change(100, QuestLifecycleAction.Accept)!;
            That(retried.Commit is { Duplicate: true, ProjectionCurrent: false } && f.Lost!.SameLifecyclePlan &&
                ReferenceEquals(recovered.Commit!.Plan,retried.Commit.Plan) && ReferenceEquals(before,f.Session.Selected) &&
                Same(saved,f.Read()) && saved.Inventory.Items.Single().Core.Value == 3 && f.Receipts == 1,
                "失效回执保留原pending精确重试，不新建operation或重复赠品");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(saved,f.Session.Selected!) && f.Change(100,QuestLifecycleAction.Accept)!.Commit is null && f.Receipts == 1,
                "重选恢复最新接取及资产，不重放旧接取增量");
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
        RentalProgress(towns, items);
        RankProgress(towns,items);
        Console.WriteLine($"PASS {Count - checks} 通用任务接取/放弃、赠品原子性与回放/恢复断言");
    }

    static void RankProgress(TownCatalog towns,ItemResources items)
    {
        var quest=QuestEligibilityTests.Quest(100) with {Condition="[pvp rank]",IntData=[1]};
        using var f=new Fixture(towns,items,[quest],ranked:true);
        var ratings=new PvpRatingSettlement(f.Repository,new(PvpRatingTests.Fixture()));
        var accepted=f.Change(100,QuestLifecycleAction.Accept)!;
        That(accepted.Character.ActiveMainQuests.Single().ConditionState==1,"段位接取读取真实初始账本，不能直接归零");
        using var other=f.Owners.Acquire("rank-other",()=>{});
        var opponent=f.Repository.Create(other,new("RankOther",towns.Room,towns.InitialReturn));
        void Win()
        {
            var result=new PvpArenaResultRecord(Guid.NewGuid(),Guid.NewGuid(),296,PvpArenaMode.Individual,
                [new(f.Owner.Account,accepted.Character.Id,0,0,PvpArenaOutcome.Win),new(other.Account,opponent.Id,1,1,PvpArenaOutcome.Lose)],false,0);
            f.Repository.CommitPvpArenaResult(f.Owner,result);ratings.Settle(f.Owner,accepted.Character.Id,result.MatchId);
        }
        for(int index=0;index<27;index++) Win();
        var snapshot=ratings.Capture(f.Owner,accepted.Character.Id);
        That(ratings.Grade(snapshot)==1,"真实逐局来源达到目标段位");
        var planned=QuestLifecyclePlan.SetCondition(f.Session.Selected!,quest,0,pvpGrade:ratings.Grade(snapshot));
        planned.PvpRating=snapshot;
        Win();
        Reject<InvalidOperationException>(()=>f.Repository.CommitQuestLifecycle(f.Owner,planned),"规划后评分版本变化，旧段位计划不能提交");
        That(f.Read().ActiveMainQuests.Single().ConditionState==1,"拒绝旧评分不留下半份任务条件");
        var changed=f.Session.ChangeQuest(100,QuestLifecycleAction.Condition,f.Catalog,items.Items,items.GoldLimits,items.EpicPieces,new(null,0,0));
        That(changed is {Commit:not null} && changed.Character.ActiveMainQuests.Single().ConditionState==0,"新请求重新捕获真实段位后可以推进");
    }

    static void RentalProgress(TownCatalog towns, ItemResources items)
    {
        var quest = QuestEligibilityTests.Quest(QuestRentalProgress.QuestId) with
        {
            Grade = "[system]", Condition = "[custom quest]", SubType = 0, IntData = [1, 0, 1],
            Npc = 2, CompleteNpc = -1, MinimumLevel = 10, MaximumLevel = 99,
            Definition = new([]) { Job = "[all]", AccountQuest = true }
        };
        var service = new RentalService(items.Rental, items.Items);
        var now = DateTimeOffset.UtcNow;
        QuestLifecycleReceipt? Report(Fixture f, QuestClientCondition report) => f.Session.ChangeQuest(QuestRentalProgress.QuestId,
            QuestLifecycleAction.Condition, f.Catalog, items.Items, items.GoldLimits, items.EpicPieces, report);
        InventoryResult Rent(Fixture f, DateTimeOffset? time = null) => f.Session.RentEquipment(service,408030023,14,0,time ?? now);
        ActiveMainQuest[] Active() => [new(0,QuestRentalProgress.QuestId,1) { AcceptanceId = Guid.NewGuid() }];
        void PendingGates(Fixture f, Character selected)
        {
            var settings = new ClientSettingsService(new ClientSettingsRepository(f.Path),
                new CharacterEmotionCatalog([new(1,0,true)]));
            var options = ImmutableArray.Create(new byte[ClientSettingsService.GameOptionLength]);
            That(f.Session.ChangeEmotion(settings,1).Error != 0 && !f.Session.SaveGameOptions(settings,options) &&
                settings.Load(f.Owner,selected.Id).GameOptions.IsDefault && ReferenceEquals(selected,f.Session.Selected),
                "未决租赁拒绝心情及可见性保存，不替换原角色快照");
            // 外部设置变更仍可只读；回放不能让旧Selected丢失原租赁operation的引用。
            settings.SaveGameOptions(f.Owner,options,selected.Id);
            var restored = f.Session.LoadClientSettings(settings);
            var keys = ImmutableArray.Create("I01\t77\nI02\t55\nI03\t73\nI04\t58\nK02\t31\n"u8.ToArray());
            That(restored.VisibilityFlags != selected.VisibilityFlags && f.Session.SaveHotkeys(settings,keys) &&
                ReferenceEquals(selected,f.Session.Selected),
                "未决租赁允许独立键位保存及只读设置加载，不安装可见性快照");
            Reject<InvalidOperationException>(() => f.Session.SetCharacterGold(selected.Name,10),
                "未决租赁拒绝GM金币覆盖");
            Reject<InvalidOperationException>(() => f.Session.SetCardMembership(selected.Name, CardMembershipKind.BlackDiamond,
                DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()), "未决租赁不能借黑钻清除虚弱并覆盖待恢复的原快照");
            var stored = f.Read();
            foreach (string command in new[] { "recharge cera 100", "recharge black-diamond 30" })
                That(Development.DevelopmentGm.Answer(command, f.Owner.Account, f.Repository, f.Owners, _ => f.Session).StartsWith("ERR ") &&
                    Same(stored, f.Read()) && ReferenceEquals(selected, f.Session.Selected),
                    "未决租赁拒绝点券及黑钻充值，不破坏原operation的恢复前提");
            That(f.Session.AcceptStoryDigest() is null && ReferenceEquals(selected,f.Session.Selected),
                "未决租赁阻止剧情写入并保持原计划");
        }
        using (var f = new Fixture(towns,items,[quest],rental:true))
        {
            That(!QuestEligibility.Evaluate(f.Session.Selected! with { Level = 9 },quest,f.Catalog).Eligible,
                "真实租赁任务等级9不能接取");
            var accepted = f.Change(QuestRentalProgress.QuestId,QuestLifecycleAction.Accept)!;
            That(accepted.Character.ActiveMainQuests.Single() is { ConditionState: 1, AcceptanceId: var id } && id != Guid.Empty,
                "31为租赁任务创建独立接取，未租赁初值1");
            var before = f.Read();
            That(Report(f,new(null,0,0))?.Outcome == QuestLifecycleOutcome.Rejected && Same(before,f.Read()),
                "假33不能用客户端归零或当前库存制造成功租赁事件");
            That(!f.Session.RentEquipment(service,408030023,0,0,now).Accepted && Same(before,f.Read()),
                "租赁职业错误不扣点或推进任务");
            var rented = Rent(f);
            var committed = f.Read();
            That(rented.Accepted && rented.Receipt is { } receipt && f.Session.CanPublishInventory(receipt) &&
                receipt.PreviousQuests!.Single().ConditionState == 1 && committed.ActiveMainQuests.Single().ConditionState == 0 &&
                committed.Inventory.AccountWallet.RentalPoints == 6 && committed.Inventory.Items.Single() is { Uid: > 0, Core.IsRental: true } &&
                committed.Revision == before.Revision+1 && f.Receipts == 2,
                "成功租赁钱包、实际UID、原接取归零和operation回执同事务保存");
            using (var writer = f.Open())
            using (var held = writer.BeginTransaction(deferred:false))
            {
                var observed = Report(f,new(null,0,0));
                That(observed?.Outcome == QuestLifecycleOutcome.Unchanged && observed.Commit is null,
                    "已提交租赁的33只读ACK，不与另一writer竞争");
                held.Rollback();
            }
            That(Report(f,new(0x10,0,0))?.Outcome == QuestLifecycleOutcome.Rejected &&
                Report(f,new(null,1,0))?.Outcome == QuestLifecycleOutcome.Rejected && Same(committed,f.Read()) && f.Receipts == 2,
                "租赁33错误通道/值不写库，重复读取不递增revision或receipt");
            That(Rent(f).Accepted && f.Read().Inventory.Items.Length == 2 && f.Read().Inventory.AccountWallet.RentalPoints == 3 &&
                f.Receipts == 2 && f.Read().ActiveMainQuests.Single().ConditionState == 0,
                "任务已归零仍允许另一轮普通租赁，不重复签任务事件");
            f.Change(QuestRentalProgress.QuestId,QuestLifecycleAction.Giveup);
            f.Change(QuestRentalProgress.QuestId,QuestLifecycleAction.Accept);
            That(f.Read().ActiveMainQuests.Single().AcceptanceId != accepted.Character.ActiveMainQuests.Single().AcceptanceId &&
                Report(f,new(null,0,0))?.Outcome == QuestLifecycleOutcome.Rejected,
                "放弃重接不会凭保留的租赁装备完成新Acceptance");
        }
        using (var f = new Fixture(towns,items,[quest],Active(),rental:true))
        {
            f.Session.UpdateProgress(new(CharacterProgressField.Skill,f.Owner.Account,"Lifecycle",7,1));
            var before = f.Read();
            f.Sql("CREATE TRIGGER rental_quest_abort AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'rental rollback'); END");
            var selected = f.Session.Selected!;
            bool injected = false;
            // 学习初始化先capture，再在锁外解析中形成租赁pending；提交阶段须再次核门禁。
            Reject<InvalidOperationException>(() => f.Session.InitializeLearning((job,id) =>
            {
                if (!injected)
                {
                    injected = true;
                    Reject<SqliteException>(() => Rent(f),"租赁进度账本晚期SQL失败");
                }
                return new SkillLearningDefinition(job,id,"skill/fixture.skl",SkillType.Active,[0],
                    Enumerable.Repeat(1,6).ToArray(),Enumerable.Repeat(1,6).ToArray(),1,0,[],new int[6],false,false,[],3,6);
            }),"capture之后形成未决租赁，锁外学习初始化不能提交旧规划");
            bool replanned = false;
            That(injected && Same(before,f.Read()) && ReferenceEquals(f.Session.Selected,selected) && f.Receipts == 0,
                "末段失败整笔回滚钱包、UID、条件和revision");
            That(!f.Session.PlanAndCommitInventory(character => { replanned=true; return InventoryResult.Reject(); }).Accepted &&
                !replanned && f.Change(QuestRentalProgress.QuestId,QuestLifecycleAction.Giveup) is null &&
                f.Session.BeginDungeonSelection() is null,
                "未决租赁阻止新库存、放弃和选图覆盖原恢复计划");
            PendingGates(f,selected);
            f.Sql("DROP TRIGGER rental_quest_abort");
            var recovered = Rent(f,now.AddHours(1));
            That(recovered.Accepted && f.Receipts == 1 && f.Read().Inventory.AccountWallet.RentalPoints == 6 &&
                f.Read().Inventory.Items.Single().ExpireTime == now.AddHours(24).ToUnixTimeSeconds(),
                "明确回滚后恢复原计划，保留原期限且只扣一次租赁点");
        }
        using (var f = new Fixture(towns,items,[quest],Active(),loseResult:true,rental:true))
        {
            var selected = f.Session.Selected!;
            Reject<IOException>(() => Rent(f),"成功租赁事务返回值丢失");
            var saved = f.Read();
            That(ReferenceEquals(selected,f.Session.Selected) && saved.ActiveMainQuests.Single().ConditionState == 0 &&
                saved.Inventory.AccountWallet.RentalPoints == 6 && f.Receipts == 1,
                "返回丢失保留原在线快照，持久租赁事实已经完整提交");
            That(!f.Session.RentEquipment(service,408030030,14,7,now).Accepted && f.Receipts == 1,
                "另一商品请求不能消耗未决operation或重新扣费");
            PendingGates(f,selected);
            var recovered = Rent(f,now.AddHours(1));
            That(recovered.Accepted && f.Lost!.SameLifecyclePlan && f.Receipts == 1 &&
                recovered.Receipt!.Character.Inventory.AccountWallet.Revision == saved.Inventory.AccountWallet.Revision &&
                recovered.Receipt.Character.Inventory.Items.Single().Uid == saved.Inventory.Items.Single().Uid &&
                recovered.Receipt.Character.Inventory.Items.Single().ExpireTime == saved.Inventory.Items.Single().ExpireTime &&
                f.Session.CanPublishInventory(recovered.Receipt),
                "返回丢失查原operation得到Duplicate/current实际钱包版本、同UID和原期限");
            f.Session.ReturnToRoster();
            That(!f.Session.CanPublishInventory(recovered.Receipt!),"换角使原租赁回执失效");
            f.Session.Select(0);f.Session.MarkReady();
            That(Same(f.Read(),f.Session.Selected!),"重选恢复持久租赁状态，不重跑事件");
        }
        using (var f = new Fixture(towns,items,[quest],Active(),loseResult:true,rental:true))
        {
            var selected = f.Session.Selected!;
            Reject<IOException>(() => Rent(f),"后续钱包冲突的租赁返回丢失");
            f.Sql("UPDATE account_wallets SET revision=revision+1");
            var changed = f.Read();
            That(!Rent(f).Accepted && f.Lost!.SameLifecyclePlan && ReferenceEquals(selected,f.Session.Selected) &&
                Same(changed,f.Read()) && f.Receipts == 1,
                "其他钱包操作后原receipt不current，不二扣、不装旧快照或发布成功");
            f.Session.ReturnToRoster();f.Session.Select(0);f.Session.MarkReady();
            That(Rent(f).Accepted && f.Read().Inventory.AccountWallet.RentalPoints == 3,
                "重新选角清原Session未决记录并允许新的正常租赁");
        }
        using (var f = new Fixture(towns,items,[quest],Active(),rental:true))
        {
            var before = f.Session.Selected!;
            f.Sql("UPDATE character_active_main_quests SET acceptance_id='"+Guid.NewGuid().ToString("N")+"'");
            Reject<InvalidOperationException>(() => Rent(f),"同revision接取身份变化拒绝旧计划");
            That(f.Read().Inventory.AccountWallet.RentalPoints == 9 && f.Read().Inventory.Items.Length == 0 && f.Receipts == 0 &&
                ReferenceEquals(before,f.Session.Selected),"旧Acceptance不能扣费或推进新接取");
            f.Session.ReturnToRoster();f.Session.Select(0);f.Session.MarkReady();
            That(Rent(f).Accepted,"重新选角后真实新租赁可推进当前接取");
        }
        using (var f = new Fixture(towns,items,[quest],rental:true))
        {
            var rented = Rent(f);
            That(rented.Accepted && rented.Receipt is not null && f.Receipts == 0 && f.Read().ActiveMainQuests.Length == 0,
                "未接取Q8581的普通租赁不补任务槽或生成任务receipt");
            using var replacement = f.Owners.Acquire(f.Owner.Account,() => { });
            Reject<InvalidOperationException>(() => f.Session.CanPublishInventory(rented.Receipt!),
                "成功租赁回执在账号接管后不能继续发布");
        }
        using (var f = new Fixture(towns,items,[quest],Active(),loseResult:true,rental:true))
        {
            Reject<IOException>(() => Rent(f),"接管前原租赁提交返回丢失");
            using var replacement = f.Owners.Acquire(f.Owner.Account,() => { });
            Reject<InvalidOperationException>(() => Rent(f),"旧generation不能恢复或发布新owner的租赁");
            f.Session.End();
            var next = new GameSession(f.Repository,towns,replacement,questCatalog:f.Catalog);
            next.Select(0);next.MarkReady();
            That(next.RentEquipment(service,408030023,14,0,now).Accepted &&
                next.Selected!.Inventory.AccountWallet.RentalPoints == 3 && next.Selected.Inventory.Items.Length == 2 && f.Receipts == 1,
                "旧Session结束只清自己的pending，新owner从持久事实继续普通租赁");
            next.End();
        }
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
        public Fixture(TownCatalog towns, ItemResources items, StoryQuest[] definitions, ActiveMainQuest[]? active = null, int questItems = 0, bool loseResult = false, bool rental = false, bool ranked = false)
        {
            this.items = items; Path = System.IO.Path.Combine(root, "game.db"); Catalog = new(definitions.ToDictionary(quest => quest.Id)); Repository = new(Path, Catalog);
            new AuthenticationService(new AuthenticationRepository(Path)).Create("quest-lifecycle","fixture-password");
            Owner = Owners.Acquire("quest-lifecycle", () => { });
            var inventory = questItems == 0 ? InventorySnapshot.Empty : new InventorySnapshot([new(0, new(InventoryListType.Main, 177),
                InventoryRewardGrantService.Create(items.Items.Get(10165055), questItems))]);
            var created = Repository.Create(Owner, new("Lifecycle", towns.Room, towns.InitialReturn)
                { ActiveMainQuests = active ?? [], Inventory = inventory, Job = rental ? (byte)14 : (byte)0, Level = rental ? (byte)10 : (byte)1 });
            if (rental) Repository.CommitInventory(Owner,created,new(created.Inventory,created.Inventory with
                { AccountWallet = created.Inventory.AccountWallet.Set(CurrencyKind.RentalPoint,9) },"fixture_rental_points"));
            Lost = loseResult ? new(Repository) { LoseLifecycleResult = true } : null;
            Session = new(Lost is null ? Repository : Lost, towns, Owner, questCatalog:rental || ranked ? Catalog : null,
                pvpRatings:ranked ? new(Repository,new(PvpRatingTests.Fixture())) : null); Session.Select(0); Session.MarkReady();
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
