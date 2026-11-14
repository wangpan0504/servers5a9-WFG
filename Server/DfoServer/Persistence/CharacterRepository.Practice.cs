using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Character ResetPracticeDay(AccountOwners.Lease owner, Character expected, int day) => owner.Execute(() =>
    {
        if (day is < 1 or > 3652058) throw new ArgumentOutOfRangeException(nameof(day));
        if (expected.DungeonState is not { } before || day <= before.PracticeDay) return expected;
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ResetPracticeDay", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision || current.DungeonState != before)
            throw new InvalidOperationException("练习日切的角色快照已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$oldDay", before.PracticeDay);
        command.Parameters.AddWithValue("$oldUsed", before.PracticeUsed);
        command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("练习日切revision冲突");
        command.CommandText = """
            UPDATE character_dungeon_state SET practice_day=$day,practice_used=0
            WHERE character_id=$id AND account=$account AND practice_day=$oldDay AND practice_used=$oldUsed
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("练习日切状态已变化");
        var committed = Require(db, tx, owner.Account, expected.Name);
        tx.Commit();
        return committed;
    });

    // 只参与原入场事务；原state/revision验证及attempt预留成功后才写，不另开事务。
    static DungeonPracticeTransition CommitPracticeEntry(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, Character expected, DungeonPracticeAdmission admission)
    {
        admission.RequireCurrentDay();
        var before = expected.DungeonState ?? throw new InvalidOperationException("练习缺少原副本owner");
        var transition = admission.Transition(before);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$oldDay", before.PracticeDay);
        command.Parameters.AddWithValue("$oldUsed", before.PracticeUsed);
        command.Parameters.AddWithValue("$day", transition.Day);
        command.Parameters.AddWithValue("$used", transition.AfterUsed);
        command.Parameters.AddWithValue("$attempt", checked(before.LastAttempt + 1));
        command.CommandText = """
            UPDATE character_dungeon_state SET practice_day=$day,practice_used=$used
            WHERE character_id=$id AND account=$account AND practice_day=$oldDay AND practice_used=$oldUsed
              AND last_attempt=$attempt
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("练习提交的次数或尝试号已变化");
        return transition;
    }
}
