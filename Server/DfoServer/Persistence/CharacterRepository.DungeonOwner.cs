using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
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
        var state = expected.DungeonState ?? throw new InvalidOperationException("副本owner未初始化");
        // 修炼场没有静态演员时只推进尝试号；对象key保持高水位，仍禁止回绕。
        if (nextObjectKey < state.NextObjectKey || nextObjectKey > 65536 || state.LastAttempt == long.MaxValue)
            throw new InvalidDataException("副本对象key或尝试号耗尽");
        using var db = database.Open();
        using var tx = db.BeginTransaction(deferred: false);
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision || current.DungeonState != state ||
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
        command.Parameters.AddWithValue("$revision", expected.Revision);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副本准入revision冲突");
        command.Parameters.AddWithValue("$key", nextObjectKey);
        command.Parameters.AddWithValue("$oldKey", state.NextObjectKey);
        command.Parameters.AddWithValue("$oldAttempt", state.LastAttempt);
        command.CommandText = """
            UPDATE character_dungeon_state SET next_object_key=$key,last_attempt=last_attempt+1
            WHERE character_id=$id AND account=$account AND next_object_key=$oldKey AND last_attempt=$oldAttempt
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("副本身份预留冲突");
        var committed = Require(db, tx, owner.Account, expected.Name);
        tx.Commit();
        return committed;
    });
}
