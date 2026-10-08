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
        if (questId != 3145 || expected.CompletedQuests.Contains(questId))
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
        command.CommandText = """
            SELECT r.event_id,r.instance_id,r.task_attempt,r.epoch,r.revision
            FROM character_quest_clear_receipts r JOIN character_dungeon_growth_receipts g
            ON g.character_id=r.character_id AND g.event_id=r.event_id AND g.account=r.account
            WHERE r.character_id=$id AND r.account=$account AND r.acceptance_id=$acceptance
              AND r.quest_id=3145 AND r.quest_slot=$slot AND r.dungeon_id=3 AND r.map_id=76126
              AND g.instance_id=r.instance_id AND g.task_attempt=r.task_attempt AND g.quest_id=r.quest_id
              AND g.map_id=r.map_id AND g.epoch+1=r.epoch AND g.revision=r.revision
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        long revision = reader.GetInt64(4);
        if (revision > current.Revision)
            throw new InvalidDataException("持久清图revision晚于实际角色");
        return new QuestClearReceipt(active.AcceptanceId, questId, active.Slot, Guid.ParseExact(reader.GetString(0), "N"),
            Guid.ParseExact(reader.GetString(1), "N"), reader.GetInt64(2), reader.GetInt64(3), revision);
    });

    // 清图事实与末次死亡经验共用事务；C2S33及诊断状态不能进入此入口。
    private static void SaveOrVerifyQuestClear(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        SinglePlayerDungeonRun run, PreparedDungeonDeath prepared, Character current, bool verifyOnly)
    {
        var entry = run.Entry;
        // 没有正式持久接取身份的合成领域调用不推进生产任务。
        if (entry.QuestAcceptanceId == Guid.Empty)
            return;
        var clear = prepared.Result.ClearFact;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", current.Id);
        command.Parameters.AddWithValue("$event", prepared.Death.EventId.ToString("N"));
        command.CommandText = """
            SELECT account,acceptance_id,quest_id,quest_slot,instance_id,task_attempt,epoch,dungeon_id,map_id
            FROM character_quest_clear_receipts WHERE character_id=$id AND event_id=$event
            """;
        bool exists;
        using (var reader = command.ExecuteReader())
        {
            exists = reader.Read();
            if (exists && (clear is null || reader.GetString(0) != owner.Account ||
                reader.GetString(1) != entry.QuestAcceptanceId.ToString("N") || reader.GetInt64(2) != entry.QuestId ||
                reader.GetInt64(3) != entry.ActiveQuestSlot || reader.GetString(4) != clear.Identity.InstanceId.ToString("N") ||
                reader.GetInt64(5) != clear.Identity.TaskAttempt || reader.GetInt64(6) != clear.Identity.Epoch ||
                reader.GetInt64(7) != clear.DungeonId || reader.GetInt64(8) != clear.ClearMap?.Id))
                throw new InvalidDataException("清图重放的接取身份、实例或地图不一致");
        }
        if (clear is null || !exists && current.ActiveMainQuests.Any(quest =>
            quest.AcceptanceId == entry.QuestAcceptanceId && quest.QuestId == entry.QuestId && quest.ConditionState == 0))
            return;
        if (!QuestClearEligibility.TryValidate(entry, prepared))
            throw new InvalidDataException("3145清图需要同一正式实例76126末次可信死亡事实");
        if (verifyOnly)
        {
            if (!exists)
                throw new InvalidDataException("已提交的末次死亡缺少同事务清图账本");
            return;
        }
        if (exists)
            throw new InvalidDataException("新死亡不能覆盖已有清图账本");
        if (!QuestClearEligibility.MatchesActiveQuest(entry, current))
            throw new InvalidOperationException("原任务接取身份或清图前提已变化");

        command.Parameters.AddWithValue("$instance", clear.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt", clear.Identity.TaskAttempt);
        command.CommandText = """
            SELECT event_id FROM character_dungeon_growth_receipts
            WHERE character_id=$id AND instance_id=$instance AND task_attempt=$attempt AND quest_id=3145
            """;
        var committedDeaths = new HashSet<Guid>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                committedDeaths.Add(Guid.ParseExact(reader.GetString(0), "N"));
        }
        if (!committedDeaths.SetEquals(clear.ConfirmedDeathEvents))
            throw new InvalidDataException("清图事件集合与本实例已提交死亡不一致");
        command.Parameters.AddWithValue("$slot", entry.ActiveQuestSlot);
        command.Parameters.AddWithValue("$acceptance", entry.QuestAcceptanceId.ToString("N"));
        command.CommandText = """
            UPDATE character_active_main_quests SET condition_state=0
            WHERE character_id=$id AND slot=$slot AND quest_id=3145 AND condition_state=1 AND acceptance_id=$acceptance
            """;
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("原任务条件不能原子达成");
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$epoch", clear.Identity.Epoch);
        command.Parameters.AddWithValue("$generation", owner.Generation);
        command.Parameters.AddWithValue("$revision", checked(current.Revision + 1));
        command.CommandText = """
            INSERT INTO character_quest_clear_receipts(character_id,account,acceptance_id,quest_id,quest_slot,event_id,
              instance_id,task_attempt,epoch,dungeon_id,map_id,generation,revision)
            VALUES($id,$account,$acceptance,3145,$slot,$event,$instance,$attempt,$epoch,3,76126,$generation,$revision)
            """;
        command.ExecuteNonQuery();
    }
}
