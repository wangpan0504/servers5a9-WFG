using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public QuestQuickClearCommit CommitQuestQuickClear(AccountOwners.Lease owner, QuestQuickClearPlan plan) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        var scope = plan.ScopeQuestIds.ToHashSet();
        var newCompleted = scope.Except(QuestEligibility.Completed(expected)).Order().ToArray();
        bool validIdentity = plan.IsPreparedSkip
            ? plan.OperationId != Guid.Empty && plan.AcceptanceId == Guid.Empty && plan.ParentKey is null && plan.QuestionQuestId is null &&
                plan.PendingQuestions.Length == 0 && plan.CutoffLevel == (plan.IsTicket ? expected.Level : QuestQuickClearPlan.ManualCutoff(plan.TargetLevel)) &&
                plan.ManualAnswers.Keys.All(scope.Contains)
            : plan.OperationId == plan.AcceptanceId && plan.AcceptanceId != Guid.Empty && plan.Unit.ParentKey >= 0 &&
                expected.ActiveMainQuests.Contains(plan.ActiveQuestion) && plan.ActiveQuestion.QuestId == plan.Question.Id &&
                scope.SetEquals(plan.Interval.Concat(plan.AreaQuests).Select(quest => quest.Id).Append(plan.Question.Id).Append(plan.MarkerQuestId));
        var expectedAnswers = plan.IsPreparedSkip ? expected.QuestAnswers.SetItems(plan.ManualAnswers) : expected.QuestAnswers.SetItem(plan.Question.Id,plan.Answer);
        if (expected.Id == 0 || !validIdentity ||
            !plan.NewCompletedQuestIds.SequenceEqual(newCompleted) ||
            !plan.CompletedAfter.SequenceEqual(expected.CompletedQuests.Concat(newCompleted).Distinct().Order()) ||
            !plan.RemovedActive.SequenceEqual(expected.ActiveMainQuests.Where(row => scope.Contains(row.QuestId))) ||
            !plan.ActiveAfter.Select(row => (row.Slot,row.QuestId,row.AcceptanceId)).SequenceEqual(
                expected.ActiveMainQuests.Where(row => !scope.Contains(row.QuestId)).Select(row => (row.Slot,row.QuestId,row.AcceptanceId))) ||
            !QuestCycle.SameAnswers(plan.AnswersAfter,expectedAnswers) ||
            plan.SyncedQuestIds.Any(id => !scope.Contains(id)) ||
            !plan.AccountCompletedAfter.SequenceEqual(expected.AccountCompletedQuests.Concat(plan.SyncedQuestIds).Distinct().Order()) ||
            !QuestCycle.SameAnswers(plan.AccountAnswersAfter,expected.AccountQuestAnswers))
            throw new InvalidDataException("无奖励跳过规划必须保留原接取、完整范围及无关任务状态");
        byte slots=expected.EquipmentSlotFlags;
        foreach(var quest in plan.Interval.Concat(plan.AreaQuests).Where(quest=>plan.NewCompletedQuestIds.Contains(quest.Id)))
            slots=QuestCharacterReward.EquipmentSlots(expected with { EquipmentSlotFlags=slots },quest);
        if(plan.EquipmentSlotFlagsAfter!=slots) throw new InvalidDataException("槽位变化必须来自实际完成任务的合法奖励");
        if (plan.ProfessionGrowType is { } professionGrow)
        {
            var endpoints=plan.Interval.Concat(plan.AreaQuests).Where(quest=>quest.RewardType=="[awakening type]" &&
                plan.NewCompletedQuestIds.Contains(quest.Id)).Select(quest=>quest.Rewards[0].Value).ToHashSet();
            if ((professionGrow&15)!=(expected.GrowType&15) || (professionGrow>>4)<=(expected.GrowType>>4) || (professionGrow>>4)>2 ||
                !Enumerable.Range((expected.GrowType>>4)+1,(professionGrow>>4)-(expected.GrowType>>4)).All(endpoints.Contains) || !plan.TargetLearning.Initialized ||
                plan.TargetLearning.Experience!=expected.Learning.Experience ||
                plan.TargetLearning.BonusSp!=expected.Learning.BonusSp || plan.TargetLearning.BonusTp!=expected.Learning.BonusTp)
                throw new InvalidDataException("觉醒券必须保留本职业、经验及两树额外来源并逐级推进");
            plan.TargetLearning.Validate();
        }
        var inventory = plan.Inventory;
        var persistedInventory=inventory.After;
        ValidateInventoryPlan(expected,inventory);
        if (plan.Ticket is { } ticket)
        {
            var verified = QuestTicketSource.Create(expected,ticket.Item.Position,ticket.Definition,ticket.UtcSeconds);
            if (verified.Item != ticket.Item || !SameInventory(ticket.Inventory.After,new(verified.Inventory.After,verified.Inventory.After,"quest_ticket_comparison")))
                throw new InvalidDataException("任务券只能消费原实例的一张且保留其他资产");
        }
        if (plan.LevelUpTicket is { } levelUp)
        {
            var growth = levelUp.Growth;
            if (!ReferenceEquals(levelUp.Inventory.Before,expected.Inventory) || expected.Inventory.Find(levelUp.Item.Position)!=levelUp.Item ||
                levelUp.Item.Uid<=0 || levelUp.Item.Core.Count<1 || levelUp.Item.Core.EquipmentLockId!=0 ||
                levelUp.Item.Core.ItemId!=levelUp.Definition.TemplateId || !levelUp.Definition.Allows(expected.Level,expected.Job) ||
                growth.CharacterId!=expected.Id || growth.Revision!=expected.Revision || growth.Job!=expected.Job || growth.GrowType!=expected.GrowType ||
                growth.PreviousLevel!=expected.Level || growth.TargetLevel!=expected.Level+1 || growth.TargetLevel>CharacterGrowthCatalog.MaximumSupportedLevel ||
                growth.GainedExperience==0 || growth.GainedExperience>uint.MaxValue || !SameLearning(growth.Before,expected.Learning) ||
                !SameLearning(plan.TargetLearning,growth.Learning) || plan.ProfessionGrowType is not null)
                throw new InvalidDataException("升级券必须绑定原角色的一次成长、两树点数和消费计划");
        }
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var current = Require(db,tx,owner.Account,expected.Name);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id",expected.Id);
        command.Parameters.AddWithValue("$account",owner.Account);
        command.Parameters.AddWithValue("$operation",plan.OperationId.ToString("N"));
        command.CommandText = "SELECT parent_key,question_quest,before_revision,revision,plan_snapshot,operation_kind,acceptance_id FROM character_quest_skip_receipts WHERE character_id=$id AND account=$account AND operation_id=$operation";
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if ((reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0)) != plan.ParentKey ||
                    (reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1)) != plan.QuestionQuestId ||
                    reader.GetInt64(2) != expected.Revision || reader.GetInt64(3) != checked(expected.Revision+1) ||
                    reader.GetString(4) != plan.Snapshot || reader.GetString(5) != plan.OperationKind ||
                    (reader.IsDBNull(6) ? null : reader.GetString(6)) != (plan.IsPreparedSkip ? null : plan.AcceptanceId.ToString("N")))
                    throw new InvalidDataException("相同跳过接取身份的范围、答案或冻结规划发生变化");
                persistedInventory=RestoreQuestEffectInventory(db,tx,expected,plan.OperationId,inventory.After);
                return new QuestQuickClearCommit(current,plan,true,Matches(current,After()));
            }
        }
        if (!Matches(current,expected))
            throw new InvalidOperationException("跳过原角色、完整资产、活动任务或revision已变化");
        if (plan.Ticket is { } source)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (source.Item.Core.IsExpired(now)) throw new QuestTicketExpiredException();
            if (!source.Definition.Allows(current.Level,current.Job))
                throw new InvalidOperationException("任务券在提交前资格变化");
        }
        if (plan.LevelUpTicket is { } levelSource)
        {
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (levelSource.Inventory.ChangedSlots.Any(slot=>levelSource.Inventory.Before.Find(slot) is { } item && item.IsExpired(now)))
                throw new QuestTicketExpiredException();
        }
        command.Parameters.AddWithValue("$before",expected.Revision);
        if (inventory.Changed)
            persistedInventory=CommitInventoryInTransaction(db,tx,owner,expected,inventory).Inventory;
        else
        {
            command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$before";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("跳过revision已变化");
        }
        if (plan.IsLevelUpTicket)
        {
            command.Parameters.AddWithValue("$level",plan.TargetLevel);
            command.Parameters.AddWithValue("$experience",checked((long)plan.TargetLearning.Experience));
            command.CommandText="UPDATE characters SET level=$level,experience=$experience WHERE id=$id AND account=$account AND revision=$before+1";
            if (command.ExecuteNonQuery()!=1) throw new InvalidOperationException("升级券角色版本已变化");
            command.Parameters.RemoveAt("$level"); command.Parameters.RemoveAt("$experience");
        }
        if (plan.EquipmentSlotFlagsAfter != expected.EquipmentSlotFlags)
        {
            command.Parameters.AddWithValue("$slotsBefore",expected.EquipmentSlotFlags);
            command.Parameters.AddWithValue("$slotsAfter",plan.EquipmentSlotFlagsAfter);
            command.CommandText = "UPDATE characters SET equipment_slot_flags=$slotsAfter WHERE id=$id AND account=$account AND revision=$before+1 AND equipment_slot_flags=$slotsBefore";
            if (command.ExecuteNonQuery()!=1) throw new InvalidOperationException("指定券槽位状态已变化");
            command.Parameters.RemoveAt("$slotsBefore"); command.Parameters.RemoveAt("$slotsAfter");
        }
        if (plan.ProfessionGrowType is { } grow)
        {
            command.Parameters.AddWithValue("$growBefore",expected.GrowType);
            command.Parameters.AddWithValue("$growAfter",grow);
            command.CommandText="UPDATE characters SET grow_type=$growAfter WHERE id=$id AND account=$account AND revision=$before+1 AND grow_type=$growBefore";
            if (command.ExecuteNonQuery()!=1) throw new InvalidOperationException("觉醒券职业状态已变化");
            command.Parameters.RemoveAt("$growBefore"); command.Parameters.RemoveAt("$growAfter");
            WriteLearning(db,tx,expected.Id,plan.TargetLearning); WriteCanonicalSkills(db,tx,expected.Id,plan.TargetLearning);
        }
        else if(!SameLearning(plan.TargetLearning,expected.Learning))
        { WriteLearning(db,tx,expected.Id,plan.TargetLearning); WriteCanonicalSkills(db,tx,expected.Id,plan.TargetLearning); }
        foreach (var active in plan.RemovedActive)
        {
            command.Parameters.AddWithValue("$slot",active.Slot);
            command.Parameters.AddWithValue("$quest",active.QuestId);
            command.Parameters.AddWithValue("$removedAcceptance",active.AcceptanceId.ToString("N"));
            command.Parameters.AddWithValue("$scalar",(long)active.ConditionState);
            command.CommandText = "DELETE FROM character_active_main_quests WHERE character_id=$id AND slot=$slot AND quest_id=$quest AND acceptance_id=$removedAcceptance AND condition_state=$scalar";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("跳过不能原子移除原活动槽");
            foreach (string key in new[] { "$slot","$quest","$removedAcceptance","$scalar" }) command.Parameters.RemoveAt(key);
        }
        WriteQuestConditions(db,tx,expected.Id,expected.ActiveMainQuests,plan.ActiveAfter);
        foreach (int id in plan.NewCompletedQuestIds)
        {
            command.Parameters.AddWithValue("$quest",id);
            object answer = plan.IsPreparedSkip
                ? plan.AnswersAfter.TryGetValue(id,out byte choice) ? choice : DBNull.Value
                : id == plan.Question.Id ? plan.Answer : DBNull.Value;
            command.Parameters.AddWithValue("$answer",answer);
            command.CommandText = "INSERT INTO character_completed_quests(character_id,quest_id,daily_period,answer) VALUES($id,$quest,0,$answer)";
            command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$quest"); command.Parameters.RemoveAt("$answer");
        }
        WriteAccountSyncMarkers(db,tx,owner.Account,plan.SyncedQuestIds,plan.OperationId);
        command.Parameters.AddWithValue("$acceptance",plan.IsPreparedSkip ? DBNull.Value : plan.AcceptanceId.ToString("N"));
        command.Parameters.AddWithValue("$parent",plan.ParentKey is { } parent ? parent : DBNull.Value);
        command.Parameters.AddWithValue("$question",plan.QuestionQuestId is { } question ? question : DBNull.Value);
        command.Parameters.AddWithValue("$kind",plan.OperationKind);
        command.Parameters.AddWithValue("$revision",checked(expected.Revision+1));
        command.Parameters.AddWithValue("$snapshot",plan.Snapshot);
        command.CommandText = """
            INSERT INTO character_quest_skip_receipts(character_id,account,acceptance_id,parent_key,question_quest,before_revision,revision,plan_snapshot,operation_id,operation_kind)
            VALUES($id,$account,$acceptance,$parent,$question,$before,$revision,$snapshot,$operation,$kind)
            """;
        command.ExecuteNonQuery();
        if(plan.Effects is { Changed:true } effects)
            WriteQuestEffectReceipt(db,tx,expected,plan.OperationId,inventory,persistedInventory);
        var committed = Require(db,tx,owner.Account,expected.Name);
        if (!Matches(committed,After())) throw new InvalidOperationException("持久无奖励跳过结果与完整规划不同");
        tx.Commit();
        return new QuestQuickClearCommit(committed,plan,false,true);

        Character After() => expected with { Revision = checked(expected.Revision+1), ActiveMainQuests = plan.ActiveAfter.ToArray(),
            CompletedQuests = plan.CompletedAfter.ToArray(), AccountCompletedQuests = plan.AccountCompletedAfter.ToArray(), QuestAnswers = plan.AnswersAfter, Inventory = persistedInventory,
            EquipmentSlotFlags=plan.EquipmentSlotFlagsAfter,GrowType=plan.ProfessionGrowType ?? expected.GrowType,
            Level=plan.TargetLevel,Learning=plan.TargetLearning,Skills=SameLearning(plan.TargetLearning,expected.Learning) ? expected.Skills :
                plan.TargetLearning.Trees.Single(row=>row.Context==0).Skills.Select(row=>new CharacterSkill(row.Id,row.Level)).OrderBy(row=>row.Id).ToArray() };

        bool Matches(Character actual,Character target) => actual.Id == target.Id && actual.Slot == target.Slot &&
            actual.Name == target.Name && actual.Revision == target.Revision && actual.Job == target.Job && actual.GrowType == target.GrowType &&
            actual.Level == target.Level && actual.EquipmentSlotFlags == target.EquipmentSlotFlags &&
            actual.AcceptedStoryLevel == target.AcceptedStoryLevel && actual.Position == target.Position && actual.Return == target.Return &&
            actual.DungeonState == target.DungeonState && actual.Skills.SequenceEqual(target.Skills) && SameLearning(actual.Learning,target.Learning) &&
            SameInventory(actual.Inventory,new(target.Inventory,target.Inventory,"quest_skip_comparison")) && actual.ActiveMainQuests.SequenceEqual(target.ActiveMainQuests) &&
            actual.CompletedQuests.SequenceEqual(target.CompletedQuests) && QuestCycle.Same(actual,target);
    });
}
