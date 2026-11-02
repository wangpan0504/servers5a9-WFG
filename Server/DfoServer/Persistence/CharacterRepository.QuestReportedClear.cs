using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    void SaveOrVerifyReportedClear(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        QuestLifecyclePlan plan, bool verifyOnly)
    {
        if (plan.ReportedClear is not { } receipt) return;
        var run = plan.ReportedClearRun ?? throw new InvalidDataException("任务统计缺少原Run");
        var expected = plan.Expected;
        var active = expected.ActiveMainQuests.Single(row => row.AcceptanceId == plan.AcceptanceId);
        var clear = run.ClearFact;
        if (quests is null || !quests.TryGet(plan.QuestId, out var quest) || !QuestDungeonClearProgress.NeedsClientCondition(quest) ||
            plan.Action != QuestLifecycleAction.Condition || plan.Inventory.Changed || active.ConditionState == 0 ||
            receipt.AcceptanceId != active.AcceptanceId || receipt.QuestId != active.QuestId || receipt.QuestSlot != active.Slot ||
            run.Phase != SinglePlayerDungeonPhase.Settlement || clear?.Identity.CharacterId != expected.Id ||
            clear.Identity.InstanceId != receipt.InstanceId || clear.Identity.TaskAttempt != receipt.TaskAttempt || clear.Identity.Epoch != receipt.Epoch ||
            clear.ClearMap?.Id != receipt.MapId || clear.DungeonId != receipt.DungeonId ||
            !clear.ConfirmedDeathEvents.Contains(receipt.EventId) || !QuestHuntProgress.Bound(active, run.Entry) ||
            !QuestDungeonClearProgress.MatchesScope(quest, run.Entry) ||
            plan.ActiveAfter.Single(row => row.AcceptanceId == active.AcceptanceId).ConditionState != active.ConditionState - 1)
            throw new InvalidDataException("统计通关不是原接取及已提交终末事实");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id); command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", receipt.EventId.ToString("N"));
        command.Parameters.AddWithValue("$instance", receipt.InstanceId.ToString("N")); command.Parameters.AddWithValue("$attempt", receipt.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", receipt.Epoch); command.Parameters.AddWithValue("$map", receipt.MapId);
        command.CommandText = "SELECT revision FROM character_dungeon_growth_receipts WHERE character_id=$id AND account=$account AND event_id=$event AND instance_id=$instance AND task_attempt=$attempt AND epoch+1=$epoch AND map_id=$map";
        if (command.ExecuteScalar() is not long sourceRevision || sourceRevision > expected.Revision)
            throw new InvalidDataException("统计通关缺少已经提交的原死亡账本");
        if (plan.ActiveAfter.Single(row => row.AcceptanceId == active.AcceptanceId).ConditionState != 0) return;
        command.Parameters.AddWithValue("$acceptance", active.AcceptanceId.ToString("N")); command.Parameters.AddWithValue("$quest", active.QuestId);
        command.Parameters.AddWithValue("$slot", active.Slot);
        // 清图凭据引用原growth的版本；本次统计报告自己的版本由生命周期回执保存。
        command.Parameters.AddWithValue("$revision", sourceRevision); command.Parameters.AddWithValue("$dungeon", receipt.DungeonId);
        command.Parameters.AddWithValue("$generation", owner.Generation);
        command.CommandText = verifyOnly ? """
            SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$id AND account=$account AND acceptance_id=$acceptance
              AND quest_id=$quest AND quest_slot=$slot AND event_id=$event AND instance_id=$instance AND task_attempt=$attempt
              AND epoch=$epoch AND revision=$revision AND dungeon_id=$dungeon AND map_id=$map AND source_type='MonsterDeath'
            """ : """
            INSERT INTO character_quest_clear_receipts(character_id,account,acceptance_id,quest_id,quest_slot,event_id,instance_id,task_attempt,epoch,revision,dungeon_id,map_id,generation,source_type,growth_event_id,scene_operation_id)
            VALUES($id,$account,$acceptance,$quest,$slot,$event,$instance,$attempt,$epoch,$revision,$dungeon,$map,$generation,'MonsterDeath',$event,NULL)
            """;
        if (verifyOnly ? Convert.ToInt64(command.ExecuteScalar()) != 1 : command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("统计通关回执未保全本次来源");
    }
}
