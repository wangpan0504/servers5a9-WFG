using DfoServer.Game.Accounts;
using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static CharacterDungeonState? ReadDungeonState(SqliteConnection db, ushort id, SqliteTransaction? tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT s.account,s.next_object_key,s.last_attempt,s.capsule_experience,s.proficiency_level,
              s.proficiency_experience,c.account FROM character_dungeon_state s
            JOIN characters c ON c.id=s.character_id WHERE s.character_id=$id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(0) != reader.GetString(6))
            throw new InvalidDataException("副本owner账号归属损坏");
        var state = new CharacterDungeonState(checked((uint)reader.GetInt64(1)), reader.GetInt64(2),
            checked((uint)reader.GetInt64(3)), checked((uint)reader.GetInt64(4)), checked((ulong)reader.GetInt64(5)));
        state.Validate();
        return state;
    }

    static void CreateDungeonState(SqliteConnection db, SqliteTransaction tx, string account, ushort id, CharacterDungeonState? state)
    {
        if (state is null) return;
        state.Validate();
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_dungeon_state(character_id,account,next_object_key,last_attempt,
              capsule_experience,proficiency_level,proficiency_experience)
            VALUES($id,$account,$key,$attempt,$capsule,$level,$experience)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$key", state.NextObjectKey);
        command.Parameters.AddWithValue("$attempt", state.LastAttempt);
        command.Parameters.AddWithValue("$capsule", state.GrowthCapsuleExperience);
        command.Parameters.AddWithValue("$level", state.ProficiencyLevel);
        command.Parameters.AddWithValue("$experience", checked((long)state.ProficiencyExperience));
        command.ExecuteNonQuery();
    }

    public Character ReserveDungeonAttempt(AccountOwners.Lease owner, Character expected, uint nextObjectKey) => owner.Execute(() =>
    {
        var state = ReservationState(expected, nextObjectKey);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ReserveDungeonAttempt");
        using var tx = write.Transaction;
        ReserveInTransaction(db, tx, owner, expected, expected.Revision, state, nextObjectKey);
        var committed = Require(db, tx, owner.Account, expected.Name);
        tx.Commit();
        return committed;
    });

    // #510/#511 入场票、入场一次性疲劳与尝试号预留同一写事务：任一步失败整笔回滚，重复请求或提交失败都不会只扣一半。
    public Character CommitDungeonEntry(AccountOwners.Lease owner, Character expected, uint nextObjectKey, DungeonEntryCharge charge) => owner.Execute(() =>
    {
        if (charge.IsEmpty) throw new InvalidOperationException("入场没有费用时使用普通尝试号预留");
        var state = ReservationState(expected, nextObjectKey);
        var identity = charge.Identity;
        if (identity.CharacterId != expected.Id || identity.InstanceId == Guid.Empty || identity.Epoch <= 0 ||
            identity.TaskAttempt != state.LastAttempt + 1 || charge.MapId <= 0)
            throw new InvalidDataException("入场费用需要本次待安装实例的身份");
        CharacterFatigueState? fatigueBefore = null, fatigueAfter = null;
        if (charge.Fatigue > 0)
        {
            fatigueBefore = expected.Fatigue ?? throw new InvalidOperationException("入场疲劳需要已初始化的疲劳owner");
            fatigueAfter = fatigueBefore.ConsumeAtEntry(charge.Fatigue);
        }
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitDungeonEntry");
        using var tx = write.Transaction;
        long revision = charge.Items is { } items ? CommitInventoryInTransaction(db, tx, owner, expected, items).Revision : expected.Revision;
        ReserveInTransaction(db, tx, owner, expected, revision, state, nextObjectKey, fatigueBefore);
        if (fatigueBefore is not null && fatigueAfter is not null)
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$used", fatigueAfter.Used);
            command.Parameters.AddWithValue("$remaining", fatigueAfter.Remaining);
            command.CommandText = "UPDATE character_fatigue SET used=$used,remaining=$remaining WHERE character_id=$id AND account=$account";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("入场疲劳缺少原疲劳owner");
            command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
            command.Parameters.AddWithValue("$attempt", identity.TaskAttempt);
            command.Parameters.AddWithValue("$map", charge.MapId);
            command.Parameters.AddWithValue("$epoch", identity.Epoch);
            command.Parameters.AddWithValue("$generation", owner.Generation);
            command.Parameters.AddWithValue("$rule", CharacterFatigueState.StartDungeonRuleId);
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$before", JsonSerializer.Serialize(fatigueBefore));
            command.Parameters.AddWithValue("$after", JsonSerializer.Serialize(fatigueAfter));
            command.CommandText = """
                INSERT INTO character_dungeon_fatigue_receipts(character_id,account,instance_id,task_attempt,map_id,epoch,generation,
                  rule_id,before_revision,revision,before_state,after_state)
                VALUES($id,$account,$instance,$attempt,$map,$epoch,$generation,$rule,$revision,$revision+1,$before,$after)
                """;
            command.ExecuteNonQuery();
        }
        var committed = Require(db, tx, owner.Account, expected.Name);
        tx.Commit();
        return committed;
    });

    static CharacterDungeonState ReservationState(Character expected, uint nextObjectKey)
    {
        var state = expected.DungeonState ?? throw new InvalidOperationException("副本owner未初始化");
        // 修炼场没有静态演员时只推进尝试号；对象key保持高水位，仍禁止回绕。
        if (nextObjectKey < state.NextObjectKey || nextObjectKey > 65536 || state.LastAttempt == long.MaxValue)
            throw new InvalidDataException("副本对象key或尝试号耗尽");
        return state;
    }

    // 只参与调用方事务：复核准入前提（revision 允许已被同事务的入场票提交推进到 revision），推进 revision 与尝试号。
    void ReserveInTransaction(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, Character expected, long revision,
        CharacterDungeonState state, uint nextObjectKey, CharacterFatigueState? fatigue = null)
    {
        var current = Require(db, tx, owner.Account, expected.Name);
        if (fatigue is not null && current.Fatigue != fatigue)
            throw new InvalidOperationException("入场疲劳的原owner已变化");
        if (current.Id != expected.Id || current.Revision != revision || current.DungeonState != state ||
            current.Job != expected.Job || current.GrowType != expected.GrowType || current.Level != expected.Level ||
            current.Position != expected.Position || !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) ||
            !current.CompletedQuests.SequenceEqual(expected.CompletedQuests) || !SameLearning(current.Learning, expected.Learning))
            throw new InvalidOperationException("副本准入的角色或任务前提已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision
            """;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副本准入revision冲突");
        command.Parameters.AddWithValue("$key", nextObjectKey);
        command.Parameters.AddWithValue("$oldKey", state.NextObjectKey);
        command.Parameters.AddWithValue("$oldAttempt", state.LastAttempt);
        command.CommandText = """
            UPDATE character_dungeon_state SET next_object_key=$key,last_attempt=last_attempt+1
            WHERE character_id=$id AND account=$account AND next_object_key=$oldKey AND last_attempt=$oldAttempt
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副本身份预留冲突");
    }
}
