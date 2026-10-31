using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public AntonNormalProgress LoadAntonNormalProgress(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadAntonNormalProgress(db, null, owner.Account, characterId);
    });

    static AntonNormalProgress ReadAntonNormalProgress(SqliteConnection db, SqliteTransaction? tx, string account, ushort id)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT day,branch,progress FROM character_anton_normal_progress WHERE character_id=$id AND account=$account";
        query.Parameters.AddWithValue("$id", id);
        query.Parameters.AddWithValue("$account", account);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)) : default;
    }

    static void WriteAntonNormalProgress(SqliteConnection db, SqliteTransaction tx, string account, ushort id, AntonNormalProgress progress)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_anton_normal_progress(character_id,account,day,branch,progress) VALUES($id,$account,$day,$branch,$progress)
            ON CONFLICT(character_id) DO UPDATE SET day=excluded.day,branch=excluded.branch,progress=excluded.progress
            WHERE character_anton_normal_progress.account=excluded.account AND character_anton_normal_progress.day<=excluded.day
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$day", progress.Day);
        command.Parameters.AddWithValue("$branch", progress.Branch);
        command.Parameters.AddWithValue("$progress", progress.Completed);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("普通安徒恩进度归属或业务日已变化");
    }

    static void AdmitAntonNormal(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, Character expected, DungeonEntryCharge charge)
    {
        if (charge.AntonNormalEntry is not { } entry) return;
        if (entry.AntonNormal is not { } admission || !AntonNormalProgress.Applies(entry) || entry.CharacterId != expected.Id ||
            entry.CharacterRevision != checked(expected.Revision + 1) || entry.DungeonId != charge.DungeonId ||
            entry.StartMap.Id != charge.MapId || entry.QuestId != charge.Identity.QuestId)
            throw new InvalidDataException("普通安徒恩准入缺少当前角色原资源计划");
        var before = ReadAntonNormalProgress(db, tx, owner.Account, expected.Id);
        if (!before.Allows(admission, entry.DungeonId)) throw new InvalidOperationException("普通安徒恩前序或当日路线已变化");
        WriteAntonNormalProgress(db, tx, owner.Account, expected.Id,
            new(admission.Day, admission.Branch, before.Day == admission.Day ? before.Completed : 0));
    }

    static void ClearAntonNormal(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, SinglePlayerDungeonRun run)
    {
        if (run.Entry.AntonNormal is not { } admission) return;
        if (!AntonNormalProgress.Applies(run.Entry)) throw new InvalidDataException("普通安徒恩通关缺少正式资源身份");
        ushort id = run.Snapshot.Identity.CharacterId;
        var before = ReadAntonNormalProgress(db, tx, owner.Account, id);
        if (before.Day < admission.Day) throw new InvalidOperationException("普通安徒恩通关缺少已提交准入日");
        var after = before.Clear(admission, run.DungeonId);
        if (after != before) WriteAntonNormalProgress(db, tx, owner.Account, id, after);
    }
}
