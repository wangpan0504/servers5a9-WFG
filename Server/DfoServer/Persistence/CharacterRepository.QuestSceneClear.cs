using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 参与原生命周期事务，物件清图保留独立来源，不借用任何growth事件作为终末。
    void SaveOrVerifySceneClear(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        QuestLifecyclePlan plan, bool verifyOnly)
    {
        if (plan.SceneClear is not { } prepared) return;
        if (plan.SceneEvent is not { } scene || plan.SceneClearRun is not { } run ||
            !QuestClearEligibility.TryValidateScene(run,prepared,scene) || plan.Action != QuestLifecycleAction.Condition)
            throw new InvalidDataException("场景清图需要原Run的冻结物件终末与生命周期事务");
        var clear = prepared.Result.ClearFact!; var expected = plan.Expected; var entry = run.Entry;
        // 普通0保留原Run身份；路线和本次实际生命周期任务各自核对原接取三元组。
        if (expected.Id != entry.CharacterId ||
            !expected.ActiveMainQuests.Any(row => row.QuestId == entry.RouteQuestId && QuestHuntProgress.Bound(row,entry)) ||
            !expected.ActiveMainQuests.Any(row => row.QuestId == plan.QuestId && row.AcceptanceId == plan.AcceptanceId &&
                QuestHuntProgress.Bound(row,entry)))
            throw new InvalidDataException("场景清图的原路线或实际任务接取身份已变化");
        if (!verifyOnly && (!run.IsPendingPassiveClear(prepared) || expected.DungeonState?.LastAttempt != clear.Identity.TaskAttempt))
            throw new InvalidOperationException("场景清图的原实例尝试或准备已变化");
        byte[] deaths = clear.ConfirmedDeathEvents.SelectMany(value => value.ToByteArray()).ToArray();
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id",expected.Id); command.Parameters.AddWithValue("$account",owner.Account);
        command.Parameters.AddWithValue("$operation",scene.OperationId.ToString("N"));
        command.Parameters.AddWithValue("$instance",clear.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt",clear.Identity.TaskAttempt);
        command.CommandText = """
            SELECT event_id FROM character_dungeon_growth_receipts
            WHERE character_id=$id AND account=$account AND instance_id=$instance AND task_attempt=$attempt
            """;
        var committedDeaths = new HashSet<Guid>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) committedDeaths.Add(Guid.ParseExact(reader.GetString(0),"N"));
        if (!committedDeaths.SetEquals(clear.ConfirmedDeathEvents))
            throw new InvalidDataException("场景清图的真实死亡集合与本实例已提交growth不一致");
        command.Parameters.AddWithValue("$quest",clear.Identity.QuestId);
        command.Parameters.AddWithValue("$beforeEpoch",scene.Identity.Epoch); command.Parameters.AddWithValue("$epoch",clear.Identity.Epoch);
        command.Parameters.AddWithValue("$dungeon",clear.DungeonId); command.Parameters.AddWithValue("$map",scene.MapId);
        command.Parameters.AddWithValue("$template",scene.TemplateId); command.Parameters.AddWithValue("$ordinal",scene.SourceOrdinal);
        command.Parameters.AddWithValue("$fingerprint",scene.Fingerprint); command.Parameters.AddWithValue("$deaths",deaths);
        command.Parameters.AddWithValue("$generation",owner.Generation); command.Parameters.AddWithValue("$revision",checked(expected.Revision+1));
        if (!verifyOnly)
        {
            command.CommandText = """
                INSERT INTO character_dungeon_scene_clear_receipts(character_id,account,operation_id,source_type,instance_id,quest_id,
                  task_attempt,before_epoch,epoch,dungeon_id,map_id,template_id,source_ordinal,fingerprint,death_events,generation,revision)
                VALUES($id,$account,$operation,'PassiveObject',$instance,$quest,$attempt,$beforeEpoch,$epoch,$dungeon,$map,
                  $template,$ordinal,$fingerprint,$deaths,$generation,$revision)
                """;
            command.ExecuteNonQuery();
        }
        else
        {
            command.CommandText = """
                SELECT count(*) FROM character_dungeon_scene_clear_receipts
                WHERE character_id=$id AND account=$account AND operation_id=$operation AND source_type='PassiveObject'
                  AND instance_id=$instance AND quest_id=$quest AND task_attempt=$attempt AND before_epoch=$beforeEpoch
                  AND epoch=$epoch AND dungeon_id=$dungeon AND map_id=$map AND template_id=$template AND source_ordinal=$ordinal
                  AND fingerprint=$fingerprint AND death_events=$deaths AND revision=$revision
                """;
            if (Convert.ToInt64(command.ExecuteScalar()) != 1)
                throw new InvalidDataException("场景清图重放的原来源或冻结结果发生变化");
        }
        foreach (var before in expected.ActiveMainQuests.Where(row => row.ConditionState > 0 && QuestHuntProgress.Bound(row,entry)))
        {
            if (quests is null || !quests.TryGet(before.QuestId,out var quest) ||
                !(QuestClearEligibility.IsSingleMap(quest) || QuestDungeonClearProgress.TryTarget(quest,out _))) continue;
            var after = plan.ActiveAfter.Single(row => row.AcceptanceId == before.AcceptanceId);
            if (after.ConditionState == before.ConditionState) continue;
            if (after.ConditionState != before.ConditionState-1)
                throw new InvalidDataException("场景清图只能减少本次绑定任务的一次完整通关");
            if (after.ConditionState != 0) continue;
            command.Parameters["$quest"].Value = before.QuestId;
            command.Parameters.AddWithValue("$slot",before.Slot); command.Parameters.AddWithValue("$acceptance",before.AcceptanceId.ToString("N"));
            command.CommandText = verifyOnly ? """
                SELECT count(*) FROM character_quest_clear_receipts
                WHERE character_id=$id AND account=$account AND acceptance_id=$acceptance AND quest_id=$quest AND quest_slot=$slot
                  AND event_id=$operation AND source_type='PassiveObject' AND growth_event_id IS NULL AND scene_operation_id=$operation
                  AND instance_id=$instance AND task_attempt=$attempt AND epoch=$epoch AND dungeon_id=$dungeon AND map_id=$map AND revision=$revision
                """ : """
                INSERT INTO character_quest_clear_receipts(character_id,account,acceptance_id,quest_id,quest_slot,event_id,
                  instance_id,task_attempt,epoch,dungeon_id,map_id,generation,revision,source_type,growth_event_id,scene_operation_id)
                VALUES($id,$account,$acceptance,$quest,$slot,$operation,$instance,$attempt,$epoch,$dungeon,$map,$generation,$revision,
                  'PassiveObject',NULL,$operation)
                """;
            if (verifyOnly && Convert.ToInt64(command.ExecuteScalar()) != 1)
                throw new InvalidDataException("场景清图重放缺少本次归零任务的领奖来源");
            if (!verifyOnly) command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$slot"); command.Parameters.RemoveAt("$acceptance");
        }
    }

    // 正式46在自己的原事务核对已提交场景来源，真实death集合和普通奖励继续沿原合同。
    static void VerifySettlementSceneClear(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, DungeonClearFact clear)
    {
        if (clear.PassiveSource is not { } source) return;
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id",clear.Identity.CharacterId); command.Parameters.AddWithValue("$account",owner.Account);
        command.Parameters.AddWithValue("$operation",source.OperationId.ToString("N"));
        command.Parameters.AddWithValue("$instance",clear.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt",clear.Identity.TaskAttempt); command.Parameters.AddWithValue("$epoch",clear.Identity.Epoch);
        command.Parameters.AddWithValue("$beforeEpoch",source.Identity.Epoch); command.Parameters.AddWithValue("$quest",clear.Identity.QuestId);
        command.Parameters.AddWithValue("$dungeon",clear.DungeonId); command.Parameters.AddWithValue("$map",clear.ClearMap?.Id ?? 0);
        command.Parameters.AddWithValue("$template",source.TemplateId); command.Parameters.AddWithValue("$ordinal",source.SourceOrdinal);
        command.Parameters.AddWithValue("$deaths",clear.ConfirmedDeathEvents.SelectMany(value => value.ToByteArray()).ToArray());
        command.CommandText = """
            SELECT count(*) FROM character_dungeon_scene_clear_receipts s JOIN character_quest_lifecycle_receipts l
              ON l.character_id=s.character_id AND l.operation_id=s.operation_id AND l.account=s.account
            WHERE s.character_id=$id AND s.account=$account AND s.operation_id=$operation AND s.source_type='PassiveObject'
              AND s.instance_id=$instance AND s.task_attempt=$attempt AND s.epoch=$epoch AND s.before_epoch=$beforeEpoch
              AND s.quest_id=$quest AND s.dungeon_id=$dungeon AND s.map_id=$map AND s.template_id=$template
              AND s.source_ordinal=$ordinal AND s.death_events=$deaths AND l.action='Condition' AND l.scene_event IS NOT NULL
              AND l.revision=s.revision
            """;
        if (source.Identity != (clear.Identity with { Epoch = source.Identity.Epoch }) || clear.Identity.Epoch != source.Identity.Epoch+1 ||
            Convert.ToInt64(command.ExecuteScalar()) != 1)
            throw new InvalidDataException("正式结算需要原实例已提交的物件清图来源");
        VerifySceneDeathLedger(db,tx,owner.Account,clear.Identity.CharacterId,source.OperationId,clear.Identity.InstanceId,
            clear.Identity.TaskAttempt);
    }

    static void VerifySceneDeathLedger(SqliteConnection db, SqliteTransaction tx, string account, ushort characterId,
        Guid operationId, Guid instanceId, long attempt)
    {
        using var command=db.CreateCommand(); command.Transaction=tx;
        command.Parameters.AddWithValue("$id",characterId); command.Parameters.AddWithValue("$account",account);
        command.Parameters.AddWithValue("$operation",operationId.ToString("N"));
        command.Parameters.AddWithValue("$instance",instanceId.ToString("N")); command.Parameters.AddWithValue("$attempt",attempt);
        command.CommandText = """
            SELECT s.quest_id,s.before_epoch,s.epoch,s.dungeon_id,s.map_id,s.template_id,s.source_ordinal,
              s.fingerprint,s.death_events,l.scene_event
            FROM character_dungeon_scene_clear_receipts s JOIN character_quest_lifecycle_receipts l
              ON l.character_id=s.character_id AND l.operation_id=s.operation_id AND l.account=s.account
            WHERE s.character_id=$id AND s.account=$account AND s.operation_id=$operation AND s.source_type='PassiveObject'
              AND s.instance_id=$instance AND s.task_attempt=$attempt AND l.action='Condition'
              AND l.revision=s.revision AND l.scene_event IS NOT NULL
            """;
        byte[] blob;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader[8] is not byte[] saved || saved.Length%16!=0)
                throw new InvalidDataException("场景清图缺少原生命周期及完整真实死亡集合");
            QuestSceneEvent scene;
            try
            {
                scene = JsonSerializer.Deserialize<QuestSceneEvent>(reader.GetString(9))
                    ?? throw new InvalidDataException("场景清图的原生命周期事件为空");
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("场景清图的原生命周期事件格式无效",error);
            }
            long beforeEpoch = reader.GetInt64(1);
            var identity = new DungeonRunIdentity(characterId,checked((uint)reader.GetInt64(0)),attempt,instanceId,beforeEpoch);
            if (scene.OperationId!=operationId || scene.Identity!=identity || beforeEpoch<=0 || beforeEpoch==long.MaxValue ||
                reader.GetInt64(2)!=beforeEpoch+1 || scene.DungeonId!=reader.GetInt32(3) || scene.MapId!=reader.GetInt32(4) ||
                scene.TemplateId!=reader.GetInt32(5) || scene.SourceOrdinal!=reader.GetInt32(6) || scene.Fingerprint!=reader.GetString(7))
                throw new InvalidDataException("场景清图的身份、地图、物件或摘要不是原生命周期事件");
            blob = saved;
        }
        var frozen=new HashSet<Guid>();
        for(int at=0;at<blob.Length;at+=16)
            if(!frozen.Add(new Guid(blob.AsSpan(at,16)))) throw new InvalidDataException("场景清图重复死亡事件");
        if(frozen.Contains(Guid.Empty)) throw new InvalidDataException("场景清图不能包含空死亡事件");
        command.CommandText="SELECT event_id FROM character_dungeon_growth_receipts WHERE character_id=$id AND account=$account AND instance_id=$instance AND task_attempt=$attempt";
        var committed=new HashSet<Guid>();
        using(var reader=command.ExecuteReader()) while(reader.Read()) committed.Add(Guid.ParseExact(reader.GetString(0),"N"));
        if(!committed.SetEquals(frozen)) throw new InvalidDataException("场景清图持久死亡集合与原实例growth不一致");
    }
}
