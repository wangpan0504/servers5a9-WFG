using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestInventoryPendingTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items,
        SkillLearningCatalog learning, CharacterGrowthCatalog growth, QuestRewardParameterCatalog parameters)
    {
        int checks = Count;
        var ordinary = QuestEligibilityTests.Quest(100) with { Condition = "[seeking]", IntData = [10165055,1],
            Rewards = [new(0,2058,null),new(0,1,null)], Attributes = ["not give exp quest"] };
        var question = QuestEligibilityTests.Quest(400) with { Condition = "[question]", Definition = new([
            new("question info",[new(6,0,"标题"),new(6,0,"正文"),new(6,0,"一"),new(6,0,"二")],[])]) };
        // 手册只接受通向普通后继的回答；夹具须先形成真实可达分支，才能进入最终提交及故障恢复。
        var successor = QuestEligibilityTests.Quest(500) with { Definition = new([]) { RequiredAnswers = [new(400,0)] } };
        StoryQuest[] definitions = [ordinary,QuestEligibilityTests.Quest(101),question,successor,
            QuestEligibilityTests.Quest(999) with { Grade = "[invisible]", IntData = [-1],
                Definition = new([new("synchronization account quest",[new(0,1,null)],[])]) }];
        var consumables = new InventoryConsumableUseService(new([ConsumableUseTests.Hp]));
        var use = new ConsumableUseRequest(new(InventoryListType.Main,65),0,1108,0);
        foreach (string mode in new[] { "lifecycle", "completion", "quick", "manual" })
        {
            using var f = Make(mode == "quick" ? [new(0,400,QuestQuestionProgress.Unanswered) { AcceptanceId = Guid.NewGuid() }] :
                [new(0,100,0) { AcceptanceId = Guid.NewGuid() }]);
            var before = f.Session.Selected!;
            QuestQuickClearPlan? manual = mode == "manual" ? f.Session.PrepareManualQuestSkip(f.Catalog)!.Prepared : null;
            if (mode == "manual") That(manual?.PendingQuestions.SequenceEqual([400]) == true,"手册恢复夹具确实等待本次问题回答");
            // 普通手册包含问题，先完成答案准备，再由最后一批回答触发真实提交。
            string table = mode switch { "lifecycle" => "character_quest_lifecycle_receipts",
                "completion" => "character_quest_completion_receipts", _ => "character_quest_skip_receipts" };
            f.Sql($"CREATE TRIGGER inventory_pending_abort AFTER INSERT ON {table} BEGIN SELECT RAISE(ABORT,'inventory pending rollback'); END");
            TestCheck.Reject<SqliteException>(Commit,"任务末段SQL回滚建立原提交恢复状态: " + mode);
            That(ReferenceEquals(before,f.Session.Selected) && Same(before,f.Read()) && Receipts(f,table) == 0,
                "真实SQL失败保持全部资产、任务和revision: " + mode);
            That(f.Session.BeginDungeonSelection() is null && f.Session.CurrentDungeonSelection is null &&
                f.Session.Phase == DfoServer.Game.Session.SessionPhase.Town && ReferenceEquals(before,f.Session.Selected),
                "未决任务不能进入选图改变原请求场景: " + mode);
            That(f.Session.UseConsumable(use,consumables).Outcome == ConsumableUseOutcome.Rejected &&
                ReferenceEquals(before,f.Session.Selected) && Same(before,f.Read()),
                "消耗品不能绕过未决任务库存门禁: " + mode);
            foreach (bool planned in new[] { false,true })
            foreach (bool town in new[] { false,true })
            {
                int calls = 0;
                var refused = Write(f,planned,town,character => { calls++; return Grant(character); });
                That(!refused.Accepted && refused.Receipt is null && calls == 0 && ReferenceEquals(before,f.Session.Selected) && Same(before,f.Read()),
                    "未决事务拒绝库存规划/执行，原快照不变: " + mode + planned + town);
            }
            f.Sql("DROP TRIGGER inventory_pending_abort");
            Commit();
            That(Receipts(f,table) == 1 && f.Read().Revision == before.Revision+1 && Same(f.Read(),f.Session.Selected!),
                "原任务恢复一次并安装完整持久结果: " + mode);
            if (mode == "lifecycle") That(f.Lost!.SameLifecyclePlan,"接取恢复复用原冻结Plan和OperationId");
            if (mode == "completion") That(f.Lost!.SamePlan && f.Lost.Calls == 2,"领奖恢复复用原冻结Plan和AcceptanceId");
            That(f.Session.BeginDungeonSelection() is not null && f.Session.ReturnFromDungeonSelection() is not null,
                "原任务恢复后可正常打开和退出选图: " + mode);
            That(f.Session.UseConsumable(use,consumables).Outcome == ConsumableUseOutcome.Committed &&
                f.Read().Inventory.Find(use.Position)?.Core.Count == 1 && Same(f.Read(),f.Session.Selected!),
                "原任务恢复后消耗品正常提交一次: " + mode);
            foreach (bool planned in new[] { false,true })
            foreach (bool town in new[] { false,true })
            {
                var prior = f.Read(); int calls = 0;
                var written = Write(f,planned,town,character => { calls++; return Grant(character); });
                That(written.Accepted && written.Receipt is not null && calls == 1 && f.Read().Revision == prior.Revision+1 &&
                    Quantity(f.Read()) == Quantity(prior)+1 && Same(f.Read(),f.Session.Selected!) && Receipts(f,table) == 1,
                    "任务恢复后同一真实库存发放成功且不重放任务: " + mode + planned + town);
            }

            void Commit()
            {
                if (mode == "lifecycle") f.Change(101,QuestLifecycleAction.Accept);
                else if (mode == "completion") f.Finish(ushort.MaxValue);
                else if (mode == "quick")
                {
                    var result = f.Session.QuickClearQuest(1007,f.Catalog,Connections());
                    That(result?.Commit is { Duplicate: false } && result.Commit.Plan.OperationId == before.ActiveMainQuests.Single().AcceptanceId,
                        "快速清除按原接取身份恢复，不替换OperationId");
                }
                else
                {
                    var result = f.Session.AnswerManualQuestSkip([new(400,0)],f.Catalog);
                    That(result?.Commit is { Duplicate: false } && result.Commit.Plan.OperationId == manual!.OperationId,
                        "手册最后答案恢复原operation，不换范围或重抽");
                }
            }
        }
        using (var f = Make([]))
        {
            That(f.Session.BeginDungeonSelection() is not null,"选图内未决任务回归先建立合法选择身份");
            var before = f.Session.Selected!;
            var selection = f.Session.CurrentDungeonSelection;
            f.Sql("CREATE TRIGGER selection_pending_abort AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'selection pending rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Change(101,QuestLifecycleAction.Accept),"选图内任务提交真实SQL回滚");
            That(f.Session.ReturnFromDungeonSelection() is null && ReferenceEquals(selection,f.Session.CurrentDungeonSelection) &&
                f.Session.Phase == DfoServer.Game.Session.SessionPhase.MapSelect && ReferenceEquals(before,f.Session.Selected) && Same(before,f.Read()),
                "选图内未决任务不消费返回身份或写入位置");
            f.Sql("DROP TRIGGER selection_pending_abort");
            That(f.Change(101,QuestLifecycleAction.Accept)?.Commit is not null && f.Session.ReturnFromDungeonSelection() is not null,
                "选图内原任务精确恢复后可以正常返回城镇");
        }
        foreach (bool installed in new[] { false,true })
        foreach (bool planned in new[] { false,true })
        foreach (bool town in new[] { false,true })
        {
            using var f = Make([]);
            var prepared = f.Session.PrepareManualQuestSkip(f.Catalog)!;
            That(prepared.Prepared?.PendingQuestions.SequenceEqual([400]) == true,"库存缓存夹具先准备可达问题");
            if (installed) That(f.Session.AnswerManualQuestSkip([new(400,0)],f.Catalog)?.Commit is not null,"安装回答缓存对照");
            var before = f.Read();
            That(Write(f,planned,town,Grant).Accepted && f.Read().Revision == before.Revision+1 && Quantity(f.Read()) == Quantity(before)+1,
                "未提交手册/已安装缓存允许正常城镇库存变化: " + installed + planned + town);
            That(!f.Session.CanPublishQuestQuickClear(prepared) && f.Session.FinishManualQuestSkip() is null &&
                Receipts(f,"character_quest_skip_receipts") == (installed ? 1 : 0),"缓存随快照自然失效，不补做旧手册");
        }
        using (var f = Make([]))
        {
            var before = f.Session.Selected!; int calls = 0;
            f.Sql("CREATE TRIGGER inventory_pending_abort AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'planning rollback'); END");
            var result = f.Session.PlanAndCommitInventory(character =>
            {
                calls++;
                TestCheck.Reject<SqliteException>(() => f.Change(101,QuestLifecycleAction.Accept),"锁外库存规划期间建立真实未决接取");
                return Grant(character);
            });
            That(!result.Accepted && calls == 1 && ReferenceEquals(before,f.Session.Selected) && Same(before,f.Read()),
                "库存提交阶段再次核门禁，不能提交capture后产生未决事务的旧规划");
            f.Sql("DROP TRIGGER inventory_pending_abort");
            That(f.Change(101,QuestLifecycleAction.Accept)?.Commit is not null && f.Lost!.SameLifecyclePlan &&
                f.Session.PlanAndCommitInventory(Grant).Accepted,"原接取精确恢复后可重新规划库存");
        }
        Console.WriteLine($"PASS {Count-checks} 任务未决提交与库存写入互斥及缓存失效断言");

        QuestOrdinaryCompletionTests.Fixture Make(ActiveMainQuest[] active) => new(towns,characters,learning,growth,parameters,items,
            ordinary,definitions:definitions,active:active,loseResult:true,configure:candidate => candidate with
            {
                Level=20,InitialLearning=SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,20,
                    candidate.InitialLearning with { Experience=growth.GetLevelStartExperience(20) },learning),
                Inventory=candidate.Inventory with { Items=candidate.Inventory.Items.Add(new(0,new(InventoryListType.Main,65),
                    new(1108,ItemKind.Consumable,2))) }
            });
        InventoryResult Grant(Character character) => InventoryRewardGrantService.TryGrantBatch(character,[new(2058,1)],
            items.Items,items.GoldLimits,items.EpicPieces);
    }

    static InventoryResult Write(QuestOrdinaryCompletionTests.Fixture f,bool planned,bool town,Func<Character,InventoryResult> operation) =>
        planned ? f.Session.PlanAndCommitInventory(operation,requireTown:town) : f.Session.MutateInventory(operation,requireTown:town);
    static long Quantity(Character character) => character.Inventory.Items.Where(row => row.Core.ItemId == 2058).Sum(row => (long)row.Core.Value);
    static long Receipts(QuestOrdinaryCompletionTests.Fixture f,string table)
    {
        using var db=f.Open(); using var command=db.CreateCommand(); command.CommandText="SELECT count(*) FROM " + table;
        return (long)command.ExecuteScalar()!;
    }
    static QuestConnectionCatalog Connections()
    {
        string[] strings = ["[connect quest unit]","[type]","episode quest","[parent key]","[name]","fixture",
            "[question quest]","[quick clear]","[sequence]","[/sequence]","[/connect quest unit]"];
        PvfToken[] tokens = [new(3,0),new(3,1),new(6,2),new(3,3),new(0,1007),new(3,4),new(6,5),new(3,6),new(0,400),
            new(3,7),new(0,999),new(0,1),new(0,500),new(0,500),new(3,8),new(0,400),new(0,0),new(0,500),new(0,0),new(3,9),new(3,10)];
        return QuestConnectionCatalog.Parse(tokens,index => strings[index]);
    }
}
