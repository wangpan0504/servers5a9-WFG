using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.ExpertJob;
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
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items, QuestCatalog quests)
    {
        int checks = Count;
        var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf); var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        QuestAntonUnlockTests.RunResources(towns, characters, items, learning, growth, parameters, quests);
        QuestCashRewardTests.Run(towns,characters,items,learning,growth,parameters,quests);
        QuestMailDeclarationTests.RunResources(quests);
        QuestAutoDailyTests.RunCompletionResources(pvf,items);
        QuestEligibilityTests.RunFirstNpcResources(quests);
        PvpTrainingProgressTests.RunResources(quests);
        PvpTrainingManualTests.Run(towns,characters,items,learning,growth,parameters,quests);
        QuestAccountSynchronizationTests.Run(towns,characters,items,learning,growth,parameters,quests);
        EpicRoadQuestSessionTests.RunResources(pvf,items);
        QuestLegacyAwakeningTests.Run(towns,pvf,characters,items,quests,learning,growth,parameters);
        var bloodAwakening = quests.Get(990);
        using (var legacy = new Fixture(towns,characters,learning,growth,parameters,items,bloodAwakening,
            materialCount: 0,active: [new(0,990,0)],definitions: quests.All.ToArray(),configure: source => source with
            {
                Level = 50, GrowType = 3, CompletedQuests = [989],
                InitialLearning = SkillPointLedger.Synchronize(source.Job,3,50,source.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(50) },learning),
                Inventory = source.Inventory with { Items = source.Inventory.Items.Add(new(0,new(InventoryListType.Main,177),
                    InventoryRewardGrantService.Create(items.Items.Get(10089420),1))) }
            }))
        {
            var before = legacy.Read();
            foreach (var invalid in new[] { bloodAwakening with { RewardType = "[unknown]" },
                bloodAwakening with { SubType = 999 }, bloodAwakening with { Attributes = ["unknown"] },
                bloodAwakening with { Definition = bloodAwakening.Definition! with
                    { Fields = bloodAwakening.Definition.Fields.Add(new("send postal",[],[])) } } })
            {
                var rejected = QuestCompletionPlan.Prepare(before,invalid,ushort.MaxValue,legacy.Catalog,parameters,
                    items.Items,items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow);
                That(rejected.Outcome == QuestCompletionOutcome.Rejected && rejected.Reason.Length > 0 &&
                    rejected.Reason != "任务需要尚未具备的周期、特殊奖励或附属交付规则" &&
                    Same(before,legacy.Read()) && legacy.Receipts == 0,"未知交付声明保持拒绝并提供具体原因");
            }
            legacy.Sql("CREATE TRIGGER awakening_receipt_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'awakening rollback'); END");
            TestCheck.Reject<SqliteException>(() => legacy.Finish(ushort.MaxValue,990),"觉醒寻物交付末段失败");
            That(Same(before,legacy.Read()) && legacy.Receipts == 0,"失败不吞精气、不发金币或写完成位");
            legacy.Sql("DROP TRIGGER awakening_receipt_abort");
            var result = legacy.Finish(ushort.MaxValue,990);
            That(result.Outcome == QuestFinishOutcome.Committed && legacy.Read().CompletedQuests.Contains(990) &&
                result.Receipt!.Commit.Consumed is [{ ItemId: 10089420, Count: 1 }] &&
                result.Receipt.Commit.DirectGoldAmount > 0 && legacy.Read().GrowType == 3,
                "767真实非剧情狱血魔神第三步寻物交付：" + result.Reason);
            var saved = legacy.Read();
            That(legacy.Finish(ushort.MaxValue,990).Outcome == QuestFinishOutcome.AlreadyCompleted &&
                Same(saved,legacy.Read()) && legacy.Receipts == 1,"重复点击不重复扣精气或发金币");
            legacy.Session.ReturnToRoster(); legacy.Session.Select(0); legacy.Session.MarkReady();
            That(Same(saved,legacy.Read()) && legacy.Read().CompletedQuests.Contains(990),"重选保留完成与奖励事实");
        }
        var marriage = quests.Get(20743);
        That(marriage is { Condition: "[meet npc]", SubType: -1, Difficulty: null, IntData: [20013] },
            "704爱情的升华保留真实会面及缺省difficulty声明");
        using (var missingDifficulty = new Fixture(towns, characters, learning, growth, parameters, items, marriage,
            materialCount: 0, active: [new(0, 20743, 0)], configure: source => source with
            { Level = 29, InitialLearning = SkillPointLedger.Synchronize(source.Job, source.GrowType, 29,
                source.InitialLearning with { Experience = growth.GetLevelStartExperience(29) }, learning) }))
        {
            var before = missingDifficulty.Read();
            var invalid = QuestCompletionPlan.Prepare(before, marriage with { Difficulty = "unknown" }, ushort.MaxValue,
                missingDifficulty.Catalog, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth, DateTimeOffset.UtcNow);
            That(invalid.Outcome == QuestCompletionOutcome.Rejected && missingDifficulty.Receipts == 0,
                "显式未知difficulty仍拒绝且不发婚礼奖励");
            var result = missingDifficulty.Finish(ushort.MaxValue, 20743);
            That(result.Outcome == QuestFinishOutcome.Committed && result.Receipt!.Commit.ExperienceReward is
                { DifficultyKey: "", DifficultyWeight: 100 } && result.Receipt.Commit.Rewards.Single() is
                { GrantedCount: 1, Item.Core.ItemId: 10003038 } && missingDifficulty.Read().CompletedQuests.Contains(20743),
                "爱情的升华真实已完成会面可交付且原奖励只发一次：" + result.Reason);
            var saved = missingDifficulty.Read();
            _ = missingDifficulty.Finish(ushort.MaxValue, 20743);
            That(Same(saved, missingDifficulty.Read()) && missingDifficulty.Receipts == 1, "重复交付不重发奖励");
        }
        foreach (int id in new[] { 2109, 2110, 20743 })
            That(quests.Get(id).Difficulty is null && parameters.TryGetDifficultyWeight(null, out int weight) && weight == 100,
                id + "普通会面使用统一缺省难度，不需要独立白名单");
        QuestPetRewardTests.Run(towns, characters, items, learning, growth, parameters);
        QuestHarlemContinuationTests.Run(towns,pvf,characters,items,quests);
        QuestGoldMailTests.Run(towns, characters, items, learning, growth, parameters);
        QuestInventoryPendingTests.Run(towns, characters, items, learning, growth, parameters);
        QuestTownMutationPendingTests.Run(towns, characters, items, learning, growth, parameters);
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
        var rental = quests.Get(8581);
        var rentalCoverage = QuestCoverage.Scan(new(new[] { rental }.ToDictionary(row => row.Id)), items.Items, items.EpicPieces, parameters);
        That(rentalCoverage.Quests.Single().ImplementedPath,
            "真实8581的已支持租赁合同不被通用subtype0诊断误报");
        foreach (var unsupportedRental in new[] { rental with { Id = 8582 }, rental with { IntData = [1,0,2] },
            rental with { Definition = rental.Definition! with { AccountQuest = false } } })
        {
            var unsupportedCoverage = QuestCoverage.Scan(new(new[] { unsupportedRental }.ToDictionary(row => row.Id)),
                items.Items, items.EpicPieces, parameters).Quests.Single();
            That(!unsupportedCoverage.ImplementedPath && unsupportedCoverage.Restrictions.Contains("目标子类型尚未接入"),
                "相邻任务、未知租赁事件和非账号custom仍保留未实现子类型诊断");
        }
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
            var saved = f.Read(); var original = f.Lost!.FirstPlan!;
            That(f.Finish(0).Outcome == QuestFinishOutcome.Rejected && f.Receipts == 1, "不同选择不能冒充原pending请求");
            That(f.Change(100,QuestLifecycleAction.Accept) is null && f.Change(100,QuestLifecycleAction.Giveup) is null &&
                f.Change(100,QuestLifecycleAction.Condition,new(null,0,0)) is null &&
                ReferenceEquals(before,f.Session.Selected) && Same(saved,f.Read()) && f.Receipts == 1 && f.Lost.Calls == 1,
                "未决领奖期间31/32/33入口不能覆盖状态或创建另一生命周期事务");
            var recovered = f.Finish(1);
            That(recovered.Receipt?.Commit is { Duplicate: true, ProjectionCurrent: true } && f.Lost!.SamePlan && f.Receipts == 1 &&
                ReferenceEquals(recovered.Receipt.Commit.Character,f.Session.Selected) && Same(saved,recovered.Receipt.Commit.Character) &&
                recovered.Receipt.Commit.AcceptanceId == original.AcceptanceId && f.Lost.Calls == 2 && f.Session.CanPublishQuestCompletion(recovered.Receipt),
                "相同选择恢复原冻结结果，不重新消耗或抽取奖励");
            That(f.Finish(1).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 1, "安装后的重复交付正常拒绝");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(f.Session.Selected!, f.Read()) && f.Finish(1).Outcome == QuestFinishOutcome.AlreadyCompleted, "重选和进程仓储重开使用持久结果");
        }
        var pendingAcceptance = QuestEligibilityTests.Quest(101);
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,seeking,
            definitions: [seeking,pendingAcceptance],loseLifecycleResult: true))
        {
            var before = f.Session.Selected!;
            TestCheck.Reject<IOException>(() => f.Change(101,QuestLifecycleAction.Accept),"接取提交返回丢失时另一任务已经具备领奖材料");
            var saved = f.Read();
            string operation;
            using (var db = f.Open())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT operation_id FROM character_quest_lifecycle_receipts";
                operation = (string)command.ExecuteScalar()!;
            }
            That(f.Finish(1).Receipt is null && f.Receipts == 0 && f.Lost!.Calls == 0 &&
                ReferenceEquals(before,f.Session.Selected) && Same(saved,f.Read()),
                "未决接取期间34不能另开领奖提交或改变原在线快照");
            var recovered = f.Change(101,QuestLifecycleAction.Accept)!;
            That(recovered.Commit is { Duplicate: true, ProjectionCurrent: true } && f.Lost!.SameLifecyclePlan &&
                recovered.Commit.Plan.OperationId.ToString("N") == operation && Same(saved,recovered.Character) && f.Receipts == 0,
                "互斥拒绝后按原operation恢复接取一次，资产和活动槽等于已提交存档");
            That(f.Finish(1).Receipt is { Commit.Duplicate: false } && f.Receipts == 1 &&
                f.Read().ActiveMainQuests.Single().QuestId == 101,
                "接取恢复后允许原合法领奖，保留另一活动接取");
        }
        foreach (string change in new[] { "UPDATE characters SET revision=revision+1",
            "INSERT INTO character_completed_quests(character_id,quest_id) VALUES(1,999)" })
        using (var f = Make(seeking, loseResult: true))
        {
            var before = f.Session.Selected!;
            TestCheck.Reject<IOException>(() => f.Finish(1), "普通交付已提交后丢失返回值，随后持久状态变化");
            f.Sql(change);
            var saved = f.Read();
            var recovered = f.Finish(1);
            That(recovered is { Outcome: QuestFinishOutcome.Committed, Receipt.Commit: { Duplicate: true, ProjectionCurrent: false } } &&
                f.Lost!.SamePlan && ReferenceEquals(before,f.Session.Selected) &&
                Same(saved,recovered.Receipt!.Commit.Character) && !f.Session.CanPublishQuestCompletion(recovered.Receipt),
                "revision或同revision完成位变化后仅返回实际失效领奖回执，不安装Selected或发布成功");
            var retried = f.Finish(1);
            That(retried.Receipt?.Commit is { Duplicate: true, ProjectionCurrent: false } && f.Lost!.SamePlan &&
                ReferenceEquals(before,f.Session.Selected) && Same(saved,f.Read()) &&
                saved.Inventory.Items.Single(row => row.Core.ItemId == 10165055).Core.Value == 2 &&
                saved.Inventory.Items.Single(row => row.Core.ItemId == 2058).Core.Value == 5 && f.Receipts == 1,
                "失效领奖回执保留原pending精确重试，不重算、重复扣材料或发奖励");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(saved,f.Session.Selected!) && f.Finish(1).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 1,
                "重选恢复最新完成位及资产，不重放旧领奖增量");
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
        foreach (ulong left in new ulong[] { 5, 0 })
        {
            ulong cap = growth.GetCumulativeExperienceBoundary(95) - 1;
            var quest = meeting with { Attributes = [], DeclaredIgnoreLevelForExperience = 1 };
            using var f = new Fixture(towns,characters,learning,growth,parameters,items,quest,0,false,false,false,
                configure: candidate => candidate with { Level = 95, InitialLearning = candidate.InitialLearning with
                {
                    Experience = cap-left,
                    Trees = candidate.InitialLearning.Trees.Select(tree => tree with {
                        Sp = checked((ushort)(tree.Sp+growth.GetSpDelta(1,95))),
                        Tp = checked((ushort)(tree.Tp+growth.GetTpDelta(1,95))) }).ToArray()
                } });
            f.Change(100,QuestLifecycleAction.Condition,new(null,0,0));
            var plan = f.Prepare(ushort.MaxValue).Plan!;
            That(plan.ExperienceReward.Amount > left && plan.Growth.GainedExperience == 0 && plan.TargetLearning.Experience == cap - left,
                "满级任务保留原奖励额度，经验条保持在原值不再增长也不回退");
            var commit = f.Repository.CommitQuestCompletion(f.Owner,plan);
            var replay = f.Repository.CommitQuestCompletion(f.Owner,plan);
            That(commit.Character.Level == 95 && commit.Character.Learning.Experience == cap - left && commit.Character.CompletedQuests.Contains(100) &&
                commit.GainedExperience == 0 && replay.Duplicate && replay.GainedExperience == 0 && f.Receipts == 1 && Same(commit.Character,f.Read()),
                "满级零到账任务仍完成，重放及独立读库不多发经验");
        }
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
                f.Change(100,QuestLifecycleAction.Condition,new(null,0,3))?.Commit is null &&
                f.Change(100,QuestLifecycleAction.Condition,new(null,1,0))?.Commit is null && Same(unchanged,f.Read()),
                "问题仅接受匹配客户端普通回答通道和资源中的零基选项");
            That(f.Change(100,QuestLifecycleAction.Condition,new(null,0,1))!.Character.ActiveMainQuests.Single().ConditionState == 1 &&
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
            f.Change(100,QuestLifecycleAction.Accept); f.Change(100,QuestLifecycleAction.Condition,new(null,0,2)); var before = f.Read();
            f.Sql("CREATE TRIGGER question_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'question rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue),"回答回执末段失败");
            That(Same(before,f.Read()) && f.Read().QuestAnswers.Count == 0 && f.Receipts == 0,
                "任务回答、完成位和奖励同事务失败回滚，不能提前开放分支");
        }
        var actualQuestions = quests;
        AlbertRequestTests.Run(towns,characters,items,actualQuestions,learning,growth,parameters);
        BilmarkDeliveryTests.Run(towns,characters,items,actualQuestions,learning,growth,parameters);
        That(new[] {(3868,3),(3869,2),(6217,3),(6382,2),(6383,2)}.All(row =>
            QuestQuestionProgress.TryCount(actualQuestions.Get(row.Item1),out int count) && count == row.Item2) &&
            QuestQuestionProgress.OrdinaryChoice(actualQuestions.Get(3868),2,actualQuestions) &&
            !QuestQuestionProgress.OrdinaryChoice(actualQuestions.Get(6217),2,actualQuestions),
            "匹配PVF的五种问题选项完整保留；无普通后继的跳过选项不能冒充领奖");
        var seekMeet = empty with { Condition = "[seek n meet npc]", IntData = [10165055,2,10165055,1,2] };
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,seekMeet,materialCount: 0,active: []))
        {
            That(f.Change(100,QuestLifecycleAction.Accept)!.Character.ActiveMainQuests.Single().ConditionState == 513,
                "寻物会面初值分别保存材料不足和独立NPC通道，不以材料缺额作为条件计数");
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
        var research = actualQuestions.Get(3729);
        var researchMeet = actualQuestions.Get(3762);
        var researchMaterials = actualQuestions.Get(3763);
        That(research.IntData.SequenceEqual([3762,3763]) && researchMaterials.IntData.SequenceEqual([3033,200,3034,200,3035,200,3036,200,79]) &&
            QuestRelationProgress.SequentialParent(research,actualQuestions) && QuestCoverage.HasOrdinaryDeclarations(researchMaterials),
            "危险的研究真实父子定义保持原顺序、NPC79及全部800晶块材料");
        // 独立角色只准备城镇准入和已声明前置；目标父子仍从合法接取开始，不触碰现场存档。
        var researchCompleted = towns.AccessQuestIds.Append(3710).Where(id => id is not (3729 or 3762 or 3763)).Distinct().ToArray();
        var researchDone = researchCompleted.ToHashSet();
        var researchArea = towns.Areas.Values.First(area => area.Map(researchDone).Npcs.Any(npc => npc.Id == 79));
        var researchRegion = researchArea.Map(researchDone).Bounds.First(region => region.Conditions.Length == 0 && region.Quests.All(researchDone.Contains));
        var researchPosition = new Location(researchArea.Town,researchArea.Area,
            checked((ushort)(researchRegion.Bounds.X + researchRegion.Bounds.Width/2)),
            checked((ushort)(researchRegion.Bounds.Y + researchRegion.Bounds.Height/2)));
        using (var f = new Fixture(towns,characters,learning,growth,parameters,items,researchMaterials,materialCount: 0,active: [],
            definitions: [research,researchMeet,researchMaterials,actualQuestions.Get(3710)],resumeTown: true,configure: candidate => candidate with
            {
                Level = 81, Position = researchPosition, Return = researchPosition, CompletedQuests = researchCompleted,
                InitialLearning = SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,81,
                    candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(81) },learning)
            }))
        {
            That(QuestClientSceneProgress.DialogueNpc(f.Read(),79,researchMaterials,f.Catalog,towns),
                "危险的研究夹具沿城镇重连入口保留实际NPC79地图");
            var acceptedResearch = f.Change(3729,QuestLifecycleAction.Accept);
            That(acceptedResearch?.Commit is not null,"危险的研究真实父任务合法接取：" + acceptedResearch?.Reason);
            That(f.Change(3763,QuestLifecycleAction.Accept)?.Commit is null,
                "危险的研究未完成会面子任务不能跳接材料步骤");
            f.Change(3762,QuestLifecycleAction.Accept);
            f.Change(3762,QuestLifecycleAction.Condition,new(null,0,0));
            That(f.Finish(ushort.MaxValue,3762).Receipt is not null &&
                f.Change(3763,QuestLifecycleAction.Accept)?.Character.ActiveMainQuests.Single(row => row.QuestId == 3763).ConditionState == 513,
                "真实会面交付后可接800材料子任务，两通道初值513不会被误判为未接取");
            That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(3033,200),new(3034,200),new(3035,199),new(3036,200)],items.Items,items.GoldLimits,items.EpicPieces)).Accepted,
                "隔离库按普通奖励入口准备四种真实钱包材料");
            var insufficient = f.Read();
            That(f.Change(3763,QuestLifecycleAction.Condition,new(0x20,0,0))?.Commit is null &&
                f.Finish(ushort.MaxValue,3763).Outcome == QuestFinishOutcome.Rejected && Same(insufficient,f.Read()),
                "四种晶块缺一件仍拒绝会面完成及交付，不按材料总数或客户端Boolean放行");
            f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(3035,1)],items.Items,items.GoldLimits,items.EpicPieces));
            That(f.Read().ActiveMainQuests.Single(row => row.QuestId == 3763).ConditionState == 512 &&
                f.Change(3763,QuestLifecycleAction.Condition,new(0x20,0,0))?.Commit is not null,
                "全部800晶块足额先清材料，实际NPC事件才清第二通道");
            var before = f.Read();
            f.Sql("CREATE TRIGGER research_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'research rollback'); END");
            TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue,3763),"真实大材料子任务回执末段失败");
            That(Same(before,f.Read()) && f.Receipts == 1,"四钱包材料、子完成与父归零同事务回滚");
            f.Sql("DROP TRIGGER research_abort");
            var completedResearch = f.Finish(ushort.MaxValue,3763).Receipt!.Commit;
            That(completedResearch.Consumed.Length == 4 && completedResearch.Consumed.All(row => row.Count == 200) &&
                completedResearch.Currencies.Length == 0 && completedResearch.Character.ActiveMainQuests is [{ QuestId: 3729, ConditionState: 0 }] &&
                new[] {3033,3034,3035,3036}.All(id => CurrencyWallet.Get(f.Read().Inventory,CurrencyWallet.RewardKind(id)!.Value) == 0),
                "真实子交付完整消费四种各200晶块并推进父归零，不截断为511或重复扣除");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Read().ActiveMainQuests is [{ QuestId: 3729, ConditionState: 0 }] &&
                f.Finish(ushort.MaxValue,3729).Receipt?.Commit.Rewards is [{ Item.Core.ItemId: 10164775, GrantedCount: 1 }],
                "重登保留大材料子完成及父进度，父只交付真实资源声明的废旧电池");
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
                { Experience = growth.GetLevelStartExperience(15), BonusSp = 137, BonusTp = 11,
                    ExpertContractExpiresAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() }, learning);
            foreach (byte context in new byte[] { 0, 1 })
            {
                var paid = SkillLearning.Plan(candidate.Job, 0, 15, state, new(context,0,[new(5,false,(byte)(context + 2))]), learning);
                That(paid.Success, "转职前两树分别购买技能，保留真实点数来源");
                state = paid.State!;
            }
            state = state with { Trees = state.Trees.Select(tree => tree with
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
                after.Learning.Experience == before.Learning.Experience &&
                after.Learning.ExpertContractExpiresAt == before.Learning.ExpertContractExpiresAt &&
                Same(before with { Inventory = after.Inventory },before), "正常转职同事务保留经验、契约、额外点数和所有物品");
            VerifyReset(after, 1);
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(after,f.Session.Selected!) && f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 1,
                "转职重登恢复完整领域状态，重复请求不重置技能或发奖");
            var actual = quests;
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

            var highLevel = SkillPointLedger.Synchronize(0,0,95,before.Learning with
                { Experience = growth.GetLevelStartExperience(95) },learning,learning.ExpertContractLevels);
            var tpSkill = PvfScript.List(pvf.ReadText("skill/swordmanskill.lst")).Keys.Select(id => learning.Get(0,(ushort)id))
                .First(skill => skill.SupportsTpLearning && skill.GetMaximum(0) >= 2 && skill.Prerequisites!.Count == 0);
            foreach (byte context in new byte[] { 0, 1 })
            {
                var paid = SkillLearning.Plan(0,0,95,highLevel,new(context,0,[new(tpSkill.SkillId,false,2)]),learning);
                That(paid.Success && paid.State!.Trees[context].Tp < highLevel.Trees[context].Tp, "延后转职的两树均真实消耗TP");
                highLevel = paid.State!;
            }
            var reset = QuestProfessionReward.Prepare(before with { Level = 95, Learning = highLevel },1,95,highLevel,learning,DateTimeOffset.UtcNow);
            VerifyReset(before with { Level = 95, GrowType = 1, Learning = reset }, 1);
        }
        foreach (byte branch in new byte[] { 1, 2, 3, 4, 5 })
        {
            NewCharacter LadyBeforeTransfer(NewCharacter _)
            {
                var candidate = CharacterCreation.Prepare("Ordinary",characters.Get(11),towns.Room,towns.InitialReturn,learning.Get);
                var state = SkillPointLedger.Synchronize(11,0,15,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(15), BonusSp = 137, BonusTp = 11 },learning);
                foreach (byte context in new byte[] { 0, 1 })
                {
                    var bought = SkillLearning.Plan(11,0,15,state,new(context,0,[new(31,false,1)]),learning);
                    That(bought.Success, "女鬼剑未转职两树可按真实上限学习唤魔蛇腹剑");
                    state = bought.State!;
                }
                return candidate with { Level = 15, InitialLearning = state,
                    Skills = state.Trees[0].Skills.Select(skill => new CharacterSkill(skill.Id,skill.Level)).ToArray() };
            }
            using var f = new Fixture(towns,characters,learning,growth,parameters,items,profession with { Rewards = Rows(branch) },
                materialCount: 0,configure: LadyBeforeTransfer);
            f.Change(100,QuestLifecycleAction.Condition,new(null,0,0));
            That(f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.Committed, "女鬼剑正常首次转职 " + branch);
            var after = f.Read();
            VerifyReset(after,branch);
            That(after.Learning.Trees.All(tree => tree.Skills.Any(skill => skill.Id == 31) == (branch == 3)),
                "唤魔蛇腹剑仅由契魔者CHR重新赠送，其余分支不残留被动 " + branch);
        }
        void VerifyReset(Character character, byte branch)
        {
            var free = SkillPointLedger.FreeBaseline(character.Job,branch,character.Level,learning,
                learning.ActiveExpertContractLevels(character.Learning.ExpertContractExpiresAt,DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            That(character.Learning.Trees.All(tree => tree.Sp == learning.TotalSp(character.Level) + 137 &&
                tree.Tp == learning.TotalTp(character.Level) + 11 && tree.Skills.Count == free.Count &&
                tree.Skills.All(skill => free.GetValueOrDefault(skill.Id) == skill.Level && skill.Command.Length == 0) &&
                tree.Combos.Count == 0), "首次转职两树仅有CHR及固定赠技，清指令/连招并全额返还已用SP/TP");
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

    public static void RunQuestionState(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        SkillLearningCatalog learning)
    {
        int checks=Count;
        var growth=global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning);
        var parameters=global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var actual=global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf).Get(3868);
        // 复用真实三选项，NPC/等级和前置映射到已有合成场景；不将此测试当作Windows对话验收。
        var question=actual with {MinimumLevel=1,Npc=2,IntData=[1],Prerequisites=[],
            Definition=actual.Definition! with {PrerequisiteGroups=[]}};
        var branches=Enumerable.Range(0,3).Select(answer=>QuestEligibilityTests.Quest(100+answer) with
            {Definition=new([]) {RequiredAnswers=[new(3868,answer)]}}).ToArray();
        foreach(byte choice in new byte[]{0,1,2})
        {
            using var f=new Fixture(towns,characters,learning,growth,parameters,items,question,materialCount:0,
                active:[],definitions:[question,..branches],configure:source=>source with {Level=2,
                    InitialLearning=SkillPointLedger.Synchronize(source.Job,source.GrowType,2,
                        source.InitialLearning with {Experience=growth.GetLevelStartExperience(2)},learning)});
            f.Change(3868,QuestLifecycleAction.Accept);
            var unanswered=f.Read();
            That(unanswered.ActiveMainQuests.Single().ConditionState==QuestQuestionProgress.Unanswered &&
                QuestCompletionPlan.Prepare(unanswered,question,ushort.MaxValue,f.Catalog,parameters,items.Items,
                    items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow).Plan is null && !unanswered.QuestAnswers.ContainsKey(3868),
                "已接取未回答与未接取/已回答/已完成分别保存，不能默认完成阵营选择");
            var accepted=DfoServer.Network.Builders.QuestPackets.Accepted(3868,unanswered.ActiveMainQuests.Single().ConditionState,[]);
            var active=DfoServer.Network.Builders.QuestPackets.ActiveList(unanswered.ActiveMainQuests);
            var selected=DfoServer.Network.Builders.TownPackets.Select(unanswered);
            That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(accepted.AsSpan(3))==1 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(active.AsSpan(6))==1 &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(selected.AsSpan(24))==1,
                "接取ACK31、跳过刷新574及重选ACK4均投影为已接取进行中，不输出不存在哨兵");
            That(QuestQuickClearPlan.TryCreateManual(unanswered,Guid.NewGuid(),f.Catalog,
                System.Collections.Immutable.ImmutableDictionary<int,byte>.Empty,out var pending,out _) && pending!.PendingQuestions.Contains(3868),
                "已接取未回答的问题仍可进入手册批量选择，不因活动状态丢掉真实选项");
            f.Session.ReturnToRoster();f.Session.Select(0);f.Session.MarkReady();
            That(f.Read().ActiveMainQuests.Single().ConditionState==QuestQuestionProgress.Unanswered &&
                QuestCompletionPlan.Prepare(f.Read(),question,ushort.MaxValue,f.Catalog,parameters,items.Items,
                    items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow).Plan is null,"重选不把客户端进行中1写回成第二个答案");
            var wrongNpc = new QuestCatalog(new[] { question with { Npc = 99999, IntData = [2] } }.Concat(branches).ToDictionary(row => row.Id));
            That(f.Session.ChangeQuest(3868, QuestLifecycleAction.Condition, wrongNpc, items.Items, items.GoldLimits,
                items.EpicPieces, new(null, 0, choice))?.Commit is null &&
                f.Read().ActiveMainQuests.Single().ConditionState == QuestQuestionProgress.Unanswered,
                "int data命中当前NPC也不能代替独立npc index字段");
            f.Change(3868,QuestLifecycleAction.Condition,new(null,0,choice));
            That(f.Read().ActiveMainQuests.Single().ConditionState==choice &&
                QuestQuestionProgress.ClientState(choice)==choice,"选择0/1/2继续保留原零基答案");
            var finished=f.Finish(ushort.MaxValue,3868).Receipt!.Commit.Character;
            That(finished.QuestAnswers[3868]==choice && !finished.ActiveMainQuests.Any(row=>row.QuestId==3868) &&
                DfoServer.Network.Builders.TownPackets.CompletedQuests(finished)[4+3868]==choice+1 &&
                branches.Count(branch=>QuestEligibility.Evaluate(finished,branch,f.Catalog).Eligible)==1,
                "完成后清接取槽、记录真实答案，只开放被选择的阵营");
        }
        Console.WriteLine($"PASS {Count-checks} 问答未接取/未回答/已回答/完成及三种投影状态断言");
    }

    public static void RunExpertJob(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items, QuestCatalog quests)
    {
        int checks = Count;
        var learning = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, learning);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var scripts = items.ExpertJobs;
        ExpertJobLearningTests.Run(towns, items, scripts);
        ExpertJobChainTests.Run(towns, characters, learning, growth, parameters, items, quests);
        NewCharacter Level20(NewCharacter source) => source with
        {
            Level = 20,
            InitialLearning = SkillPointLedger.Synchronize(source.Job, source.GrowType, 20,
                source.InitialLearning with { Experience = growth.GetLevelStartExperience(20) }, learning)
        };
        foreach (var sample in new (int Id, byte Type)[] { (2702, 1), (2708, 2), (2710, 3), (2712, 4) })
        {
            var quest = quests.Get(sample.Id);
            That(QuestExpertJobReward.Type(quest) == sample.Type && quest.Difficulty is null &&
                quest.Condition == "[seeking]" && quest.IntData.SequenceEqual([3037, 100]),
                "真实副职业第一步消耗 100 个无色小晶块，奖励类型 " + sample.Type);
            var covered = QuestCoverage.Scan(new(new[] { quest }.ToDictionary(row => row.Id)), items.Items, items.EpicPieces, parameters).Quests.Single();
            That(covered.ImplementedPath, sample.Id + " 覆盖：" + string.Join("；", covered.Restrictions));
            using var f = new Fixture(towns, characters, learning, growth, parameters, items, quest, materialCount: 0, configure: Level20);
            var missing = QuestCompletionPlan.Prepare(f.Read(), quest, ushort.MaxValue, f.Catalog, parameters, items.Items,
                items.GoldLimits, items.EpicPieces, growth, DateTimeOffset.UtcNow);
            That(missing.Plan is null && f.Receipts == 0, "没有无色小晶块不能完成 " + sample.Id + "：" + missing.Reason);
            That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character, [new(3037, 100)],
                items.Items, items.GoldLimits, items.EpicPieces)).Accepted, "测试无色小晶块走现有钱包发放");
            var funded = f.Read();
            var malformed = QuestCompletionPlan.Prepare(funded, quest with { Rewards = [new(0, 5, null)] }, ushort.MaxValue, f.Catalog,
                parameters, items.Items, items.GoldLimits, items.EpicPieces, growth, DateTimeOffset.UtcNow);
            That(malformed.Plan is null && CurrencyWallet.Get(f.Read().Inventory, CurrencyKind.ClearCube) == 100,
                "类型超出 1–4 时不扣无色小晶块");
            if (sample.Id == 2702)
            {
                string before = System.Text.Json.JsonSerializer.Serialize(f.Read());
                f.Sql("CREATE TRIGGER fail_qualification AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'qualification rollback'); END");
                TestCheck.Reject<SqliteException>(() => f.Finish(ushort.MaxValue, (ushort)sample.Id), "授职回执末段失败向上传递");
                That(f.Receipts == 0 && System.Text.Json.JsonSerializer.Serialize(f.Read()) == before,
                    "授职末段失败回滚资格、双树、材料及任务，不留下半份赠技");
                f.Sql("DROP TRIGGER fail_qualification");
            }
            var result = f.Finish(ushort.MaxValue, (ushort)sample.Id);
            var commit = result.Receipt!.Commit;
            byte[] ack = DfoServer.Network.Builders.QuestPackets.Finish((ushort)sample.Id, commit.ConditionType, commit.GainedExperience,
                commit.Consumed.Select(row => new DfoServer.Network.Builders.QuestConsumedItemProjection(row.Position.Index, row.Count)).ToArray(),
                commit.DirectGoldAmount, [], expertJobType: commit.ExpertJobType);
            using var db = f.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT job_type FROM character_expert_job";
            byte[] actor = DfoServer.Network.Builders.TownPackets.Actor(commit.Character, 0);
            byte[] actorBlind = DfoServer.Network.Builders.TownPackets.Actor(commit.Character with { ExpertJobType = 0 }, 0);
            That(result.Outcome == QuestFinishOutcome.Committed && commit.GainedExperience == 0 && commit.DirectGoldAmount == 0 &&
                commit.ExpertJobType == sample.Type && commit.Consumed.Single() is { ItemId: 3037, Count: 100, Uid: 0 } &&
                commit.Consumed[0].Position.Index == ItemSlotBoundService.CurrencySlot(CurrencyKind.ClearCube) &&
                commit.Character.ExpertJobType == sample.Type && commit.Character.CompletedQuests.Contains(sample.Id) &&
                CurrencyWallet.Get(commit.Character.Inventory, CurrencyKind.ClearCube) == 0 &&
                (long)command.ExecuteScalar()! == sample.Type && ack[^4] == 20 && ack[^3] == sample.Type && ack[^2] == 0 && ack[^1] == 0 &&
                f.Receipts == 1 && actor.Length == actorBlind.Length &&
                ActorExpertJobType(actor, actorBlind) == sample.Type,
                "副职业 " + sample.Id + " 扣除 100 无色、经验为 0、类型落库，ACK 使用mode20，Actor 投影 I+3B0");
            var grants = scripts.Skills(sample.Type);
            That(grants is [{ Level: 1 }] && grants[0].Id == (sample.Type switch { 1 => 191, 2 => 193, 3 => 194, _ => 192 }) &&
                commit.Character.Learning.Trees.All(tree => grants.All(grant => tree.Skills.Any(skill => skill.Id == grant.Id && skill.Level == grant.Level))) &&
                commit.Character.Learning.Trees.Zip(funded.Learning.Trees).All(pair => pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp),
                "四份EXJ的真实赠技进入双树且不扣SP/TP，规范Skills与重读一致");
            var synchronized = SkillPointLedger.Synchronize(0, 0, 20, commit.Character.Learning, learning);
            var reset = SkillPointLedger.Reset(0, 0, 20, synchronized, 0, learning, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var changed = QuestProfessionReward.Prepare(commit.Character, 1, 20, synchronized, learning, DateTimeOffset.UtcNow);
            That(reset.Success && new[] { reset.State!, changed }.All(state => state.Trees.All(tree =>
                    grants.All(grant => tree.Skills.Any(skill => skill.Id == grant.Id && skill.Level >= grant.Level)))),
                "等级核算、洗点和主职业重建保留独立副职业赠技，不误算为普通SP消费");
            var replay = f.Finish(ushort.MaxValue, (ushort)sample.Id);
            var saved = f.Read();
            That(replay.Outcome == QuestFinishOutcome.AlreadyCompleted && saved.ExpertJobType == sample.Type &&
                CurrencyWallet.Get(saved.Inventory, CurrencyKind.ClearCube) == 0 && f.Receipts == 1,
                "完成后再次交付保持已完成，不重复扣晶块、不改类型");
            That(f.Session.MutateInventory(character => CurrencyService.SetGold(character, 20_000)).Accepted, "放弃前置金币");
            var given = f.Session.GiveUpExpertJob(scripts);
            That(given.Error == 0 && !f.Read().CompletedQuests.Contains(sample.Id) &&
                QuestEligibility.Evaluate(f.Read(), quest, quests).Eligible,
                "放弃副职业清理EXJ声明的旧链完成记录，可重新接取授职任务");
            byte[] actorCleared = DfoServer.Network.Builders.TownPackets.Actor(given.Character!, 0);
            using var cleared = f.Open();
            using var clearedCommand = cleared.CreateCommand();
            clearedCommand.CommandText = "SELECT count(*) FROM character_expert_job";
            using var meta = f.Open();
            using var metaCommand = meta.CreateCommand();
            metaCommand.CommandText = "SELECT giveup_state FROM character_expert_meta";
            That(given.Error == 0 && given.Character!.ExpertJobType == 0 && given.Character.Inventory.Gold == 19_000 &&
                given.Character.ExpertGiveUpState == 1 && (long)clearedCommand.ExecuteScalar()! == 0 &&
                (long)metaCommand.ExecuteScalar()! == 1 &&
                ActorExpertJobType(actor, actorCleared) == sample.Type &&
                f.Read().ExpertJobType == 0 && f.Read().ExpertGiveUpState == 1 &&
                f.Read().Learning.Trees.All(tree => tree.Skills.All(skill => !grants.Any(grant => grant.Id == skill.Id))),
                "C2S 239 扣 1000 金后清 character_expert_job，放弃档留在 meta，Actor I+3B0 回到 0");
            if (sample.Id == 2702)
            {
                var both = new QuestCatalog(new[] { quests.Get(2702), quests.Get(2708) }.ToDictionary(row => row.Id));
                That(!QuestEligibility.Evaluate(commit.Character, quests.Get(2708), both).Eligible, "完成附魔师后，碰撞声明挡住炼金术师");
                f.Sql("INSERT INTO character_expert_job(character_id,account,job_type) VALUES(" + commit.Character.Id +
                    ",'ordinary-quest',1)");
                f.Session.ReturnToRoster();
                f.Session.Select(0);
                f.Session.MarkReady();
                That(f.Session.InitializeLearning(learning, scripts) == SkillLearningFailure.None &&
                    f.Read().Learning.Trees.All(tree => tree.Skills.Any(skill => skill.Id == 191 && skill.Level == 1)),
                    "存量已授职角色入城补齐缺失赠技，不重领任务或扣材料");
                long revision = f.Read().Revision;
                That(f.Session.InitializeLearning(learning, scripts) == SkillLearningFailure.None && f.Read().Revision == revision,
                    "已有正确赠技不重复保存或增加revision");
                var second = f.Session.GiveUpExpertJob(scripts);
                That(second.Error == 0 && second.Character!.Inventory.Gold == 9_000 && second.Character.ExpertGiveUpState == 2 &&
                    f.Read().ExpertJobType == 0,
                    "第二次放弃在资格行删掉后仍按档 10000 扣金");
                f.Sql("INSERT INTO character_expert_job(character_id,account,job_type) VALUES(" + commit.Character.Id +
                    ",'ordinary-quest',1)");
                f.Session.ReturnToRoster();
                f.Session.Select(0);
                f.Session.MarkReady();
                That(f.Session.MutateInventory(character => CurrencyService.SetGold(character, 500)).Accepted, "不足金币");
                var poor = f.Session.GiveUpExpertJob(scripts);
                That(poor.Error == 19 && f.Read().ExpertJobType == 1 && f.Read().Inventory.Gold == 500,
                    "金币不足错误 19 且不清资格");
            }
        }
        RunExpertJobGameplay(towns, characters, learning, growth, parameters, items, quests, scripts);
        void RejectLater(int id, int[] completed, ActiveMainQuest[]? active = null)
        {
            var quest = quests.Get(id);
            using var f = new Fixture(towns, characters, learning, growth, parameters, items, quest, materialCount: 0,
                active: active, configure: source => Level20(source) with { CompletedQuests = completed });
            var prepared = QuestCompletionPlan.Prepare(f.Read(), quest, ushort.MaxValue, f.Catalog, parameters, items.Items,
                items.GoldLimits, items.EpicPieces, growth, DateTimeOffset.UtcNow);
            That(prepared.Plan is null && f.Receipts == 0, id + " 仍不能完成：" + prepared.Reason);
        }
        foreach (var meet in new (int Id, int Prerequisite, int Reward)[]
                 { (11007, 2702, 2600482), (11013, 2708, 2600463), (11016, 2712, 2600474), (10020, 2710, 18400) })
        {
            var quest = quests.Get(meet.Id);
            var first = quests.Get(meet.Prerequisite);
            That(quest is { Condition: "[meet npc]", SubType: -1, Difficulty: null } && quest.IntData.SequenceEqual([144]) &&
                quest.RewardType == "[item]" && QuestCoverage.Scan(new(new[] { quest, first }.ToDictionary(row => row.Id)),
                    items.Items, items.EpicPieces, parameters).Quests.Single(row => row.Id == meet.Id).ImplementedPath,
                meet.Id + " 会面第二步省略 difficulty 后可走普通领奖");
            using var f = new Fixture(towns, characters, learning, growth, parameters, items, quest, materialCount: 0,
                definitions: [quest, first], active: [new(0, (ushort)meet.Id, 0)],
                configure: source => Level20(source) with { CompletedQuests = [meet.Prerequisite] });
            var result = f.Finish(ushort.MaxValue, (ushort)meet.Id);
            var commit = result.Receipt!.Commit;
            That(result.Outcome == QuestFinishOutcome.Committed && commit.ConditionType == 4 &&
                commit.ExperienceReward is { DifficultyKey: "", DifficultyWeight: 100, Amount: > 0 } &&
                commit.Rewards.Single() is { GrantedCount: 1, Item.Core.ItemId: var rewardId } && rewardId == meet.Reward &&
                commit.Character.CompletedQuests.Contains(meet.Id) && f.Receipts == 1,
                meet.Id + " 会面交付发物品并按缺省 100 权重给经验：" + result.Reason);
        }
        foreach (var seek in new (int Id, int[] Completed, int Material, (int Id, int Count)[] Rewards)[]
                 { (11019, [2702, 11007], 3227, [(3227, 5), (3166, 20), (3619, 1), (18400, 1)]),
                   (11014, [2708, 11013], 2610024, [(2610024, 1), (2610027, 1)]),
                   (11017, [2712, 11016], 2610042, [(2610042, 1), (2610031, 1)]) })
        {
            var quest = quests.Get(seek.Id);
            var related = seek.Completed.Select(quests.Get).Append(quest).ToArray();
            That(quest is { Condition: "[seeking]", SubType: -1, Difficulty: null } && quest.IntData.SequenceEqual([seek.Material, 1]),
                seek.Id + " 寻物第三步消耗单件材料");
            using var f = new Fixture(towns, characters, learning, growth, parameters, items, quest, materialCount: 0,
                definitions: related, active: [new(0, (ushort)seek.Id, 0)],
                configure: source => Level20(source) with { CompletedQuests = seek.Completed });
            That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character, [new(seek.Material, 1)],
                items.Items, items.GoldLimits, items.EpicPieces)).Accepted, seek.Id + " 寻物材料走现有发放");
            var result = f.Finish(ushort.MaxValue, (ushort)seek.Id);
            var commit = result.Receipt!.Commit;
            bool Rewarded((int Id, int Count) row) =>
                commit.Rewards.Any(granted => granted.Item.Core.ItemId == row.Id && granted.GrantedCount == row.Count) ||
                commit.Currencies.Any(granted => granted.ItemId == row.Id && granted.Count == row.Count);
            That(result.Outcome == QuestFinishOutcome.Committed && commit.ConditionType == 0 &&
                commit.ExperienceReward is { DifficultyKey: "", DifficultyWeight: 100, Amount: > 0 } &&
                commit.Consumed.Single() is { Count: 1, ItemId: var consumedId } && consumedId == seek.Material &&
                seek.Rewards.All(Rewarded) && commit.Character.CompletedQuests.Contains(seek.Id) && f.Receipts == 1,
                seek.Id + " 寻物交付扣材料、发奖励并按缺省 100 给经验：" + result.Reason);
        }
        RejectLater(11009, [2702, 11007, 11019]);
        RejectLater(10021, [2710, 10020]);
        foreach (var tutorial in new (int Id, int[] Completed, byte Type)[]
                 { (11009, [2702, 11007, 11019], 1), (11015, [2708, 11013, 11014], 2),
                   (10021, [2710, 10020], 3), (11018, [2712, 11016, 11017], 4) })
        {
            var quest = quests.Get(tutorial.Id);
            var related = tutorial.Completed.Select(quests.Get).Append(quest).ToArray();
            That(QuestExpertJobProgress.Tutorial(quest) && QuestExpertJobProgress.RequiredType(quest) == tutorial.Type &&
                QuestCoverage.Scan(new(related.ToDictionary(row => row.Id)), items.Items, items.EpicPieces, parameters)
                    .Quests.Single(row => row.Id == tutorial.Id).ImplementedPath,
                tutorial.Id + " 第四步/分解末步覆盖：" + quest.Condition);
            using var f = new Fixture(towns, characters, learning, growth, parameters, items, quest, materialCount: 0,
                definitions: related, active: [new(0, (ushort)tutorial.Id, 1)],
                configure: source => Level20(source) with { CompletedQuests = tutorial.Completed });
            var created = f.Read();
            f.Sql("INSERT INTO character_expert_job(character_id,account,job_type) VALUES(" + created.Id + ",'ordinary-quest'," +
                tutorial.Type + ")");
            f.Session.ReturnToRoster();
            f.Session.Select(0);
            f.Session.MarkReady();
            That(f.Read().ExpertJobType == tutorial.Type && f.Session.OpenExpertJobStore(),
                tutorial.Id + " 重登读到类型后才能开店");
            var observed = f.Session.ObserveExpertJobTutorial((ushort)tutorial.Id, f.Catalog);
            That(observed is { Outcome: QuestLifecycleOutcome.Committed } &&
                f.Read().ActiveMainQuests.Single().ConditionState == 0,
                tutorial.Id + " 商店事实归零条件：" + observed?.Reason);
            var result = f.Finish(ushort.MaxValue, (ushort)tutorial.Id);
            var commit = result.Receipt!.Commit;
            That(result.Outcome == QuestFinishOutcome.Committed &&
                commit.ConditionType == QuestExpertJobProgress.NativeType(quest) &&
                commit.DirectGoldAmount == 0 &&
                commit.ExperienceReward is { DifficultyKey: "", DifficultyWeight: 100, Amount: > 0 } &&
                commit.Character.CompletedQuests.Contains(tutorial.Id) && f.Receipts == 1,
                tutorial.Id + " 教程交付零金币、缺省 100 经验：" + result.Reason);
        }
        var plain = new Character(1, 0, "Expert", 1, new(38, 1, 1, 1), null) { Level = 20 };
        var ordinaryDefault = QuestExperiencePolicy.Prepare(plain,
            quests.Get(11019) with { Path = "n_quest/ordinary/missing-difficulty.qst" }, parameters, out _);
        That(ordinaryDefault.DifficultyKey == "" && ordinaryDefault.DifficultyWeight == 100 &&
            ordinaryDefault.Amount == QuestExperiencePolicy.Prepare(plain, quests.Get(11019), parameters).Amount,
            "统一缺省 difficulty=100 不依赖副职业目录或特定任务路径");
        Console.WriteLine($"PASS {Count - checks} 四条副职业资格交付、落库、重登投影、第二/三/四步领奖与放弃断言");
    }

    static void RunExpertJobGameplay(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning,
        CharacterGrowthCatalog growth, QuestRewardParameterCatalog parameters, ItemResources items, QuestCatalog quests,
        ExpertJobScriptCatalog scripts)
    {
        NewCharacter Level20(NewCharacter source) => source with
        {
            Level = 20,
            InitialLearning = SkillPointLedger.Synchronize(source.Job, source.GrowType, 20,
                source.InitialLearning with { Experience = growth.GetLevelStartExperience(20) }, learning)
        };
        var dummy = quests.Get(2702);
        byte[] type3 = DfoServer.Network.Builders.ExpertJobPackets.Info(3, [], 1, 300);
        That(type3.Length == 10 && type3[0] == 0 && type3[1] == 3, "NOTI 205 分解师 10 字节");
        var alchemist = new Character(1, 0, "Alch", 0, new(0, 0, 0, 0), null) { ExpertJobType = 2 };
        byte[] type2 = DfoServer.Network.Builders.ExpertJobPackets.Info(alchemist, scripts);
        That(type2.Length == 3 + 4 * type2[2] && type2[0] == 0 && type2[1] == 2, "NOTI 205 炼金 3+4n");
        using (var f = new Fixture(towns, characters, learning, growth, parameters, items, dummy, materialCount: 0, configure: Level20))
        {
            var created = f.Read();
            That(BitConverter.ToUInt16(DfoServer.Network.Builders.ExpertJobPackets.CreateStoreNotice(0, created.Id), 1) == created.Id,
                "开店通知演员号等于 character.Id");
            var board = new ExpertJobStoreBoard();
            board.Open(new(created.Id, 2, 0, "shop", 7, 8, 9, 10, 1, 300));
            That(board.TryGet(created.Id, out var store) && store.OwnerId == created.Id && store.First == 7,
                "副职业商店按角色 Id 持久化");
            board.TryClose(created.Id, out _);
            That(!board.TryUpdate(store, store with { Endurance = 299 }) && !board.TryGet(created.Id, out _),
                "迟到的店铺耐久刷新不能复活已关闭店铺");
        }
        using (var f = new Fixture(towns, characters, learning, growth, parameters, items, dummy, materialCount: 0, configure: Level20))
        {
            var created = f.Read();
            f.Sql("INSERT INTO character_expert_job(character_id,account,job_type) VALUES(" + created.Id + ",'ordinary-quest',1)");
            f.Session.ReturnToRoster();
            f.Session.Select(0);
            f.Session.MarkReady();
            int cardId = 0;
            foreach (int id in scripts.KnownCards(1))
            {
                try { _ = items.Items.Get(id); }
                catch (InvalidDataException) { continue; }
                if (f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character, [new(id, 1)],
                    items.Items, items.GoldLimits, items.EpicPieces)).Accepted)
                {
                    cardId = id;
                    break;
                }
            }
            That(cardId != 0, "附魔师 [base result] 有可发放卡片");
            var card = f.Read().Inventory.Items.Single(item => item.Core.ItemId == cardId);
            That(!ExpertJobCraft.TryPlan(f.Read(), scripts, items.Items, items.GoldLimits, items.EpicPieces, 0, 1,
                (ushort)card.Position.Index, _ => 0, DateTimeOffset.UtcNow, out _, out _) &&
                f.Read().Inventory.Find(card.Position) == card,
                "C2S 238 无映射配方不得扣卡伪造成功");
        }
        using (var f = new Fixture(towns, characters, learning, growth, parameters, items, dummy, materialCount: 0, configure: Level20))
        {
            var created = f.Read();
            f.Sql("INSERT INTO character_expert_job(character_id,account,job_type) VALUES(" + created.Id + ",'ordinary-quest',1)");
            f.Session.ReturnToRoster();
            f.Session.Select(0);
            f.Session.MarkReady();
            That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
                [new(490705314, 1), new(22004, 1)], items.Items, items.GoldLimits, items.EpicPieces)).Accepted, "601 源与目标发放");
            var snapshot = f.Read();
            var bead = snapshot.Inventory.Items.Single(item => item.Core.ItemId == 490705314);
            var gear = snapshot.Inventory.Items.Single(item => item.Core.ItemId == 22004);
            var service = new InventoryEnchantmentService(items.Items, items.Enchantments);
            That(ExpertJobCraft.TryPlanEnchant(snapshot, service, scripts, (byte)gear.Position.List, (ushort)gear.Position.Index,
                (byte)bead.Position.List, (ushort)bead.Position.Index, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                out var plan, out int experience, out _) &&
                f.Session.CommitExpertJobCraft(plan, experience).Accepted,
                "C2S 601 规划消费宝珠并附魔");
            var after = f.Read();
            That(after.Inventory.Find(bead.Position) is null &&
                after.Inventory.Find(gear.Position)!.Core.EnchantCardId == 10309006,
                "C2S 601 消费源栈并写入目标 EnchantCardId");
        }
        using (var f = new Fixture(towns, characters, learning, growth, parameters, items, dummy, materialCount: 0, configure: Level20))
        {
            var created = f.Read();
            f.Sql("INSERT INTO character_expert_job(character_id,account,job_type) VALUES(" + created.Id + ",'ordinary-quest',3)");
            f.Session.ReturnToRoster();
            f.Session.Select(0);
            f.Session.MarkReady();
            That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character, [new(10400, 1)],
                items.Items, items.GoldLimits, items.EpicPieces)).Accepted, "分解装备发放");
            var gear = f.Read().Inventory.Items.First(item => item.Core.ItemId == 10400 &&
                item.Position.List == InventoryListType.Main && item.Core.EquipmentLockId == 0);
            var service = new InventoryDisjointService(items.Items, items.Disjoint, items.GoldLimits, items.EpicPieces,
                new Random(1), scripts);
            var result = f.Session.DisjointEquipment(new(gear.Position, -1, 0), service, out var receipt);
            That(result.Accepted && receipt is not null && receipt.Rewards.Any(row => row.ItemId == 3037 && row.Count > 0),
                "分解师装备分解按 [disjoint result] 发放 3037，匹配 gradeKey=machineGrade-1/rarity/state");
        }
    }

    static byte ActorExpertJobType(byte[] typed, byte[] blind)
    {
        int offset = -1;
        for (int at = 0; at < typed.Length; at++)
            if (typed[at] != blind[at])
            {
                TestCheck.That(offset < 0 && at + 4 < typed.Length, "Actor 副职业类型只能改一个字节");
                offset = at;
            }
        TestCheck.That(offset >= 0 && typed.AsSpan(offset + 1, 4).SequenceEqual(blind.AsSpan(offset + 1, 4)),
            "026777A0 的 u32 累计仍为 0");
        return typed[offset];
    }

    internal sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-ordinary-quest-" + Guid.NewGuid().ToString("N"));
        internal string Database => Path.Combine(root, "game.db");
        readonly ItemResources items; readonly CharacterGrowthCatalog growth; readonly QuestRewardParameterCatalog parameters;
        public CharacterRepository Repository { get; }
        readonly AccountOwners owners = new(); public AccountOwners.Lease Owner { get; }
        internal AccountOwners Owners => owners;
        public QuestCatalog Catalog { get; } public GameSession Session { get; }
        public QuestCompletionRepositoryTests.LostResultRepository? Lost { get; }
        public Fixture(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning, CharacterGrowthCatalog growth,
            QuestRewardParameterCatalog parameters, ItemResources items, StoryQuest quest, int materialCount = 5, bool locked = false,
            bool fullEquipment = false, bool loseResult = false, StoryQuest[]? definitions = null, ActiveMainQuest[]? active = null,
            Func<NewCharacter,NewCharacter>? configure = null, bool useCycles = false,QuestSkipEffects? questEffects=null,bool resumeTown=false,bool loseLifecycleResult=false, QuestStageGraph? stages=null,
            TimeProvider? timeProvider=null, Func<string,DfoServer.Game.Events.ServerEventService>? createEvents=null)
        {
            this.items = items; this.growth = growth; this.parameters = parameters;
            Catalog = new((definitions ?? [quest]).ToDictionary(row => row.Id));
            Repository = new(Path.Combine(root, "game.db"), Catalog); Owner = owners.Acquire("ordinary-quest", () => { });
            if (Catalog.All.Any(row => QuestAccountPolicy.SharedCompletion(row) || QuestRelationProgress.IsAccountSyncMarker(row)))
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
            Lost = loseResult || loseLifecycleResult ? new(Repository) { LoseCompletionResult = loseResult, LoseLifecycleResult = loseLifecycleResult } : null;
            Session = new(Lost is null ? Repository : Lost, towns, Owner, questCatalog: useCycles ? Catalog : null,questEffects:questEffects,stageGraph:stages,
                timeProvider:timeProvider,serverEvents:createEvents?.Invoke(Database));
            Session.Select(0,resumeTown: resumeTown); Session.MarkReady();
        }
        public Character Read() => Repository.LoadCharacter(Owner, 0);
        internal string OpenPath => Path.Combine(root,"game.db");
        public QuestCompletionPreparation Prepare(ushort selector, ushort multiplier = 1) => QuestCompletionPlan.Prepare(Read(), Catalog.Get(100), selector,
            Catalog, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth, DateTimeOffset.UtcNow,multiplier: multiplier);
        public QuestLifecycleReceipt? Change(ushort id, QuestLifecycleAction action, QuestClientCondition? condition = null) =>
            Session.ChangeQuest(id, action, Catalog, items.Items, items.GoldLimits, items.EpicPieces, condition);
        public QuestFinishResult Finish(ushort selector, ushort questId = 100, ushort multiplier = 1) => Session.FinishQuest(questId, selector, Catalog, parameters,
            items.Items, items.GoldLimits, items.EpicPieces, growth,multiplier,items.ExpertJobs);
        public SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + Path.Combine(root, "game.db") + ";Pooling=False"); db.Open(); return db; }
        public long Receipts { get { using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT count(*) FROM character_quest_completion_receipts"; return (long)c.ExecuteScalar()!; } }
        public void Sql(string sql) { using var db = Open(); using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        public void Dispose() { Session.End(); Owner.Dispose(); TestFiles.DeleteDirectory(root, true); }
    }
}
