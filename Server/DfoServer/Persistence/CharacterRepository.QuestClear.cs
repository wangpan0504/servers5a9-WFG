using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public QuestClearReceipt? LoadQuestClear(AccountOwners.Lease owner, Character expected, ushort questId) => owner.Execute(() =>
    {
        if (questId is 0 or >= 30000 || expected.CompletedQuests.Contains(questId))
            return null;
        var active = expected.ActiveMainQuests.SingleOrDefault(quest => quest.QuestId == questId);
        if (active is not { ConditionState: 0 } || active.AcceptanceId == Guid.Empty)
            return null;
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision ||
            !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) ||
            !current.CompletedQuests.SequenceEqual(expected.CompletedQuests))
            throw new InvalidOperationException("任务查询的角色revision或接取状态已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", current.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$acceptance", active.AcceptanceId.ToString("N"));
        command.Parameters.AddWithValue("$slot", active.Slot);
        command.Parameters.AddWithValue("$quest", questId);
        command.CommandText = """
            SELECT r.event_id,r.instance_id,r.task_attempt,r.epoch,r.revision,r.dungeon_id,r.map_id
            FROM character_quest_clear_receipts r JOIN character_dungeon_growth_receipts g
            ON g.character_id=r.character_id AND g.event_id=r.event_id AND g.account=r.account
            WHERE r.character_id=$id AND r.account=$account AND r.acceptance_id=$acceptance
              AND r.quest_id=$quest AND r.quest_slot=$slot
              AND g.instance_id=r.instance_id AND g.task_attempt=r.task_attempt
              AND g.map_id=r.map_id AND g.epoch+1=r.epoch AND g.revision=r.revision
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        long revision = reader.GetInt64(4);
        if (revision > current.Revision)
            throw new InvalidDataException("持久清图revision晚于实际角色");
        return new QuestClearReceipt(active.AcceptanceId, questId, active.Slot, Guid.ParseExact(reader.GetString(0), "N"),
            Guid.ParseExact(reader.GetString(1), "N"), reader.GetInt64(2), reader.GetInt64(3), revision) { DungeonId = reader.GetInt32(5), MapId = reader.GetInt32(6) };
    });

    // 清图事实与末次死亡经验共用事务；C2S33及诊断状态不能进入此入口。
    private void SaveOrVerifyQuestClear(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        SinglePlayerDungeonRun run, PreparedDungeonDeath prepared, Character current, bool verifyOnly)
    {
        var entry = run.Entry;
        var clear = prepared.Result.ClearFact;
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", current.Id);
        command.Parameters.AddWithValue("$event", prepared.Death.EventId.ToString("N"));
        command.CommandText = """
            SELECT account,acceptance_id,quest_id,quest_slot,instance_id,task_attempt,epoch,dungeon_id,map_id
            FROM character_quest_clear_receipts WHERE character_id=$id AND event_id=$event
            """;
        var saved = new HashSet<Guid>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                Guid acceptance = Guid.ParseExact(reader.GetString(1), "N");
                if (clear is null || reader.GetString(0) != owner.Account || !entry.QuestAcceptances.Any(row =>
                    row.AcceptanceId == acceptance && row.QuestId == reader.GetInt32(2) && row.Slot == reader.GetInt32(3)) ||
                    reader.GetString(4) != clear.Identity.InstanceId.ToString("N") || reader.GetInt64(5) != clear.Identity.TaskAttempt ||
                    reader.GetInt64(6) != clear.Identity.Epoch || reader.GetInt32(7) != clear.DungeonId || reader.GetInt32(8) != clear.ClearMap?.Id)
                    throw new InvalidDataException("清图重放的接取身份、实例或地图不一致");
                saved.Add(acceptance);
            }
        }
        if (clear is null) return;
        // 接取身份在入场时冻结；资源缺失或玩法未支持不产生完成事实。
        var targets = current.ActiveMainQuests.Where(row => row.ConditionState > 0 && QuestHuntProgress.Bound(row, entry) &&
            quests is not null && quests.TryGet(row.QuestId, out var quest) && QuestCoverage.HasOrdinaryDeclarations(quest) &&
            (QuestClearEligibility.IsSingleMap(quest) && row.ConditionState == 1 && quest.IntData[0] == clear.ClearMap?.Id &&
                (quest.DungeonIndex is null or < 0 || quest.DungeonIndex == entry.DungeonId) ||
             QuestEligibility.InspectResource(quest).Entry is QuestResourceEntry.Ordinary or QuestResourceEntry.Dialogue &&
                QuestDungeonClearProgress.Matches(quest,entry,run) && QuestDungeonClearProgress.ValidScalar(quest,row.ConditionState))).ToArray();
        if (targets.Length == 0 && saved.Count == 0) return;
        if (!QuestClearEligibility.TryValidate(entry, prepared))
            throw new InvalidDataException("任务清图需要同一正式实例末次可信死亡事实");
        if (verifyOnly)
        {
            // 中间次数只有growth账本和持久scalar；最终归零才产生可领奖清图凭据。
            if (targets.Any(row => quests!.TryGet(row.QuestId,out var quest) && QuestClearEligibility.IsSingleMap(quest) &&
                !saved.Contains(row.AcceptanceId)))
                throw new InvalidDataException("已提交的末次死亡缺少同事务清图账本");
            return;
        }
        if (saved.Count != 0) throw new InvalidDataException("新死亡不能覆盖已有清图账本");
        command.Parameters.AddWithValue("$instance", clear.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", clear.Identity.TaskAttempt);
        command.CommandText = """
            SELECT event_id FROM character_dungeon_growth_receipts
            WHERE character_id=$id AND instance_id=$instance AND task_attempt=$attempt
            """;
        var committedDeaths = new HashSet<Guid>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) committedDeaths.Add(Guid.ParseExact(reader.GetString(0), "N"));
        if (!committedDeaths.SetEquals(clear.ConfirmedDeathEvents))
            throw new InvalidDataException("清图事件集合与本实例已提交死亡不一致");
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$epoch", clear.Identity.Epoch);
        command.Parameters.AddWithValue("$generation", owner.Generation);
        command.Parameters.AddWithValue("$revision", checked(current.Revision + 1));
        command.Parameters.AddWithValue("$dungeon", clear.DungeonId);
        command.Parameters.AddWithValue("$map", clear.ClearMap!.Id);
        command.Parameters.AddWithValue("$slot", 0); command.Parameters.AddWithValue("$quest", 0);
        command.Parameters.AddWithValue("$before", 0L); command.Parameters.AddWithValue("$remaining", 0L);
        command.Parameters.AddWithValue("$acceptance", "");
        foreach (var target in targets)
        {
            command.Parameters["$slot"].Value = target.Slot; command.Parameters["$quest"].Value = target.QuestId;
            command.Parameters["$acceptance"].Value = target.AcceptanceId.ToString("N");
            command.Parameters["$before"].Value = (long)target.ConditionState;
            command.Parameters["$remaining"].Value = (long)target.ConditionState - 1;
            command.CommandText = """
                UPDATE character_active_main_quests SET condition_state=$remaining
                WHERE character_id=$id AND slot=$slot AND quest_id=$quest AND condition_state=$before AND acceptance_id=$acceptance
                """;
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("原任务条件不能原子达成");
            if (target.ConditionState != 1) continue;
            command.CommandText = """
                INSERT INTO character_quest_clear_receipts(character_id,account,acceptance_id,quest_id,quest_slot,event_id,
                  instance_id,task_attempt,epoch,dungeon_id,map_id,generation,revision)
                VALUES($id,$account,$acceptance,$quest,$slot,$event,$instance,$attempt,$epoch,$dungeon,$map,$generation,$revision)
                """;
            command.ExecuteNonQuery();
        }
    }
}
