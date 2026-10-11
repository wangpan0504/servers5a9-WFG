using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.QuestCompletionRepositoryTests;

namespace DfoServer;

internal static class QuestOrdinaryCompletionTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items)
    {
        int checks = Count;
        var learning = SkillLearningCatalog.Load(pvf); var growth = CharacterGrowthCatalog.Load(pvf, learning);
        var parameters = QuestRewardParameterCatalog.Load(pvf);
        QuestRewardToken[] Rows(params int[] values) => values.Select(value => new QuestRewardToken(0, value, null)).ToArray();
        var seeking = QuestEligibilityTests.Quest(100) with
        {
            Condition = "[seeking]", IntData = [10165055, 2, 10165055, 1], Npc = 2, CompleteNpc = 2,
            Rewards = Rows(2058, 3), Attributes = ["not give exp quest"],
            Definition = new([]) { Job = "[all]", SelectRewards = Rows(10403, 1, 2058, 2).ToImmutableArray() }
        };
        var coverageDefinitions = new[] { seeking,
            seeking with { Id = 101, IntData = [3037,1] },
            seeking with { Id = 102, Rewards = Rows(1999999999,1) },
            seeking with { Id = 103, MainQuest = 100 },
            seeking with { Id = 104, Definition = seeking.Definition! with { PrerequisiteGroups = [[105]] } },
            seeking with { Id = 105, Definition = seeking.Definition! with { PrerequisiteGroups = [[104]] } } };
        var coverage = QuestCoverage.Scan(new(coverageDefinitions.ToDictionary(row => row.Id)), items.Items, items.EpicPieces, parameters);
        That(coverage.Quests.Single(row => row.Id == 100).ImplementedPath &&
            coverage.Quests.Single(row => row.Id == 101).ImplementedPath &&
            coverage.Quests.Where(row => row.Id is 102 or 103).All(row => !row.ImplementedPath) &&
            coverage.PrerequisiteCycles is [{ Length: 2 }], "覆盖清单支持已确认钱包材料，区分缺失模板、隐藏入口与前置循环");
        using (var f = Make(seeking))
        {
            var before = f.Read();
            That(f.Prepare(2).Outcome == QuestCompletionOutcome.Rejected && Same(before, f.Read()), "越界选择不能先扣材料");
            var plan = f.Prepare(1).Plan!;
            That(plan is { GrowthPlan: null, ExperienceReward.Amount: 0, Consumed.Length: 1 } && plan.Consumed[0].Count == 3 &&
                plan.Rewards.Single().GrantedCount == 5 && plan.Inventory.After.Items.Single(item => item.Core.ItemId == 10165055).Core.Count == 2,
                "重复寻物行相加，固定/选择同物品相加，零经验保留原两树");
            var commit = f.Repository.CommitQuestCompletion(f.Owner, plan);
            That(commit is { Clear: null, ConditionType: 0, Rewards.Length: 1 } && commit.Character.Revision == before.Revision + 1 &&
                commit.Character.CompletedQuests.Contains(100) && commit.Character.ActiveMainQuests.Length == 0 &&
                commit.Rewards[0].Item.Uid > 0 && commit.Rewards[0].GrantedCount == 5 && Same(commit.Character, f.Read()),
                "材料、可变数量奖励、真实UID、零XP、清槽和完成回执原子提交");
            using var writer = f.Open(); using var held = writer.BeginTransaction(deferred: false);
            var replay = f.Repository.CommitQuestCompletion(f.Owner, plan);
            That(replay is { Duplicate: true, ProjectionCurrent: true } && Same(replay.Character, commit.Character) && f.Receipts == 1,
                "普通交付返回丢失可仅读回执，另一writer不阻塞或重复扣发"); held.Rollback();
        }
        using (var f = Make(seeking))
        {
            var before = f.Read(); var plan = f.Prepare(1).Plan!;
            f.Sql("CREATE TRIGGER quest_late_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'ordinary quest rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Repository.CommitQuestCompletion(f.Owner, plan), "普通交付末段SQL失败");
            That(Same(before, f.Read()) && f.Receipts == 0, "SQL失败回滚材料、奖励UID序列、余额、清槽和完成位");
            f.Sql("DROP TRIGGER quest_late_abort");
            That(f.Repository.CommitQuestCompletion(f.Owner, plan) is { Duplicate: false, ProjectionCurrent: true } && f.Receipts == 1,
                "回滚后原选择/冻结期限规划可正常提交");
        }
        using (var f = Make(seeking, loseResult: true))
        {
            var before = f.Session.Selected!;
            TestCheck.Reject<IOException>(() => f.Finish(1), "普通交付真实提交后返回丢失");
            That(ReferenceEquals(before, f.Session.Selected) && f.Read().CompletedQuests.Contains(100), "返回丢失不安装临时资产，但保留真实事务");
            That(f.Finish(0).Outcome == QuestFinishOutcome.Rejected && f.Receipts == 1, "不同选择不能冒充原pending请求");
            var recovered = f.Finish(1);
            That(recovered.Receipt?.Commit is { Duplicate: true, ProjectionCurrent: true } && f.Lost!.SamePlan && f.Receipts == 1,
                "相同选择恢复原冻结结果，不重新消耗或抽取奖励");
            That(f.Finish(1).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 1, "安装后的重复交付正常拒绝");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(f.Session.Selected!, f.Read()) && f.Finish(1).Outcome == QuestFinishOutcome.AlreadyCompleted, "重选和进程仓储重开使用持久结果");
        }
        using (var f = Make(seeking, materialCount: 2))
            That(f.Prepare(1).Outcome == QuestCompletionOutcome.Rejected && f.Receipts == 0, "库存不足不依赖客户端scalar0放行");
        using (var f = Make(seeking, locked: true))
            That(f.Prepare(1).Outcome == QuestCompletionOutcome.Rejected && f.Receipts == 0, "锁定材料不能交付扣除");
        var walletSeek = seeking with { IntData = [3033,2,10165055,3], Rewards = Rows(3033,2,3035,4,2058,2),
            Definition = seeking.Definition! with { SelectRewards = [] } };
        foreach (bool loseResult in new[] {false,true})
        using (var f = Make(walletSeek,loseResult: loseResult))
        {
            That(f.Prepare(ushort.MaxValue).Plan is null,"实体材料足够但账号晶体不足仍不能交付");
            That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,[new(3033,3)],
                items.Items,items.GoldLimits,items.EpicPieces)).Accepted,"测试晶体来自现有钱包发放事务，不创建伪物品UID");
            var before = f.Read(); var plan = f.Prepare(ushort.MaxValue).Plan!;
            That(plan.Consumed.Any(row => row.Uid == 0 && row.Position.Index == 354 && row.ItemId == 3033 && row.Count == 2) &&
                plan.Consumed.Any(row => row.Uid > 0 && row.ItemId == 10165055 && row.Count == 3) && plan.Currencies.Length == 2 &&
                CurrencyWallet.Get(plan.Inventory.After,CurrencyKind.BlackCube) == 3 &&
                CurrencyWallet.Get(plan.Inventory.After,CurrencyKind.RedCube) == 4,"实体和钱包材料整笔扣除，钱包奖励不伪造普通物品");
            if (!loseResult)
            {
                f.Sql("CREATE TRIGGER wallet_quest_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'wallet quest rollback'); END");
                TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue),"混合实体与钱包任务末段失败");
                That(Same(before,f.Read()) && f.Receipts == 0,"混合任务材料、钱包revision、奖励UID和完成位一起回滚");
                f.Sql("DROP TRIGGER wallet_quest_abort");
            }
            else TestCheck.Reject<IOException>(() => f.Finish(ushort.MaxValue),"混合钱包任务提交后返回丢失");
            var commit = f.Finish(ushort.MaxValue).Receipt!.Commit;
            That(commit.Duplicate == loseResult && commit.ProjectionCurrent && commit.Currencies.Length == 2 &&
                commit.ChangedSlots.Any(slot => slot.Index == 354) && commit.ChangedSlots.Any(slot => slot.Index == 356) &&
                CurrencyWallet.Get(f.Read().Inventory,CurrencyKind.BlackCube) == 3 && CurrencyWallet.Get(f.Read().Inventory,CurrencyKind.RedCube) == 4 &&
                f.Read().Inventory.Items.Single(row => row.Core.ItemId == 10165055).Core.Count == 2 && Same(commit.Character,f.Read()) && f.Receipts == 1,
                $"钱包扣发相抵仍投影实际绝对余额，结果恢复不重复消费或发奖：lost={loseResult}, duplicate={commit.Duplicate}, current={commit.ProjectionCurrent}, currencies={commit.Currencies.Length}, slots={string.Join(',',commit.ChangedSlots.Select(slot => slot.Index))}, black={CurrencyWallet.Get(f.Read().Inventory,CurrencyKind.BlackCube)}, red={CurrencyWallet.Get(f.Read().Inventory,CurrencyKind.RedCube)}, same={Same(commit.Character,f.Read())}, receipts={f.Receipts}");
            var body = DfoServer.Network.Builders.QuestPackets.Finish(100,0,0,
                commit.Consumed.Select(row => new DfoServer.Network.Builders.QuestConsumedItemProjection(row.Position.Index,row.Count)).ToArray(),0,
                commit.Rewards.Select(row => new DfoServer.Network.Builders.QuestRewardItemProjection(row.Item,row.GrantedCount)).ToArray(),
                currencies: commit.Currencies.Select(row => new DfoServer.Network.Builders.QuestCurrencyRewardProjection(
                    ItemSlotBoundService.CurrencySlot(row.Kind)!.Value,row.ItemId,row.Count)).ToArray());
            That(body.Length == 8+1+2*7+2+3*19,"钱包和实体统一使用本版Main消耗7B行、奖励19B行，数组长度不写死");
        }
        using (var f = Make(walletSeek with { IntData = [10165055,3], Rewards = Rows(3033,1) }))
        {
            f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,[new(3033,int.MaxValue)],
                items.Items,items.GoldLimits,items.EpicPieces)); var before = f.Read();
            That(f.Prepare(ushort.MaxValue).Plan is null && Same(before,f.Read()) && f.Receipts == 0,
                "钱包已满不能静默截断任务奖励或先扣材料");
        }
        using (var f = Make(seeking, fullEquipment: true))
        {
            var before = f.Read();
            That(f.Prepare(0) is { Outcome: QuestCompletionOutcome.InsufficientSpace, Failure: QuestCompletionFailure.InventoryCapacity } &&
                Same(before, f.Read()) && f.Receipts == 0, "满包选择奖励整笔拒绝，原材料保留");
        }
        using (var f = Make(seeking))
        {
            f.Sql("UPDATE character_inventory_items SET item_core=json_set(item_core,'$.ExpireTime',strftime('%s','now')-1) WHERE json_extract(item_core,'$.ItemId')=10165055");
            var before = f.Read();
            var oldPlan = QuestCompletionPlan.Prepare(before, seeking, 1, f.Catalog, parameters, items.Items, items.GoldLimits,
                items.EpicPieces, growth, DateTimeOffset.UtcNow.AddMinutes(-2)).Plan!;
            That(oldPlan is not null, "过期前冻结的材料规划具有完整数量和实例");
            TestCheck.Reject<InvalidOperationException>(() => f.Repository.CommitQuestCompletion(f.Owner, oldPlan!), "未提交的冻结材料在交付时到期");
            That(Same(before, f.Read()) && f.Receipts == 0, "到期拒绝不消耗材料或发任何奖励，已提交回放仍在期限检查之前");
        }
        var meeting = seeking with { Condition = "[meet npc]", IntData = [2], Rewards = Rows(0, 0), Definition = new([]) { Job = "[all]" } };
        using (var f = Make(meeting, materialCount: 0))
        {
            f.Session.ChangeQuest(100, QuestLifecycleAction.Condition, f.Catalog, items.Items, items.GoldLimits, items.EpicPieces, new(null, 0, 0));
            var before = f.Read(); var result = f.Finish(ushort.MaxValue);
            That(result.Receipt?.Commit is { ConditionType: 4, ExperienceReward.Amount: 0, GoldAmount: 101, Rewards.Length: 0 } &&
                result.Receipt.Commit.Character.Inventory.Gold == before.Inventory.Gold + 101 && f.Receipts == 1,
                "item0声明按本服金币表计算，零物品/零经验会面仍真实完成");
        }
        var empty = meeting with { Rewards = [] };
        var question = empty with { Condition = "[question]", CompleteNpc = -1,
            Definition = new([new("question info",[new(6,0,"回答后对话"),new(6,0,"问题"),
                new(6,0,"选项0"),new(6,0,"选项1"),new(6,0,"选项2")],[])]) { Job = "[all]" } };
        var answerBranches = Enumerable.Range(0,3).Select(answer => empty with { Id = 101+answer,
            Definition = empty.Definition! with { PrerequisiteGroups = [[100]], RequiredAnswers = [new(100,answer)] } }).ToArray();
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,question,materialCount: 0,
            loseResult: true,active: [],definitions: [question,.. answerBranches]))
        {
            f.Change(100,QuestLifecycleAction.Accept);
            That(f.Read().ActiveMainQuests.Single().ConditionState == QuestQuestionProgress.Unanswered &&
                f.Prepare(ushort.MaxValue).Plan is null,"问题接取未选答案不能默认交付成第一个分支");
            var unchanged = f.Read();
            That(f.Change(100,QuestLifecycleAction.Condition,new(0x10,1,0))?.Commit is null &&
                f.Change(100,QuestLifecycleAction.Condition,new(null,3,0))?.Commit is null && Same(unchanged,f.Read()),
                "问题仅接受匹配客户端普通回答通道和资源中的零基选项");
            That(f.Change(100,QuestLifecycleAction.Condition,new(null,1,0))!.Character.ActiveMainQuests.Single().ConditionState == 1 &&
                f.Prepare(ushort.MaxValue).Plan is { ConditionType: 42, Answer: 1 },"第二个选项保存零基scalar1，不误当第一回答完成标记");
            TestCheck.Reject<IOException>(() => f.Finish(ushort.MaxValue),"回答及奖励提交后返回丢失");
            var result = f.Finish(ushort.MaxValue).Receipt!.Commit; var restored = f.Read();
            That(result.Duplicate && result.ProjectionCurrent && f.Receipts == 1 && restored.QuestAnswers[100] == 1 &&
                !QuestEligibility.Evaluate(restored,answerBranches[0],f.Catalog).Eligible &&
                QuestEligibility.Evaluate(restored,answerBranches[1],f.Catalog).Eligible &&
                !QuestEligibility.Evaluate(restored,answerBranches[2],f.Catalog).Eligible,"持久回答精确开放对应后继，重复恢复不改选分支");
            var table = DfoServer.Network.Builders.TownPackets.CompletedQuests(restored);
            That(table[104] == 2 && DfoServer.Network.Builders.QuestPackets.Finish(100,42,0,[],0,[]).Length == 10,
                "NOTI356保存回答序号+1；问题ACK34沿本版无材料的普通奖励路径");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Session.Selected!.QuestAnswers[100] == 1 && Same(f.Session.Selected,f.Read()),"回答跨重登保留，不只存在在线槽位");
        }
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,question,materialCount: 0,
            active: [],definitions: [question,.. answerBranches]))
        {
            f.Change(100,QuestLifecycleAction.Accept); f.Change(100,QuestLifecycleAction.Condition,new(null,2,0)); var before = f.Read();
            f.Sql("CREATE TRIGGER question_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'question rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue),"回答回执末段失败");
            That(Same(before,f.Read()) && f.Read().QuestAnswers.Count == 0 && f.Receipts == 0,
                "任务回答、完成位和奖励同事务失败回滚，不能提前开放分支");
        }
        var actualQuestions = QuestCatalog.Load(pvf);
        That(new[] {(3868,3),(3869,2),(6217,3),(6382,2),(6383,2)}.All(row =>
            QuestQuestionProgress.TryCount(actualQuestions.Get(row.Item1),out int count) && count == row.Item2) &&
            QuestQuestionProgress.OrdinaryChoice(actualQuestions.Get(3868),2,actualQuestions) &&
            !QuestQuestionProgress.OrdinaryChoice(actualQuestions.Get(6217),2,actualQuestions),
            "匹配PVF的五种问题选项完整保留；无普通后继的跳过选项不能冒充领奖");
        var seekMeet = empty with { Condition = "[seek n meet npc]", IntData = [10165055,2,10165055,1,2] };
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,seekMeet,materialCount: 0,active: []))
        {
            That(f.Change(100,QuestLifecycleAction.Accept)!.Character.ActiveMainQuests.Single().ConditionState == 515,
                "寻物会面初值包含真实缺额3和独立NPC通道1");
            var before = f.Read();
            That(f.Change(100,QuestLifecycleAction.Condition,new(0x20,0,0))!.Commit is null && Same(before,f.Read()),
                "材料不足不能先清会面通道");
            That(f.Change(100,QuestLifecycleAction.Condition,new(0x10,0,0))!.Outcome == QuestLifecycleOutcome.Unchanged &&
                f.Change(100,QuestLifecycleAction.Condition,new(null,0,0))!.Commit is null,
                "材料通道0不是假完成，普通Boolean也不能代替双通道");
            var granted = f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(10165055,3)],items.Items,items.GoldLimits,items.EpicPieces));
            That(granted.Accepted && f.Read().ActiveMainQuests.Single().ConditionState == 512 &&
                f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.Rejected,
                "真实库存足额只清材料通道，未会面不能交付");
            That(f.Change(100,QuestLifecycleAction.Condition,new(0x20,0,0))!.Character.ActiveMainQuests.Single().ConditionState == 0,
                "当前目标NPC和足额物品允许会面通道归零");
            var item = f.Read().Inventory.Items.Single(value => value.Core.ItemId == 10165055);
            That(f.Session.MutateInventory(character => InventoryDeleteService.Plan(character.Inventory,item.Position,item.Core.Value,1)).Accepted &&
                f.Read().ActiveMainQuests.Single().ConditionState == 1 && f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.Rejected,
                "会面后丢失材料重新打开缺额，保留已完成NPC通道");
            f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(10165055,1)],items.Items,items.GoldLimits,items.EpicPieces));
            var result = f.Finish(ushort.MaxValue);
            That(result.Receipt?.Commit is { ConditionType: 25, Consumed.Length: 1 } &&
                result.Receipt.Commit.Consumed[0].Count == 3 && f.Read().Inventory.Items.All(value => value.Core.ItemId != 10165055),
                "双通道全部达成才原子消耗重复材料行，不把末尾NPC编号当物品");
        }
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,
            seekMeet with { IntData = [10165055,3,99999] },materialCount: 3,active: []))
        {
            f.Change(100,QuestLifecycleAction.Accept);var before = f.Read();
            That(f.Change(100,QuestLifecycleAction.Condition,new(0x20,0,0))!.Commit is null && Same(before,f.Read()),
                "不在当前场景的NPC不能由会面通道上报代替");
        }
        var hunt = empty with { Condition = "[hunt enemy]", IntData = [3,-1,1,1,1] };
        using (var f = Make(hunt,materialCount: 0))
            That(f.Prepare(ushort.MaxValue).Outcome == QuestCompletionOutcome.Rejected, "猎杀剩余非零不能领取普通奖励");
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,hunt,materialCount: 0,active: [new(0,100,0)]))
        {
            var before = f.Read();
            var result = f.Finish(ushort.MaxValue);
            That(result.Receipt?.Commit is { ConditionType: 6, Rewards.Length: 0, ExperienceReward.Amount: 0 } &&
                Same(before with { ActiveMainQuests = [], CompletedQuests = [100], Revision = before.Revision + 1 }, f.Read()),
                "已达成猎杀的分类6零资产回执与完成状态同事务持久化");
        }
        var huntMonster = hunt with { Condition = "[hunt monster]", IntData = [3,-1,1,1] };
        using (var f = Make(huntMonster,materialCount: 0))
            That(f.Prepare(ushort.MaxValue).Outcome == QuestCompletionOutcome.Rejected,"分类2不能交付尚未达成的怪物目标");
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,huntMonster,materialCount: 0,active: [new(0,100,0)]))
        {
            var before = f.Read(); var result = f.Finish(ushort.MaxValue);
            That(result.Receipt?.Commit is { ConditionType: 2, Rewards.Length: 0, ExperienceReward.Amount: 0 } &&
                Same(before with { ActiveMainQuests = [], CompletedQuests = [100], Revision = before.Revision+1 },f.Read()),
                "分类2实际完成快照和独立枚举回执同事务持久化");
        }
        using (var f = Make(empty, materialCount: 0))
        {
            f.Session.ChangeQuest(100, QuestLifecycleAction.Condition, f.Catalog, items.Items, items.GoldLimits, items.EpicPieces, new(null, 0, 0));
            var before = f.Read(); var result = f.Finish(ushort.MaxValue);
            That(result.Receipt?.Commit is { GoldAmount: 0, Rewards.Length: 0 } && result.Receipt.Commit.Character.Revision == before.Revision + 1 &&
                Same(before with { ActiveMainQuests = [], CompletedQuests = [100], Revision = before.Revision + 1 }, result.Receipt.Commit.Character),
                "无金币声明不补金币；零资产交付只保存完成状态和一次revision");
        }
        var firstChild = empty with { MainQuest = 200, RewardType = null };
        var secondChild = firstChild with { Id = 101, Path = "fixture/101.qst" };
        var parent = QuestEligibilityTests.Quest(200) with { Condition = "[quest clear]", SubType = 2, IntData = [100,101], Npc = 2, CompleteNpc = 2 };
        using (var f = new Fixture(towns, characters, learning, growth, parameters, items, firstChild, materialCount: 0,
            definitions: [parent,firstChild,secondChild], active: [new(0,200,2)]))
        {
            // 合成父任务引用两条未完成子任务，按两端的剩余子数合同独立验证2→1→0。
            var original = f.Read();
            That(original.ActiveMainQuests is [{ QuestId: 200, ConditionState: 2 }], "已有顺序父任务保留两条未完成子任务的初值");
            That(f.Change(101, QuestLifecycleAction.Accept)!.Commit is null && Same(original, f.Read()),
                "隐藏子任务不能跳过尚未完成的前一个资源项");
            That(f.Change(100, QuestLifecycleAction.Accept)!.Commit is not null &&
                f.Read().ActiveMainQuests.Single(row => row.QuestId == 100).AcceptanceId != Guid.Empty,
                "真实Town NPC与活动父任务允许C2S31按顺序接取零奖励会面子任务");
            f.Change(100, QuestLifecycleAction.Condition, new(null,0,0));
            var first = f.Finish(ushort.MaxValue);
            That(first.Receipt?.Commit is { Rewards.Length: 0, GoldAmount: 0, ExperienceReward.Amount: 0 } &&
                f.Read().ActiveMainQuests is [{ QuestId: 200, ConditionState: 1 }] && f.Read().CompletedQuests.SequenceEqual([100]),
                "零奖励子任务真实完成，父任务按真实完成集更新剩余子任务数为1");
            f.Change(101, QuestLifecycleAction.Accept); f.Change(101, QuestLifecycleAction.Condition, new(null,0,0));
            var before = f.Read();
            f.Sql("CREATE TRIGGER parent_late_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'parent update rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue,101), "末个子任务与父条件更新末段SQL失败");
            That(Same(before, f.Read()) && f.Receipts == 1, "子完成/清槽与父归零同事务回滚");
            f.Sql("DROP TRIGGER parent_late_abort");
            var last = f.Finish(ushort.MaxValue,101);
            That(last.Receipt?.Commit.Character.ActiveMainQuests is [{ QuestId: 200, ConditionState: 0 }] &&
                f.Read().CompletedQuests.SequenceEqual([100,101]) && !f.Read().CompletedQuests.Contains(200) && f.Receipts == 2,
                "全部真实子完成才使父条件归零，不自动标完成父任务或发父奖励");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            var restored = f.Read();
            That(restored.ActiveMainQuests is [{ QuestId: 200, ConditionState: 0 }] &&
                Same(f.Session.Selected!, restored) && Same(restored with { Learning = original.Learning }, restored),
                "父子进度重登恢复，零经验完整保留原学习状态");
        }
        var rewardedParent = parent with { Attributes = ["not give exp quest"], Rewards = Rows(2058,2) };
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,firstChild,materialCount: 0,
            definitions: [rewardedParent,firstChild,secondChild], active: []))
        {
            var accepted = f.Change(200,QuestLifecycleAction.Accept)!;
            That(accepted.Commit is not null && f.Read().ActiveMainQuests is [{ QuestId: 200, ConditionState: 2 }],
                "新顺序父任务真实接取，两条未完成子任务保持完整初值及独立身份");
            var before = f.Read();
            var forged = before with { ActiveMainQuests = [before.ActiveMainQuests[0] with { ConditionState = 0 }] };
            That(QuestCompletionPlan.Prepare(forged,rewardedParent,ushort.MaxValue,f.Catalog,parameters,items.Items,
                items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow).Outcome == QuestCompletionOutcome.Rejected &&
                f.Finish(ushort.MaxValue,200).Outcome == QuestFinishOutcome.Rejected &&
                f.Change(200,QuestLifecycleAction.Condition,new(null,0,0))!.Commit is null && Same(before,f.Read()),
                "条件0或客户端33不能替代父任务全部真实子完成记录");
            foreach (ushort id in new ushort[] { 100,101 })
            {
                f.Change(id,QuestLifecycleAction.Accept); f.Change(id,QuestLifecycleAction.Condition,new(null,0,0));
                That(f.Finish(ushort.MaxValue,id).Outcome == QuestFinishOutcome.Committed, "顺序子任务真实交付 " + id);
            }
            before = f.Read();
            That(before.ActiveMainQuests is [{ QuestId: 200, ConditionState: 0 }] && before.CompletedQuests.SequenceEqual([100,101]),
                "末子完成后保留未领奖父槽，不提前发父奖励");
            f.Sql("CREATE TRIGGER parent_reward_abort AFTER INSERT ON character_quest_completion_receipts WHEN NEW.quest_id=200 BEGIN SELECT RAISE(ABORT,'parent reward rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue,200), "父奖励末段SQL失败");
            That(Same(before,f.Read()) && f.Receipts == 2, "父奖励、实际UID、清槽、完成位与回执整笔回滚，子完成保留");
            f.Sql("DROP TRIGGER parent_reward_abort");
            var finished = f.Finish(ushort.MaxValue,200);
            That(finished.Receipt?.Commit is { ConditionType: 44, Clear: null, Rewards.Length: 1, ExperienceReward.Amount: 0 } &&
                finished.Receipt.Commit.Rewards[0].GrantedCount == 2 && f.Read().ActiveMainQuests.Length == 0 &&
                f.Read().CompletedQuests.SequenceEqual([100,101,200]) && f.Read().Revision == before.Revision + 1 && f.Receipts == 3,
                "父任务分类44原子领取自身奖励，不制造清图或再发子奖励");
            That(f.Finish(ushort.MaxValue,200).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 3,
                "父交付安装后的重复不再发奖励");
        }
        var profession = empty with { MinimumLevel = 15, RewardType = "[grow type]", Rewards = Rows(1),
            Definition = new([]) { Job = "[all]", JobChange = 1 } };
        NewCharacter AtTransferLevel(NewCharacter candidate)
        {
            var state = SkillPointLedger.Synchronize(candidate.Job, 0, 15, candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(15), BonusSp = 137, BonusTp = 11 }, learning);
            var paid = SkillLearning.Plan(candidate.Job, 0, 15, state, new(0,0,[new(5,false,2)]), learning);
            That(paid.Success, "转职前保留实际已购买技能的合法来源");
            state = paid.State! with { Trees = paid.State!.Trees.Select(tree => tree with
                { Skills = tree.Skills.Select(skill => skill.Id == 5 ? skill with { Command = "000302" } : skill).ToArray() }).ToArray() };
            return candidate with { Level = 15, InitialLearning = state,
                Skills = state.Trees[0].Skills.Select(skill => new CharacterSkill(skill.Id,skill.Level)).OrderBy(skill => skill.Id).ToArray() };
        }
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,profession,materialCount: 0,configure: AtTransferLevel))
        {
            f.Change(100,QuestLifecycleAction.Condition,new(null,0,0));
            var before = f.Read(); var online = f.Session.Selected!; var plan = f.Prepare(ushort.MaxValue).Plan!;
            That(plan is { ProfessionGrowType: 1, GrowthPlan: null, Rewards.Length: 0 } && plan.TargetLearning.Experience == before.Learning.Experience,
                "零经验转职独立规划实际职业分支，不构造物品奖励或经验");
            f.Sql("CREATE TRIGGER profession_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'profession rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue), "转职末段SQL失败");
            That(Same(before,f.Read()) && f.Receipts == 0 && ReferenceEquals(online,f.Session.Selected),
                "转职、学习、点数、完成位与回执全部回滚，在线角色不安装候选");
            f.Sql("DROP TRIGGER profession_abort");
            var result = f.Finish(ushort.MaxValue); var after = f.Read();
            That(result.Receipt?.Commit is { ProfessionGrowType: 1, PreviousGrowType: 0, Clear: null } && after.GrowType == 1 &&
                after.Revision == before.Revision + 1 && after.CompletedQuests.Contains(100) && after.ActiveMainQuests.Length == 0 &&
                after.Learning.BonusSp == 137 && after.Learning.BonusTp == 11 &&
                before.Learning.Trees.Zip(after.Learning.Trees).All(pair => pair.First.Skills.All(skill =>
                    pair.Second.Skills.Any(saved => saved.Id == skill.Id && saved.Slot == skill.Slot && saved.Level >= skill.Level && saved.Command == skill.Command))) &&
                Same(before with { Inventory = after.Inventory },before), "正常转职同事务保留两树已购买技能、原槽、指令、额外来源和所有物品");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(after,f.Session.Selected!) && f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 1,
                "转职重登恢复完整领域状态，重复请求不重置技能或发奖");
            var actual = QuestCatalog.Load(pvf);
            var branches = actual.All.Where(quest => quest.Grade == "[epic]" && quest.MinimumLevel is >= 1 and <= 20 &&
                quest.RewardType == "[grow type]" && quest.Definition!.JobChange == 1).ToArray();
            foreach (var quest in branches)
            {
                var candidate = before with { ActiveMainQuests = [], CompletedQuests = quest.Definition!.PrerequisiteGroups[0].ToArray() };
                var eligible = Enumerable.Range(0,16).Select(job => candidate with { Job = (byte)job }).Where(value =>
                    QuestEligibility.Evaluate(value,quest,actual).Eligible).ToArray();
                That(eligible.Length == 1 && QuestProfessionReward.Target(eligible[0],quest,learning) == quest.Rewards[0].Value,
                    "实际主线转职任务限定真实职业与CHR分支 " + quest.Id);
            }
            That(branches.Length == 59, "匹配资源59条Lv15主线转职分支全部具有合法首次转职奖励");
            That(QuestCompletionPlan.Prepare(before with { GrowType = 1 },profession,ushort.MaxValue,f.Catalog,parameters,items.Items,
                items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow).Outcome == QuestCompletionOutcome.Rejected,
                "已转职角色不能再次领取首次转职奖励");
        }
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,profession with { Attributes = [] },materialCount: 0,
            loseResult: true,configure: AtTransferLevel))
        {
            f.Change(100,QuestLifecycleAction.Condition,new(null,0,0)); var before = f.Session.Selected!;
            TestCheck.Reject<IOException>(() => f.Finish(ushort.MaxValue), "转职和经验已提交但返回丢失");
            var saved = f.Read(); var recovered = f.Finish(ushort.MaxValue);
            That(saved.GrowType == 1 && saved.Learning.Experience > before.Learning.Experience &&
                recovered.Receipt?.Commit is { Duplicate: true, ProjectionCurrent: true } && f.Lost!.SamePlan &&
                Same(saved,f.Session.Selected!) && f.Receipts == 1, "转职与任务经验采用原冻结结果恢复，不重新发奖或计算技能");
        }
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,profession,materialCount: 0,
            configure: candidate => { var prepared = AtTransferLevel(candidate); return prepared with { InitialLearning = prepared.InitialLearning with { BonusTp = null } }; }))
            That(f.Prepare(ushort.MaxValue).Outcome == QuestCompletionOutcome.Rejected && f.Receipts == 0,
                "未知技能来源不能通过正常转职变成GM重置");
        foreach (byte stage in new byte[] {1,2})
        {
            byte previousGrow = checked((byte)(1 | (stage - 1) << 4)); byte targetGrow = checked((byte)(1 | stage << 4));
            byte level = stage == 1 ? (byte)50 : (byte)75;
            var awakening = empty with { MinimumLevel = level, RewardType = "[awakening type]", Rewards = Rows(stage),
                Definition = new([]) { Job = "[all]", GrowType = 1, JobChange = stage + 1 } };
            NewCharacter AtAwakeningLevel(NewCharacter candidate)
            {
                var state = SkillPointLedger.Synchronize(candidate.Job,previousGrow,level,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(level), BonusSp = 137, BonusTp = 11 },learning);
                var paid = SkillLearning.Plan(candidate.Job,previousGrow,level,state,new(0,0,[new(5,false,2)]),learning);
                That(paid.Success,"觉醒前已购买技能的来源合法");
                return candidate with { GrowType = previousGrow, Level = level, InitialLearning = paid.State!,
                    Skills = paid.State!.Trees[0].Skills.Select(skill => new CharacterSkill(skill.Id,skill.Level)).ToArray() };
            }
            using var f = new Fixture(towns,characters,learning,growth,parameters,items,awakening,materialCount: 0,
                configure: AtAwakeningLevel,loseResult: stage == 2);
            f.Change(100,QuestLifecycleAction.Condition,new(null,0,0)); var before = f.Read();
            That(f.Prepare(ushort.MaxValue).Plan is { } plan && plan.TargetGrowType == targetGrow && plan.Rewards.Length == 0,
                "觉醒规划只推进一阶，保持基础职业分支");
            if (stage == 1)
            {
                f.Sql("CREATE TRIGGER awaken_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'awakening rollback'); END");
                TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue),"觉醒回执末段失败");
                That(Same(before,f.Read()),"觉醒、免费赠技、SP/TP和完成位整笔回滚");
                f.Sql("DROP TRIGGER awaken_abort");
            }
            else TestCheck.Reject<IOException>(() => f.Finish(ushort.MaxValue),"二觉提交后返回丢失");
            var result = f.Finish(ushort.MaxValue); var after = f.Read();
            That(after.GrowType == targetGrow && result.Receipt?.Commit.ProfessionGrowType == targetGrow &&
                result.Receipt.Commit.Duplicate == (stage == 2) && after.Learning.BonusSp == 137 && after.Learning.BonusTp == 11 &&
                before.Learning.Trees.Zip(after.Learning.Trees).All(pair => pair.First.Skills.All(skill =>
                    pair.Second.Skills.Any(saved => saved.Id == skill.Id && saved.Slot == skill.Slot && saved.Level >= skill.Level))),
                "一觉和二觉保留已购等级、绑定和额外点数，结果丢失时重放同一冻结回执");
            That(f.Receipts == 1 && f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.AlreadyCompleted,
                "觉醒重复交付不再次赠技或更改点数");
            var backwards = awakening with { Rewards = Rows(stage == 1 ? 2 : 1) };
            TestCheck.Reject<NotSupportedException>(() => QuestProfessionReward.Target(before,backwards,learning),"不能跳级或倒退觉醒");
            foreach (byte job in new byte[] {9,10})
                That(QuestProfessionReward.Target(before with { Job = job, GrowType = checked((byte)((stage - 1) << 4)) },awakening,learning) == stage << 4,
                    "黑暗武士、缔造者按CHR的零分支逐级觉醒，不伪造首次转职");
        }
        That(QuestCycle.Day(new DateTimeOffset(2026,10,9,5,59,59,TimeSpan.FromHours(8))) == 20261008 &&
            QuestCycle.Day(new DateTimeOffset(2026,10,9,6,0,0,TimeSpan.FromHours(8))) == 20261009 &&
            QuestCycle.Day(new DateTimeOffset(2027,1,1,5,59,59,TimeSpan.FromHours(8))) == 20261231,
            "每日周期严格采用UTC+8 06:00，跨年仍归正确游戏日");
        var batch = seeking with { Grade = "[normaly repeat]", Attributes = [], Rewards = Rows(0,77,2058,3) };
        using (var f = Make(batch,materialCount: 7,loseResult: true))
        {
            var before = f.Read(); var single = f.Prepare(1).Plan!; var twice = f.Prepare(1,2).Plan!;
            That(f.Prepare(1,0).Plan is null && f.Prepare(1,3).Plan is null && f.Receipts == 0 && Same(before,f.Read()),
                "批量交付零倍数、材料不足均在扣发前拒绝");
            That(twice.Multiplier == 2 && twice.Consumed.Single().Count == 6 && twice.Rewards.Single().GrantedCount == 10 &&
                twice.ExperienceReward.Amount == single.ExperienceReward.Amount*2 && twice.GoldAmount == 154,
                "重复寻物逐次奖励取整后倍增，重复材料行、固定和选择奖励整批相加");
            TestCheck.Reject<IOException>(() => f.Finish(1,multiplier: 2),"真实批量奖励提交后返回丢失");
            That(f.Finish(1).Outcome == QuestFinishOutcome.Rejected && f.Receipts == 1,
                "不同倍数不能恢复另一份已提交批量请求");
            var restored = f.Finish(1,multiplier: 2).Receipt!.Commit;
            That(restored.Duplicate && restored.ProjectionCurrent && f.Lost!.SamePlan && f.Receipts == 1 &&
                f.Read().Inventory.Items.Single(item => item.Core.ItemId == 10165055).Core.Count == 1 &&
                restored.Rewards.Single().GrantedCount == 10 && Same(restored.Character,f.Read()),
                "批量交付用同一冻结回执恢复数量、UID、金币、经验和清槽，不逐次提交");
            using (var db = f.Open())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT multiplier FROM character_quest_completion_receipts";
                That(Convert.ToInt64(command.ExecuteScalar()) == 2,"交付倍数独立持久化，进程恢复可验证原请求");
            }
            f.Change(100,QuestLifecycleAction.Accept);
            That(f.Read().ActiveMainQuests.Single().AcceptanceId != before.ActiveMainQuests.Single().AcceptanceId &&
                f.Prepare(1,2).Plan is null,"重接使用新身份和当前剩余材料，不复用旧批量资格");
        }
        using (var f = Make(batch,materialCount: 7))
        {
            var before = f.Read(); var plan = f.Prepare(1,2).Plan!;
            f.Sql("CREATE TRIGGER batch_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'batch rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Repository.CommitQuestCompletion(f.Owner,plan),"批量回执末段失败");
            That(Same(before,f.Read()) && f.Receipts == 0,"整批材料、奖励、经验、金币、UID和完成位一起回滚");
        }
        using (var f = Make(seeking,materialCount: 7))
            That(f.Prepare(1,2).Plan is null,"普通一次性寻物不因有足够材料变成批量任务");
        var repeat = empty with { Grade = "[normaly repeat]", Rewards = Rows(2058,2) };
        using (var f = Make(repeat, materialCount: 0))
        {
            f.Change(100,QuestLifecycleAction.Condition,new(null,0,0));
            var firstPlan = f.Prepare(ushort.MaxValue).Plan!;
            var first = f.Repository.CommitQuestCompletion(f.Owner,firstPlan);
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(first.Character.CompletedQuests.Contains(100) && QuestEligibility.Visible(first.Character,f.Catalog).Contains(100),
                "重复任务完成后仍可展示及重新接取，不删除历史领奖回执");
            f.Sql("CREATE TRIGGER repeat_accept_abort AFTER INSERT ON character_quest_lifecycle_receipts BEGIN SELECT RAISE(ABORT,'repeat accept rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Change(100,QuestLifecycleAction.Accept), "重接时清完成投影末段失败");
            That(Same(first.Character,f.Read()) && f.Receipts == 1, "重接失败完整回滚旧完成位与活动槽");
            f.Sql("DROP TRIGGER repeat_accept_abort");
            var accepted = f.Change(100,QuestLifecycleAction.Accept)!.Character;
            That(!accepted.CompletedQuests.Contains(100) && accepted.ActiveMainQuests.Single().AcceptanceId != first.AcceptanceId && f.Receipts == 1,
                "重接原子清当前完成投影并产生新接取身份，历史奖励及回执保留");
            var stale = f.Repository.CommitQuestCompletion(f.Owner,firstPlan);
            That(stale is { Duplicate: true, ProjectionCurrent: false } && Same(accepted,f.Read()),
                "旧接取回执重放仅读取持久结果，不能推进或清除新接取");
            f.Change(100,QuestLifecycleAction.Condition,new(null,0,0));
            var second = f.Finish(ushort.MaxValue).Receipt!.Commit;
            That(second.AcceptanceId != first.AcceptanceId && f.Receipts == 2 &&
                second.Character.Inventory.Items.Single(row => row.Core.ItemId == 2058).Core.Count == 4,
                "每次重复接取独立领奖，按完整次数累计真实资产");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(second.Character,f.Session.Selected!) && QuestEligibility.Visible(f.Session.Selected!,f.Catalog).Contains(100),
                "重复任务的完成位、资产与可接资格重登恢复");
        }
        var daily = repeat with { Grade = "[daily]" };
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,daily,materialCount: 0,useCycles: true))
        {
            var day = QuestCycle.Day(DateTimeOffset.UtcNow);
            That(f.Read().QuestCycleDay == day && f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow) is null,
                "选角装载当前持久游戏日；同周期刷新无SQL提交和revision变化");
            f.Change(100,QuestLifecycleAction.Condition,new(null,0,0));
            var plan = f.Prepare(ushort.MaxValue).Plan!;
            f.Sql("CREATE TRIGGER daily_reward_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'daily reward rollback'); END");
            var before = f.Read();
            TestCheck.Reject<SqliteException>(() => f.Repository.CommitQuestCompletion(f.Owner,plan), "每日领奖末段SQL失败");
            That(Same(before,f.Read()) && f.Read().QuestDailyClaims.Count == 0 && f.Receipts == 0,
                "每日领取周期与物品、完成位、回执同事务回滚");
            f.Sql("DROP TRIGGER daily_reward_abort");
            var finished = f.Repository.CommitQuestCompletion(f.Owner,plan).Character;
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(finished.QuestDailyClaims.GetValueOrDefault(100) == day &&
                !QuestEligibility.Visible(finished,f.Catalog).Contains(100) && f.Change(100,QuestLifecycleAction.Accept)!.Commit is null && f.Receipts == 1,
                "每日领取持久化当前周期，完成后本日禁止重接与再次发奖");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(finished,f.Session.Selected!), "每日限领重登保持，不以登录重置");
            f.Sql("CREATE TRIGGER daily_roll_abort AFTER DELETE ON character_completed_quests BEGIN SELECT RAISE(ABORT,'daily roll rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1)), "每日切换删除投影失败");
            That(Same(finished,f.Read()) && Same(finished,f.Session.Selected!), "切日失败回滚周期与完成投影，保留在线原快照");
            f.Sql("DROP TRIGGER daily_roll_abort");
            var rolled = f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1))!;
            That(rolled.Character.CompletedQuests.Length == 0 && rolled.Character.QuestDailyClaims.Count == 0 &&
                rolled.Character.ActiveMainQuests.Length == 0 && f.Receipts == 1 &&
                System.Text.Json.JsonSerializer.Serialize(finished.Inventory) == System.Text.Json.JsonSerializer.Serialize(rolled.Character.Inventory) && f.Session.CanPublishQuestCycle(rolled),
                "切日只撤销过期每日完成投影，原物品、领奖回执和真实活动槽保持");
            That(f.Repository.CommitQuestCompletion(f.Owner,plan) is { Duplicate: true, ProjectionCurrent: false } && f.Receipts == 1,
                "跨周期旧领奖回执保留可读取，但不能重发资产或投影为当前领取");
        }
        var accountQuest = empty with { Definition = empty.Definition! with { AccountQuest = true }, Rewards = Rows(2058,1) };
        using (var f = Make(accountQuest, materialCount: 0))
        {
            var alternate = f.Repository.Create(f.Owner,new("Alternate",towns.Room,towns.InitialReturn) { ActiveMainQuests = [new(0,100,1)] });
            f.Change(100,QuestLifecycleAction.Condition,new(null,0,0));
            var before = f.Read();
            f.Sql("CREATE TRIGGER account_claim_abort AFTER INSERT ON account_completed_quests BEGIN SELECT RAISE(ABORT,'account claim rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue), "账号任务共享完成末段失败");
            That(Same(before,f.Read()) && Same(alternate,f.Repository.LoadCharacter(f.Owner,1)) && f.Receipts == 0,
                "账号完成、领取角色奖励、其他角色同任务槽与所有revision一次回滚");
            f.Sql("DROP TRIGGER account_claim_abort");
            var finished = f.Finish(ushort.MaxValue).Receipt!.Commit;
            var other = f.Repository.LoadCharacter(f.Owner,1);
            That(finished.Character.AccountCompletedQuests.SequenceEqual([100]) && other.AccountCompletedQuests.SequenceEqual([100]) &&
                other.CompletedQuests.Length == 0 && other.ActiveMainQuests.Length == 0 && other.Revision == alternate.Revision + 1 &&
                finished.Character.Inventory.Items.Single(row => row.Core.ItemId == 2058).Core.Count == 1 && other.Inventory.Items.Length == 0,
                "账号完成共享至其他角色；关联活动槽原子清除，奖励只发真实领取角色");
            f.Session.ReturnToRoster(); f.Session.Select(1); f.Session.MarkReady();
            That(!QuestEligibility.Visible(f.Session.Selected!,f.Catalog).Contains(100) &&
                f.Change(100,QuestLifecycleAction.Accept)!.Commit is null && f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 1,
                "换角色不能重接或再领账号任务；当前角色没有被伪造个人完成记录");
            var colliding = empty with { Id = 101, Definition = empty.Definition! with { AccountCollisions = [100] } };
            That(!QuestEligibility.Evaluate(other,colliding,f.Catalog).Eligible &&
                DfoServer.Network.Builders.TownPackets.CompletedQuests(other)[104] == 1,
                "账号冲突使用共享完成位，选角完成表投影账号记录");
        }
        var accountDaily = accountQuest with { Grade = "[daily]" };
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,accountDaily,materialCount: 0,useCycles: true))
        {
            var alternate = f.Repository.Create(f.Owner,new("DailyAlt",towns.Room,towns.InitialReturn));
            f.Change(100,QuestLifecycleAction.Condition,new(null,0,0));
            var after = f.Finish(ushort.MaxValue).Receipt!.Commit.Character;
            f.Session.ReturnToRoster(); f.Session.Select(1); f.Session.MarkReady();
            That(f.Session.Selected!.AccountQuestDailyClaims.GetValueOrDefault(100) == after.QuestCycleDay &&
                f.Change(100,QuestLifecycleAction.Accept)!.Commit is null && f.Receipts == 1,
                "每日账号任务同周期跨角色共享限领，不因另一角色选角清除");
            var rolled = f.Session.AdvanceQuestCycle(DateTimeOffset.UtcNow.AddDays(1))!.Character;
            That(rolled.AccountCompletedQuests.Length == 0 && rolled.AccountQuestDailyClaims.Count == 0 &&
                f.Repository.LoadCharacter(f.Owner,0).CompletedQuests.SequenceEqual([100]) && f.Receipts == 1,
                "账号切日撤销共享日完成位，离线角色自己的旧周期投影在它选角时处理，旧回执保持");
        }
        Console.WriteLine($"PASS {Count - checks} 普通任务、父子、转职、每日/重复与账号共享原子交付、SQLite回滚与恢复断言");

        // 本地工厂捕获共享只读资源，避免每个事务用例再加载PVF。
        Fixture Make(StoryQuest quest, int materialCount = 5, bool locked = false, bool fullEquipment = false, bool loseResult = false) =>
            new(towns, characters, learning, growth, parameters, items, quest, materialCount, locked, fullEquipment, loseResult);
    }

    internal sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-ordinary-quest-" + Guid.NewGuid().ToString("N"));
        readonly ItemResources items; readonly CharacterGrowthCatalog growth; readonly QuestRewardParameterCatalog parameters;
        public CharacterRepository Repository { get; }
        readonly AccountOwners owners = new(); public AccountOwners.Lease Owner { get; }
        public QuestCatalog Catalog { get; } public GameSession Session { get; }
        public QuestCompletionRepositoryTests.LostResultRepository? Lost { get; }
        public Fixture(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning, CharacterGrowthCatalog growth,
            QuestRewardParameterCatalog parameters, ItemResources items, StoryQuest quest, int materialCount = 5, bool locked = false,
            bool fullEquipment = false, bool loseResult = false, StoryQuest[]? definitions = null, ActiveMainQuest[]? active = null,
            Func<NewCharacter,NewCharacter>? configure = null, bool useCycles = false)
        {
            this.items = items; this.growth = growth; this.parameters = parameters;
            Catalog = new((definitions ?? [quest]).ToDictionary(row => row.Id));
            Repository = new(Path.Combine(root, "game.db"), Catalog); Owner = owners.Acquire("ordinary-quest", () => { });
            if ((definitions ?? [quest]).Any(row => row.Definition?.AccountQuest == true))
                new AuthenticationService(new AuthenticationRepository(Path.Combine(root,"game.db"))).Create("ordinary-quest","fixture-password");
            var candidate = CharacterCreation.Prepare("Ordinary", characters.Get(0), towns.Room, towns.InitialReturn, learning.Get);
            if (configure is not null) candidate = configure(candidate);
            var inventory = candidate.Inventory.Items.ToBuilder();
            if (materialCount != 0) inventory.Add(new(0, new(InventoryListType.Main, 177),
                InventoryRewardGrantService.Create(items.Items.Get(10165055), materialCount) with { EquipmentLockId = locked ? (byte)1 : (byte)0 }));
            if (fullEquipment)
            {
                var range = ItemSlotBoundService.Range(ItemKind.Equipment, candidate.Inventory.Expansion);
                for (short slot = range.Start; slot <= range.End; slot++)
                    inventory.Add(new(0, new(InventoryListType.Main, slot), InventoryRewardGrantService.Create(items.Items.Get(10403))));
            }
            Repository.Create(Owner, candidate with { ActiveMainQuests = active ?? [new(0, (ushort)quest.Id, 1)], Inventory = candidate.Inventory with { Items = inventory.ToImmutable() } });
            Lost = loseResult ? new(Repository) : null;
            Session = new(Lost is null ? Repository : Lost, towns, Owner, questCatalog: useCycles ? Catalog : null); Session.Select(0); Session.MarkReady();
        }
        public Character Read() => Repository.LoadCharacter(Owner, 0);
        public QuestCompletionPreparation Prepare(ushort selector, ushort multiplier = 1) => QuestCompletionPlan.Prepare(Read(), Catalog.Get(100), selector,
            Catalog, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth, DateTimeOffset.UtcNow,multiplier: multiplier);
        public QuestLifecycleReceipt? Change(ushort id, QuestLifecycleAction action, QuestClientCondition? condition = null) =>
            Session.ChangeQuest(id, action, Catalog, items.Items, items.GoldLimits, items.EpicPieces, condition);
        public QuestFinishResult Finish(ushort selector, ushort questId = 100, ushort multiplier = 1) => Session.FinishQuest(questId, selector, Catalog, parameters,
            items.Items, items.GoldLimits, items.EpicPieces, growth,multiplier);
        public SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + Path.Combine(root, "game.db") + ";Pooling=False"); db.Open(); return db; }
        public long Receipts { get { using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT count(*) FROM character_quest_completion_receipts"; return (long)c.ExecuteScalar()!; } }
        public void Sql(string sql) { using var db = Open(); using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        public void Dispose() { Session.End(); Owner.Dispose(); Directory.Delete(root, true); }
    }
}
