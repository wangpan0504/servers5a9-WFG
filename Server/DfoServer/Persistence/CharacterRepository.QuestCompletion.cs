using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Currency;
using DfoServer.Game.Quests;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public QuestCompletionCommit CommitQuestCompletion(AccountOwners.Lease owner, QuestCompletionPlan plan) => owner.Execute(() =>
    {
        var expected = plan.Expected; var clear = plan.AuthoritativeClear; var reward = plan.ExperienceReward;
        uint? goldMailId = null, resourceMailId = null;
        var active = expected.ActiveMainQuests.SingleOrDefault(row => row.AcceptanceId == plan.AcceptanceId);
        var closed = plan.ClosedChildren.Select(row => row.AcceptanceId).ToHashSet();
        if (plan.CompletedChild is { } terminalChild) closed.Add(terminalChild.AcceptanceId);
        var newCompleted = new[] { (int)plan.QuestId }.Concat(plan.CascadeMarkerQuestId is { } marker ? new[] { marker } : [])
            .Concat(plan.CompletedChild is { } completedChild ? new[] { (int)completedChild.QuestId } : [])
            .Except(QuestEligibility.Completed(expected)).Order().ToArray();
        if (expected.Id == 0 || expected.Job > 15 || plan.AcceptanceId == Guid.Empty || active is null ||
            active.QuestId != plan.QuestId || active.Slot != plan.QuestSlot || expected.CompletedQuests.Contains(plan.QuestId) ||
            plan.CascadeMarkerQuestId is { } cascade && (cascade is <= 0 or >= 30000 || cascade == plan.QuestId) ||
            reward.ResourcePath != QuestRewardParameterCatalog.ResourcePath ||
            (plan.GrowthPlan?.RequestedExperience ?? 0) != plan.TotalRequestedExperience ||
            plan.TargetLearning.Experience != (plan.GrowthPlan?.Learning.Experience ?? expected.Learning.Experience) ||
            closed.Count != plan.ClosedChildren.Length + (plan.CompletedChild is null ? 0 : 1) || closed.Contains(plan.AcceptanceId) ||
            plan.CompletedChild is { } readyChild && (plan.ConditionType != 44 || readyChild.AcceptanceId == Guid.Empty || readyChild.ConditionState != 0 ||
                !expected.ActiveMainQuests.Contains(readyChild) || QuestEligibility.Completed(expected).Contains(readyChild.QuestId)) ||
            plan.ClosedChildren.Length != 0 && plan.ConditionType != 70 ||
            plan.ClosedChildren.Any(row => row.AcceptanceId == Guid.Empty || !expected.ActiveMainQuests.Contains(row)) ||
            !plan.ActiveAfter.Select(row => (row.Slot, row.QuestId, row.AcceptanceId)).SequenceEqual(
                expected.ActiveMainQuests.Where(row => row.AcceptanceId != plan.AcceptanceId && !closed.Contains(row.AcceptanceId)).Select(row => (row.Slot, row.QuestId, row.AcceptanceId))) ||
            !plan.NewCompletedQuestIds.SequenceEqual(newCompleted) ||
            !plan.CompletedAfter.SequenceEqual(expected.CompletedQuests.Concat(newCompleted).Distinct().Order()) ||
            plan.SyncedQuestIds.Any(id => id != plan.CascadeMarkerQuestId) ||
            !plan.AccountCompletedAfter.SequenceEqual((plan.AccountQuest ? expected.AccountCompletedQuests.Append((int)plan.QuestId) : expected.AccountCompletedQuests)
                .Concat(plan.SyncedQuestIds).Distinct().Order()))
            throw new InvalidDataException("领奖必须使用原接取、完整资产和成长规划");
            if (!DfoServer.GameWorld.Quests.QuestTargetTypes.IsNative(plan.ConditionType) || reward.BaseExperience < 0 || reward.DifficultyWeight < 0 ||
            plan.Snapshot is null || plan.MailedGoldAmount != 0 && plan.MailedGoldAmount != plan.GoldAmount ||
            plan.ConditionType is not (0 or 42) && active.ConditionState != 0 && !(plan.CompletedChild is not null && active.ConditionState == 1) ||
            plan.TotalRequestedExperience == 0 && plan.GrowthPlan is not null || plan.TotalRequestedExperience != 0 && plan.GrowthPlan is null ||
            plan.GrowthPlan is { } growth && (growth.CharacterId != expected.Id || growth.Revision != expected.Revision) ||
            (plan.ConditionType is 1 or 3 or 45) != (clear is not null) ||
            clear is not null && !QuestCompletionPlan.MatchesClearedActiveQuest(expected, clear) ||
            plan.TerminalClear is { } terminalClear && (plan.ConditionType != 44 || plan.CompletedChild is not { } clearChild ||
                clearChild.QuestId != terminalClear.QuestId || clearChild.AcceptanceId != terminalClear.AcceptanceId ||
                !QuestCompletionPlan.MatchesClearedActiveQuest(expected,terminalClear)) ||
            plan.CompletedParentId is { } parent && (!expected.CompletedQuests.Contains(parent) ||
                expected.ActiveMainQuests.Any(row => row.QuestId == parent) || active.ConditionState != 0 ||
                reward.Amount != 0 || plan.Inventory.Changed || plan.Consumed.Length != 0 || plan.Rewards.Length != 0 ||
                plan.Currencies.Length != 0 || plan.GoldAmount != 0 || plan.UpdatesLearning || plan.ProfessionGrowType is not null ||
                plan.TargetEquipmentSlotFlags != expected.EquipmentSlotFlags || plan.TpAmount != 0 || plan.CascadeMarkerQuestId is not null))
            throw new InvalidDataException("任务交付需要已确认类型、权威条件与一致成长规划");
        ValidateInventoryPlan(expected, plan.Inventory);
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CommitQuestCompletion", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id) throw new InvalidOperationException("领奖角色或账号已变化");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id); command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$acceptance", plan.AcceptanceId.ToString("N"));
        command.Parameters.AddWithValue("$quest", plan.QuestId);
        if (clear is not null) VerifyClear(clear);
        if (plan.TerminalClear is { } childClear) VerifyClear(childClear);
        command.Parameters["$acceptance"].Value = plan.AcceptanceId.ToString("N");
        command.Parameters["$quest"].Value = plan.QuestId;

        void VerifyClear(QuestClearReceipt fact)
        {
            command.Parameters["$acceptance"].Value = fact.AcceptanceId.ToString("N");
            command.Parameters["$quest"].Value = fact.QuestId;
            command.CommandText = """
                SELECT event_id,instance_id,task_attempt,epoch,quest_slot,revision,dungeon_id,map_id,source_type FROM character_quest_clear_receipts
                WHERE character_id=$id AND account=$account AND acceptance_id=$acceptance AND quest_id=$quest
                """;
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != fact.EventId.ToString("N") || reader.GetString(1) != fact.InstanceId.ToString("N") ||
                reader.GetInt64(2) != fact.TaskAttempt || reader.GetInt64(3) != fact.Epoch || reader.GetInt32(4) != fact.QuestSlot || reader.GetInt64(5) != fact.Revision ||
                reader.GetInt32(6) != fact.DungeonId || reader.GetInt32(7) != fact.MapId || reader.GetString(8) != fact.SourceType.ToString())
                throw new InvalidDataException("领奖资格不是原持久接取的权威清图事实");
            reader.Close();
            if (fact.SourceType == QuestClearSourceType.PassiveObject)
                VerifySceneDeathLedger(db,tx,owner.Account,current.Id,fact.EventId,fact.InstanceId,fact.TaskAttempt);
        }
        string consumed = JsonSerializer.Serialize(plan.Consumed);
        command.CommandText = "SELECT * FROM character_quest_completion_receipts WHERE character_id=$id AND account=$account AND acceptance_id=$acceptance";
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                long I(string column) => reader.GetInt64(reader.GetOrdinal(column));
                string? S(string column) { int at = reader.GetOrdinal(column); return reader.IsDBNull(at) ? null : reader.GetString(at); }
                if (I("quest_id") != plan.QuestId || S("event_id") != clear?.EventId.ToString("N") || S("instance_id") != clear?.InstanceId.ToString("N") ||
                    clear is not null && (I("task_attempt") != clear.TaskAttempt || I("epoch") != clear.Epoch) ||
                    S("resource_source") != reward.ResourcePath || S("quest_source") != reward.QuestPath ||
                    I("native_index") != reward.NativeIndex || I("base_experience") != reward.BaseExperience || I("raw_additional_percent") != reward.RawAdditionalPercent ||
                    S("difficulty_key") != reward.DifficultyKey || I("difficulty_weight") != reward.DifficultyWeight || I("experience_amount") != plan.TotalRequestedExperience ||
                    I("gold_amount") != plan.GoldAmount || I("before_revision") != expected.Revision || I("before_level") != expected.Level ||
                    I("before_experience") != checked((long)expected.Learning.Experience) || I("condition_type") != plan.ConditionType ||
                    I("selector") != plan.RewardSelector || I("multiplier") != plan.Multiplier || S("consumed") != consumed ||
                    S("plan_snapshot") is { } snapshot && snapshot != plan.Snapshot)
                    throw new InvalidDataException("相同领奖身份的规则、请求或持久结果发生变化");
                var granted = JsonSerializer.Deserialize<ImmutableArray<QuestGrantedItem>>(S("rewards")!);
                if (plan.MailedGoldAmount != 0)
                    goldMailId = ReadQuestDeliveryMail(db, tx, QuestGoldMail(owner, current, plan));
                if (plan.ResourceMail is not null)
                    resourceMailId = ReadQuestDeliveryMail(db, tx, QuestResourceMail(owner, current, plan));
                VerifyQuestGrantedItems(plan, granted);
                var changed = JsonSerializer.Deserialize<ImmutableArray<InventoryItem>>(S("changed_items")!);
                // 已发布v13只改变七个奖励槽；保留旧回执，不补写新的来源。
                if (clear is not null && changed.Length == 0) changed = granted.Select(row => row.Item).ToImmutableArray();
                var index = changed.ToDictionary(item => item.Position);
                var after = plan.Inventory.After with { Items = plan.Inventory.After.Items.Select(item => index.GetValueOrDefault(item.Position, item)).ToImmutableArray() };
                if (plan.Inventory.ChangedCurrencies.Any(CurrencyWallet.IsAccount))
                    after = after with { AccountWallet = after.AccountWallet with { Revision = checked(expected.Inventory.AccountWallet.Revision + 1) } };
                bool projection = current.Revision == I("revision") && current.Level == I("after_level") && current.Level == plan.TargetLevel &&
                    current.Job == expected.Job && current.GrowType == plan.TargetGrowType && current.Position == expected.Position &&
                    current.EquipmentSlotFlags == plan.TargetEquipmentSlotFlags && current.ExpertJobType == plan.TargetExpertJob &&
                    current.AcceptedStoryLevel == expected.AcceptedStoryLevel && current.ActiveMainQuests.SequenceEqual(plan.ActiveAfter) &&
                    current.CompletedQuests.SequenceEqual(plan.CompletedAfter) && current.Learning.Experience == (ulong)I("after_experience") &&
                    current.AccountCompletedQuests.SequenceEqual(plan.AccountCompletedAfter) && QuestCycle.SameClaims(current.AccountQuestDailyClaims,plan.AccountDailyClaimsAfter) &&
                    QuestCycle.SameAnswers(current.QuestAnswers,plan.AnswersAfter) && QuestCycle.SameAnswers(current.AccountQuestAnswers,plan.AccountAnswersAfter) &&
                    SameLearning(current.Learning, plan.TargetLearning) &&
                    current.Skills.SequenceEqual(!plan.UpdatesLearning ? expected.Skills : plan.TargetLearning.Trees.Single(tree => tree.Context == plan.TargetLearning.ActiveContext).Skills
                        .Select(skill => new CharacterSkill(skill.Id, skill.Level)).OrderBy(skill => skill.Id)) &&
                    SameInventory(current.Inventory, new(after, after, "quest_receipt_projection"));
                return Result(current, granted, true, projection, checked((uint)(I("after_experience") - I("before_experience"))));
            }
        }
        if (plan.RequiredEvent is { } required &&
            !required.Matches(ServerEventRepository.Read(db, tx, required.Schedule.EventId)))
            throw new InvalidOperationException("任务活动领奖的原时间表已变化或已停用");
        VerifyQuestPvpRating(db,tx,owner.Account,expected.Id,plan.PvpRating);
        if (current.Revision != expected.Revision || current.Level != expected.Level || current.Job != expected.Job ||
            current.GrowType != expected.GrowType || current.Position != expected.Position || current.AcceptedStoryLevel != expected.AcceptedStoryLevel ||
            current.EquipmentSlotFlags != expected.EquipmentSlotFlags || current.ExpertJobType != expected.ExpertJobType ||
            !SameLearning(current.Learning, expected.Learning) || !current.Skills.SequenceEqual(expected.Skills) ||
            !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) || !current.CompletedQuests.SequenceEqual(expected.CompletedQuests) ||
            !QuestCycle.Same(current, expected))
            throw new InvalidOperationException("领奖原角色、场景、两树、任务或revision已变化");
        // 熟练度按规划时的持久值推进；事务内旧值不符则整笔回滚，不覆盖他人已提交的进度。
        if (plan.Proficiency is { } pendingProficiency &&
            (current.DungeonState is not { } dungeonState ||
             dungeonState.ProficiencyLevel != pendingProficiency.Before.Level ||
             dungeonState.ProficiencyExperience != pendingProficiency.Before.Experience))
            throw new InvalidOperationException("领奖熟练度前提已变化");
        if (plan.DailyPeriod != 0 && (plan.DailyPeriod != expected.QuestCycleDay || plan.DailyPeriod != QuestCycle.Day(DateTimeOffset.UtcNow)))
            throw new InvalidOperationException("每日领奖规划已经跨过游戏日边界");
        if (plan.Consumed.Any(row => row.Uid == 0 ? CurrencyWallet.RewardKind(row.ItemId) is not { } kind ||
                ItemSlotBoundService.CurrencySlot(kind) != row.Position.Index || row.Position.List != InventoryListType.Main ||
                row.Count <= 0 || CurrencyWallet.Get(expected.Inventory,kind) < row.Count :
            expected.Inventory.Find(row.Position)?.IsExpired(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) != false))
            throw new InvalidOperationException("交付材料在规划后已经过期");
        var inventoryCommit = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        if (!plan.Inventory.Changed)
        {
            command.Parameters.AddWithValue("$beforeRevision", expected.Revision);
            command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$beforeRevision";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("领奖revision已变化");
            command.Parameters.RemoveAt("$beforeRevision");
        }
        command.Parameters.AddWithValue("$level", plan.TargetLevel);
        command.Parameters.AddWithValue("$experience", checked((long)plan.TargetLearning.Experience));
        command.Parameters.AddWithValue("$revision", expected.Revision + 1);
        command.Parameters.AddWithValue("$grow", plan.TargetGrowType);
        command.Parameters.AddWithValue("$equipmentSlots",plan.TargetEquipmentSlotFlags);
        command.CommandText = "UPDATE characters SET level=$level,experience=$experience,grow_type=$grow,equipment_slot_flags=$equipmentSlots WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("领奖资产提交revision已变化");
        if (plan.GrantedExpertJob is { } expertJob)
        {
            command.Parameters.AddWithValue("$expertJob", expertJob);
            command.CommandText = """
                INSERT INTO character_expert_job(character_id,account,job_type) VALUES($id,$account,$expertJob)
                ON CONFLICT(character_id) DO UPDATE SET account=excluded.account, job_type=excluded.job_type
                WHERE character_expert_job.account=$account
                """;
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副职业类型没有写入当前角色");
        }
        ResetSkillSwitchProfession(db, tx, current.Id, current.GrowType, plan.TargetGrowType);
        if (plan.UpdatesLearning) { WriteLearning(db, tx, current.Id, plan.TargetLearning); WriteCanonicalSkills(db, tx, current.Id, plan.TargetLearning); }
        if (plan.Proficiency is { } proficiency) WriteProficiency(db, tx, owner.Account, expected.Id, proficiency.Before, proficiency.After);
        // 任务经验计入冒险团；同一接取的回执在上方提前返回，不会重复计入。
        if (CreditAdventure(db, tx, owner.Account, expected.Id, AdventureCreditSource.Earned, plan.TotalRequestedExperience,
                EffectivePosition(expected.Level, expected.Learning.Experience), expected.Level, plan.TargetLevel,
                DateTimeOffset.UtcNow) is { } credit)
            WriteAdventureCredit(db, tx, owner.Account, credit, AdventureCreditSource.Earned, adventureCatalog!);
        command.Parameters.AddWithValue("$slot", plan.QuestSlot);
        command.CommandText = "DELETE FROM character_active_main_quests WHERE character_id=$id AND slot=$slot AND quest_id=$quest AND acceptance_id=$acceptance";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("原接取活动任务不能原子清槽");
        foreach (var child in plan.ClosedChildren.Concat(plan.CompletedChild is { } completed ? new[] { completed } : []))
        {
            command.Parameters.AddWithValue("$childSlot",child.Slot);
            command.Parameters.AddWithValue("$childQuest",child.QuestId);
            command.Parameters.AddWithValue("$childAcceptance",child.AcceptanceId.ToString("N"));
            command.Parameters.AddWithValue("$childScalar",(long)child.ConditionState);
            command.CommandText = "DELETE FROM character_active_main_quests WHERE character_id=$id AND slot=$childSlot AND quest_id=$childQuest AND acceptance_id=$childAcceptance AND condition_state=$childScalar";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("父任务不能原子关闭子任务原接取");
            foreach (string key in new[] { "$childSlot","$childQuest","$childAcceptance","$childScalar" }) command.Parameters.RemoveAt(key);
        }
        WriteQuestConditions(db, tx, current.Id, expected.ActiveMainQuests, plan.ActiveAfter);
        command.Parameters.AddWithValue("$period", plan.DailyPeriod);
        command.Parameters.AddWithValue("$answer",(object?)plan.Answer ?? DBNull.Value);
        command.CommandText = "INSERT INTO character_completed_quests(character_id,quest_id,daily_period,answer) VALUES($id,$quest,$period,$answer)";
        command.ExecuteNonQuery();
        if (plan.CompletedChild is { } finishedChild)
        {
            command.Parameters.AddWithValue("$finishedChild",finishedChild.QuestId);
            command.CommandText = "INSERT INTO character_completed_quests(character_id,quest_id,daily_period,answer) VALUES($id,$finishedChild,0,NULL)";
            command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$finishedChild");
        }
        if (plan.CascadeMarkerQuestId is { } markerQuest && plan.NewCompletedQuestIds.Contains(markerQuest))
        {
            command.Parameters.AddWithValue("$marker",markerQuest);
            command.CommandText = "INSERT INTO character_completed_quests(character_id,quest_id,daily_period,answer) VALUES($id,$marker,0,NULL)";
            command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$marker");
        }
        WriteAccountSyncMarkers(db,tx,owner.Account,plan.SyncedQuestIds,plan.AcceptanceId);
        if (plan.AccountQuest)
        {
            command.CommandText = "INSERT INTO account_completed_quests(account,quest_id,character_id,acceptance_id,daily_period,answer) VALUES($account,$quest,$id,$acceptance,$period,$answer)";
            command.ExecuteNonQuery();
            // 同账号只有一个在线owner；另一角色留下的同任务槽在这一事务结束，不能再领取账号奖励。
            command.CommandText = "SELECT DISTINCT q.character_id FROM character_active_main_quests q JOIN characters c ON c.id=q.character_id WHERE c.account=$account AND q.quest_id=$quest AND c.id<>$id";
            var otherCharacters = new List<int>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) otherCharacters.Add(reader.GetInt32(0));
            foreach (int id in otherCharacters)
            {
                command.Parameters.AddWithValue("$other",id);
                command.CommandText = "DELETE FROM character_active_main_quests WHERE character_id=$other AND quest_id=$quest";
                command.ExecuteNonQuery();
                command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$other AND account=$account";
                if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("账号任务的关联角色已变化");
                command.Parameters.RemoveAt("$other");
            }
        }
        var grantedItems = plan.Rewards.Select(row => new QuestGrantedItem(
            inventoryCommit.Inventory.Find(row.Position) ?? throw new InvalidOperationException("奖励缺少实际目的槽"), row.GrantedCount)).ToImmutableArray();
        VerifyQuestGrantedItems(plan, grantedItems);
        var changedItems = plan.Inventory.ChangedSlots.Select(inventoryCommit.Inventory.Find).Where(item => item is not null).Cast<InventoryItem>().ToImmutableArray();
        foreach (var (name, value) in new (string, object?)[] {
            ("$event", clear?.EventId.ToString("N")), ("$instance", clear?.InstanceId.ToString("N")), ("$attempt", clear?.TaskAttempt), ("$epoch", clear?.Epoch),
            ("$clear", clear?.AcceptanceId.ToString("N")), ("$generation", owner.Generation), ("$rule", ""), ("$resource", reward.ResourcePath),
            ("$questPath", reward.QuestPath), ("$index", reward.NativeIndex), ("$basis", reward.BaseExperience), ("$additional", reward.RawAdditionalPercent),
            ("$difficulty", reward.DifficultyKey), ("$weight", reward.DifficultyWeight), ("$amount", plan.TotalRequestedExperience), ("$gold", plan.GoldAmount),
            ("$beforeLevel", expected.Level), ("$beforeExperience", checked((long)expected.Learning.Experience)), ("$beforeRevision", expected.Revision),
            ("$rewards", JsonSerializer.Serialize(grantedItems)), ("$condition", plan.ConditionType), ("$selector", plan.RewardSelector), ("$multiplier",plan.Multiplier),
            ("$consumed", consumed), ("$changed", JsonSerializer.Serialize(changedItems)), ("$snapshot", plan.Snapshot)
        }) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.CommandText = """
            INSERT INTO character_quest_completion_receipts(character_id,account,acceptance_id,quest_id,event_id,instance_id,
              task_attempt,epoch,generation,rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,
              difficulty_key,difficulty_weight,experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,
              before_revision,revision,rewards,clear_acceptance_id,condition_type,selector,multiplier,consumed,changed_items,plan_snapshot)
            VALUES($id,$account,$acceptance,$quest,$event,$instance,$attempt,$epoch,$generation,$rule,$resource,$questPath,$index,$basis,$additional,
              $difficulty,$weight,$amount,$gold,$beforeLevel,$level,$beforeExperience,$experience,$beforeRevision,$revision,$rewards,
              $clear,$condition,$selector,$multiplier,$consumed,$changed,$snapshot)
            """;
        command.ExecuteNonQuery();
        if (plan.StagePlan is { } stage) WriteQuestStage(db, tx, owner.Account, expected, stage);
        var committed = Require(db, tx, owner.Account, expected.Name);
        if (committed.Revision != expected.Revision + 1 || committed.Level != plan.TargetLevel || committed.GrowType != plan.TargetGrowType ||
            committed.EquipmentSlotFlags != plan.TargetEquipmentSlotFlags || committed.ExpertJobType != plan.TargetExpertJob ||
            !SameLearning(committed.Learning, plan.TargetLearning) || !committed.ActiveMainQuests.SequenceEqual(plan.ActiveAfter) ||
            !committed.CompletedQuests.SequenceEqual(plan.CompletedAfter) || !committed.AccountCompletedQuests.SequenceEqual(plan.AccountCompletedAfter) ||
            !QuestCycle.SameAnswers(committed.QuestAnswers,plan.AnswersAfter) || !QuestCycle.SameAnswers(committed.AccountQuestAnswers,plan.AccountAnswersAfter) ||
            !QuestCycle.SameClaims(committed.AccountQuestDailyClaims,plan.AccountDailyClaimsAfter))
            throw new InvalidOperationException("持久领奖结果与完整规划不同");
        if (plan.MailedGoldAmount != 0)
        {
            var (goldMailError, goldMail) = DeliverMailInTransaction(db, tx, QuestGoldMail(owner, committed, plan));
            if (goldMailError != MailboxError.None) throw new InvalidOperationException("任务金币邮件提交失败：" + goldMailError);
            goldMailId = goldMail;
        }
        if (plan.ResourceMail is not null)
        {
            var (error, mailId) = DeliverMailInTransaction(db, tx, QuestResourceMail(owner, committed, plan));
            if (error != MailboxError.None) throw new InvalidOperationException("任务声明邮件提交失败：" + error);
            resourceMailId = mailId;
        }
        ApplyBattlePassQuestCompletion(db, tx, owner.Account, plan);
        tx.Commit();
        return Result(committed, grantedItems, false, true, checked((uint)(plan.TargetLearning.Experience - expected.Learning.Experience)));

        QuestCompletionCommit Result(Character character, ImmutableArray<QuestGrantedItem> rows, bool duplicate, bool projection, uint gained) =>
            new(character, clear, reward, rows, duplicate, projection, expected.Level, expected.Learning) {
                QuestId = plan.QuestId, AcceptanceId = plan.AcceptanceId, ConditionType = plan.ConditionType,
                GoldAmount = plan.GoldAmount, MailedGoldAmount = plan.MailedGoldAmount, GoldMailId = goldMailId, ResourceMailId = resourceMailId,
                GainedExperience = gained, StagePlan = plan.StagePlan, OrdinaryExperience = plan.OrdinaryExperience,
                ContractExperience = plan.ContractExperience, TotalRequestedExperience = plan.TotalRequestedExperience, TpAmount = plan.TpAmount, HellChallenge = plan.HellChallenge, Consumed = plan.Consumed, Currencies = plan.Currencies,
                ReplacedRewardSlots = plan.ConditionType is 0 or 25 ? [] : rows.Where(row => plan.Inventory.BeforeIndex.Find(row.Item.Position) is { } old &&
                    old.Uid != row.Item.Uid).Select(row => row.Item.Position).Distinct().ToImmutableArray(),
                ChangedSlots = plan.Inventory.ChangedSlots.Concat(plan.Inventory.ChangedCurrencies.Select(ItemSlotBoundService.CurrencySlot)
                    .Where(slot => slot.HasValue).Select(slot => new InventorySlot(InventoryListType.Main,slot!.Value)))
                    .Concat(plan.Consumed.Where(row => row.Uid == 0).Select(row => row.Position)).Distinct().ToImmutableArray(), PreviousQuests = expected.ActiveMainQuests,
                PreviousGrowType = expected.GrowType, ProfessionGrowType = plan.ProfessionGrowType, ExpertJobType = plan.GrantedExpertJob,
                PreviousEquipmentSlotFlags = expected.EquipmentSlotFlags
            };
    });

    static MailboxDelivery QuestResourceMail(AccountOwners.Lease owner, Character character, QuestCompletionPlan plan)
    {
        var mail = plan.ResourceMail!;
        if (mail.QuestId != plan.QuestId || mail.Declaration.Time != 1 || mail.FrozenAt != plan.FrozenAt)
            throw new InvalidDataException("任务声明邮件必须属于原任务完成规划");
        return new("quest-resource:" + character.Id + ":" + plan.AcceptanceId.ToString("N"),
            new(character.Id, owner.Account, character.Name, character.Job, character.GrowType, character.Level),
            QuestMailPlan.Sender, mail.Declaration.Body, 0, mail.Attachments, mail.FrozenAt.ToUnixTimeSeconds());
    }

    static MailboxDelivery QuestGoldMail(AccountOwners.Lease owner, Character character, QuestCompletionPlan plan) => new(
        "quest-gold:" + character.Id + ":" + plan.AcceptanceId.ToString("N"),
        new(character.Id, owner.Account, character.Name, character.Job, character.GrowType, character.Level),
        "任务奖励", "本次任务金币因携带空间不足已保存在永久邮件，可腾出空间后领取。",
        plan.MailedGoldAmount, [], plan.FrozenAt.ToUnixTimeSeconds());

    static uint ReadQuestDeliveryMail(SqliteConnection db, SqliteTransaction tx, MailboxDelivery delivery)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT mail_id,delivery_content FROM mailbox_messages WHERE delivery_key=$key";
        query.Parameters.AddWithValue("$key", delivery.Key);
        using var reader = query.ExecuteReader();
        if (!reader.Read() || reader.GetString(1) != MailDeliveryContent(delivery))
            throw new InvalidDataException("领奖回执的原邮件缺失或冻结内容已变化");
        // 重放只恢复既有结果；已领取或删除的邮件也不重建、不补发。
        return checked((uint)reader.GetInt64(0));
    }

    private static void VerifyQuestGrantedItems(QuestCompletionPlan plan, ImmutableArray<QuestGrantedItem> rewards)
    {
        if (rewards.IsDefault || rewards.Length != plan.Rewards.Length || rewards.Select(row => row.Item.Uid).Distinct().Count() != rewards.Length)
            throw new InvalidDataException("领奖账本奖励缺失或UID重复");
        for (int index = 0; index < rewards.Length; index++) {
            var actual = rewards[index]; var expected = plan.Rewards[index];
            var planned = plan.Inventory.After.Find(expected.Position) ?? throw new InvalidDataException("领奖计划缺少奖励目的槽");
            // 宠物分配UID后只有Core.Value随真实实例更新；详情与其余核心必须逐项等于冻结计划。
            var core = expected.Core.Kind == GameWorld.Items.ItemKind.Creature
                ? expected.Core with { Value = checked((int)actual.Item.Uid) } : expected.Core;
            actual.Item.ValidateDetails(allocating: false);
            if (actual.Item.Uid <= 0 || actual.Item.Position != expected.Position || actual.Item.Core != core ||
                actual.Item.Creature != planned.Creature || actual.Item.Avatar != planned.Avatar ||
                actual.GrantedCount != expected.GrantedCount || expected.ExpectedDestinationUid != 0 && actual.Item.Uid != expected.ExpectedDestinationUid)
                throw new InvalidDataException("实际领奖UID、完整Core、槽位或增量与规划不同");
        }
    }
}
